#include "NativeThreads.h"
#include "Log.h"
#include "Modules.h"
#include "ProcUtils.h"

#include <atomic>
#include <cerrno>
#include <csignal>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <dirent.h>
#include <dlfcn.h>
#include <pthread.h>
#include <sys/syscall.h>
#include <sys/ucontext.h>
#include <unistd.h>

namespace anrwatchdog
{
    namespace
    {
        // Deliberately not SIGUSR1/SIGUSR2 - Mono uses those for its own thread bookkeeping, and a
        // handler installed here would race with it. Real-time signals above the ones bionic
        // reserves are free for application use.
        int CaptureSignal()
        {
            return SIGRTMIN + 4;
        }

        constexpr size_t kMaxFrames = 128;
        constexpr size_t kMaxThreads = 256;
        constexpr int kPollIntervalUs = 200;

        // One slot per thread being captured. A thread that was given up on can still run its
        // handler much later - with a slot of its own it writes where nobody is looking any more,
        // instead of into the stack currently being captured.
        //
        // The table is statically allocated and never freed or resized for the same reason: a late
        // handler must never find a dangling pointer. Untouched slots stay in untouched BSS pages.
        struct CaptureSlot
        {
            std::atomic<pid_t> tid;
            std::atomic<size_t> count;
            std::atomic<bool> done;
            uintptr_t frames[kMaxFrames];
        };

        CaptureSlot s_Slots[kMaxThreads];
        std::atomic<size_t> s_SlotCount{0};
        std::atomic<bool> s_HandlerInstalled{false};

        // How far above the interrupted stack pointer a frame pointer may still be believed. Stacks
        // are contiguous, so this keeps a garbage chain from being dereferenced into a fault.
        constexpr uintptr_t kMaxStackSpan = 8 * 1024 * 1024;

        struct Registers
        {
            uintptr_t pc;
            uintptr_t lr;
            uintptr_t fp;
            uintptr_t sp;
        };

        pid_t GetCurrentThreadId()
        {
            return static_cast<pid_t>(syscall(SYS_gettid));
        }

        bool ReadRegisters(const ucontext_t* context, Registers& registers)
        {
#if defined(__aarch64__)
            registers.pc = context->uc_mcontext.pc;
            registers.lr = context->uc_mcontext.regs[30];
            registers.fp = context->uc_mcontext.regs[29];
            registers.sp = context->uc_mcontext.sp;
            return true;
#elif defined(__arm__)
            registers.pc = context->uc_mcontext.arm_pc;
            registers.lr = context->uc_mcontext.arm_lr;
            registers.fp = context->uc_mcontext.arm_fp;
            registers.sp = context->uc_mcontext.arm_sp;
            return true;
#elif defined(__x86_64__)
            registers.pc = static_cast<uintptr_t>(context->uc_mcontext.gregs[REG_RIP]);
            registers.lr = 0;
            registers.fp = static_cast<uintptr_t>(context->uc_mcontext.gregs[REG_RBP]);
            registers.sp = static_cast<uintptr_t>(context->uc_mcontext.gregs[REG_RSP]);
            return true;
#elif defined(__i386__)
            registers.pc = static_cast<uintptr_t>(context->uc_mcontext.gregs[REG_EIP]);
            registers.lr = 0;
            registers.fp = static_cast<uintptr_t>(context->uc_mcontext.gregs[REG_EBP]);
            registers.sp = static_cast<uintptr_t>(context->uc_mcontext.gregs[REG_ESP]);
            return true;
#else
            (void)context;
            (void)registers;
            return false;
#endif
        }

        /// Walks the frame pointer chain of the interrupted thread, starting from the registers the
        /// kernel saved when it delivered the signal.
        ///
        /// _Unwind_Backtrace would give richer stacks, but it cannot be used here: it walks the
        /// handler's own stack, and crossing the signal trampoline into a frame without unwind
        /// information - the vdso, hand written assembly, JIT code - faults inside the unwinder
        /// instead of stopping. Reading memory is the only thing that is safe in this context.
        size_t WalkFramePointers(const ucontext_t* context, uintptr_t* frames, size_t capacity)
        {
            Registers registers;
            if (context == nullptr || !ReadRegisters(context, registers))
                return 0;

            size_t count = 0;

            // Starting at the interrupted instruction rather than inside the handler, so the top
            // of the stack is where the thread actually was.
            if (registers.pc != 0 && count < capacity)
                frames[count++] = registers.pc;

            // A leaf function may not have set up a frame yet, so on architectures with a link
            // register its caller is taken from there.
            if (registers.lr != 0 && count < capacity)
                frames[count++] = registers.lr;

            uintptr_t fp = registers.fp;
            const uintptr_t lowest = registers.sp;
            const uintptr_t highest = registers.sp + kMaxStackSpan;

            while (count < capacity && fp >= lowest && fp < highest && (fp & (sizeof(uintptr_t) - 1)) == 0)
            {
                const auto* frame = reinterpret_cast<const uintptr_t*>(fp);
                const uintptr_t callerFp = frame[0];
                const uintptr_t returnAddress = frame[1];

                if (returnAddress == 0)
                    break;

                frames[count++] = returnAddress;

                // The chain has to move up the stack; anything else is garbage or a loop.
                if (callerFp <= fp)
                    break;

                fp = callerFp;
            }

            return count;
        }

        /// The target thread is interrupted mid-ANR and very likely holds the malloc lock, so this
        /// must not allocate, take a lock, or call anything that might - the unwinder included. It
        /// reads registers and stack memory into its own slot and nothing else; symbolication
        /// happens on the watchdog thread afterwards.
        void CaptureSignalHandler(int /*signum*/, siginfo_t* /*info*/, void* context)
        {
            const pid_t self = GetCurrentThreadId();
            const size_t slotCount = s_SlotCount.load(std::memory_order_acquire);

            for (size_t i = 0; i < slotCount; i++)
            {
                CaptureSlot& slot = s_Slots[i];
                if (slot.tid.load(std::memory_order_acquire) != self)
                    continue;

                // Already captured: a duplicate or late signal must not overwrite the stack that
                // was collected and read.
                if (slot.done.load(std::memory_order_acquire))
                    return;

                const size_t count = WalkFramePointers(static_cast<const ucontext_t*>(context), slot.frames, kMaxFrames);

                slot.count.store(count, std::memory_order_release);
                slot.done.store(true, std::memory_order_release);
                return;
            }
        }

        bool InstallSignalHandler()
        {
            // Installed once and never restored. A thread that was stuck in an uninterruptible
            // syscall can be handed the signal long after the capture gave up on it, and by then
            // the default action for a real-time signal is to kill the process.
            if (s_HandlerInstalled.load(std::memory_order_acquire))
                return true;

            struct sigaction action;
            memset(&action, 0, sizeof(action));
            action.sa_sigaction = CaptureSignalHandler;
            action.sa_flags = SA_SIGINFO | SA_RESTART | SA_ONSTACK;
            sigemptyset(&action.sa_mask);

            if (sigaction(CaptureSignal(), &action, nullptr) == -1)
            {
                ANR_LOG_ERROR("Error setting up signal handler for signal %d: %s", CaptureSignal(), strerror(errno));
                return false;
            }

            s_HandlerInstalled.store(true, std::memory_order_release);
            return true;
        }

        bool SendSignalToThread(pid_t tid)
        {
            // tkill takes the kernel thread id, which is what /proc/self/task gives us.
            if (syscall(SYS_tkill, tid, CaptureSignal()) == -1)
            {
                // ESRCH simply means the thread exited between enumeration and now.
                if (errno != ESRCH)
                    ANR_LOG_ERROR("Error sending signal to kernel thread (tid %d), %s", tid, strerror(errno));
                return false;
            }
            return true;
        }

        int64_t NowMs()
        {
            timespec now;
            if (clock_gettime(CLOCK_MONOTONIC, &now) == -1)
                return 0;
            return static_cast<int64_t>(now.tv_sec) * 1000 + now.tv_nsec / 1000000;
        }

        /// Waits for one slot to be filled. Polling rather than a semaphore: a post from a thread
        /// that timed out earlier would otherwise be consumed by whoever is waiting now, and read
        /// as an answer from the wrong thread.
        bool WaitForSlot(const CaptureSlot& slot, int timeoutMs)
        {
            const int64_t deadline = NowMs() + timeoutMs;

            while (!slot.done.load(std::memory_order_acquire))
            {
                if (NowMs() >= deadline)
                    return false;
                usleep(kPollIntervalUs);
            }

            return true;
        }

        std::vector<NativeThread> EnumerateThreads(pid_t selfTid)
        {
            std::vector<NativeThread> threads;

            DIR* dir = opendir("/proc/self/task");
            if (dir == nullptr)
            {
                ANR_LOG_ERROR("opendir '/proc/self/task' failed: %s", strerror(errno));
                return threads;
            }

            for (dirent* entry = readdir(dir); entry != nullptr; entry = readdir(dir))
            {
                if (entry->d_type != DT_DIR)
                    continue;

                pid_t tid = static_cast<pid_t>(atoi(entry->d_name));
                if (tid <= 0 || tid == selfTid)
                    continue;

                if (threads.size() >= kMaxThreads)
                {
                    ANR_LOG_ERROR("More than %zu threads, the rest are reported without stacks", kMaxThreads);
                    break;
                }

                NativeThread thread;
                thread.id = tid;
                thread.name = GetThreadName(tid);
                thread.state = GetKeyValueFromThreadStatus("State", GetThreadStatus(tid));
                thread.priority = GetThreadPriority(tid);
                threads.push_back(thread);
            }
            closedir(dir);

            return threads;
        }

        void ResolveFrames(NativeThread& thread, const CaptureSlot& slot, const std::vector<LoadedModule>& modules)
        {
            const size_t frameCount = slot.count.load(std::memory_order_acquire);

            thread.stackTrace.reserve(frameCount);
            for (size_t i = 0; i < frameCount; i++)
            {
                const uintptr_t pc = slot.frames[i];

                NativeStacktraceFrame frame;
                frame.address = static_cast<uint64_t>(pc);

                // dladdr takes the loader lock, which is why it runs here and not in the handler.
                Dl_info info;
                if (dladdr(reinterpret_cast<void*>(pc), &info) != 0 && info.dli_fbase != nullptr)
                {
                    const auto base = reinterpret_cast<uintptr_t>(info.dli_fbase);
                    frame.address = static_cast<uint64_t>(pc - base);
                    if (info.dli_fname != nullptr)
                        frame.libraryName = info.dli_fname;

                    const LoadedModule* module = FindModuleByBase(modules, base);
                    if (module != nullptr)
                        frame.buildId = module->buildId;
                }

                thread.stackTrace.push_back(frame);
            }
        }
    }

    std::vector<NativeThread> CaptureNativeThreads(int captureTimeoutMs)
    {
        const pid_t selfTid = GetCurrentThreadId();

        std::vector<NativeThread> threads = EnumerateThreads(selfTid);
        if (threads.empty())
            return threads;

        if (!InstallSignalHandler())
            return threads;

        ANR_LOG_INFO("Collecting stacktraces for %zu native threads", threads.size());

        // Taken once: the mapping from load address to library and build id is the same for every
        // thread, and walking it per frame would be wasted work.
        const std::vector<LoadedModule> modules = CollectLoadedModules();

        // Published before any signal is sent, so a handler always sees a complete table.
        for (size_t i = 0; i < threads.size(); i++)
        {
            s_Slots[i].tid.store(threads[i].id, std::memory_order_relaxed);
            s_Slots[i].count.store(0, std::memory_order_relaxed);
            s_Slots[i].done.store(false, std::memory_order_relaxed);
        }
        s_SlotCount.store(threads.size(), std::memory_order_release);

        for (size_t i = 0; i < threads.size(); i++)
        {
            if (!SendSignalToThread(threads[i].id))
                continue;

            if (!WaitForSlot(s_Slots[i], captureTimeoutMs))
            {
                // Usually a thread in an uninterruptible syscall: the signal stays pending until
                // that syscall returns, which can be long after the report is written.
                ANR_LOG_ERROR("Timeout while waiting for stacktrace from thread %d ('%s')",
                    threads[i].id, threads[i].name.c_str());
                continue;
            }

            ResolveFrames(threads[i], s_Slots[i], modules);
        }

        return threads;
    }
}
