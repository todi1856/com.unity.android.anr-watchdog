#include "NativeThreads.h"
#include "Log.h"
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
#include <semaphore.h>
#include <sys/syscall.h>
#include <unistd.h>
#include <unwind.h>

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

        // The target thread is interrupted mid-ANR and very likely holds the malloc lock, so the
        // signal handler must not allocate. It writes raw program counters into this preallocated
        // buffer and nothing else; symbolication happens on the watchdog thread afterwards.
        // Only one thread is captured at a time, so a single buffer is enough.
        uintptr_t s_Frames[kMaxFrames];
        std::atomic<size_t> s_FrameCount{0};
        std::atomic<pid_t> s_TargetTid{0};
        sem_t s_CaptureDone;

        struct UnwindState
        {
            size_t count;
        };

        pid_t GetCurrentThreadId()
        {
            return static_cast<pid_t>(syscall(SYS_gettid));
        }

        _Unwind_Reason_Code UnwindCallback(_Unwind_Context* context, void* argument)
        {
            UnwindState* state = static_cast<UnwindState*>(argument);

            uintptr_t pc = _Unwind_GetIP(context);
            if (pc == 0)
                return _URC_END_OF_STACK;

            if (state->count >= kMaxFrames)
                return _URC_END_OF_STACK;

            s_Frames[state->count++] = pc;
            return _URC_NO_REASON;
        }

        void CaptureSignalHandler(int /*signum*/, siginfo_t* /*info*/, void* /*context*/)
        {
            // Something else may have sent this signal - only the thread we asked for responds.
            if (s_TargetTid.load(std::memory_order_acquire) != GetCurrentThreadId())
                return;

            UnwindState state{0};
            _Unwind_Backtrace(&UnwindCallback, &state);

            s_FrameCount.store(state.count, std::memory_order_release);
            sem_post(&s_CaptureDone); // async-signal-safe
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

        bool WaitForCapture(int timeoutMs)
        {
            timespec deadline;
            if (clock_gettime(CLOCK_REALTIME, &deadline) == -1)
                return false;

            deadline.tv_sec += timeoutMs / 1000;
            deadline.tv_nsec += static_cast<long>(timeoutMs % 1000) * 1000000L;
            if (deadline.tv_nsec >= 1000000000L)
            {
                deadline.tv_sec += 1;
                deadline.tv_nsec -= 1000000000L;
            }

            while (sem_timedwait(&s_CaptureDone, &deadline) == -1)
            {
                if (errno == EINTR)
                    continue;
                return false;
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

        void ResolveFrames(NativeThread& thread, size_t frameCount)
        {
            thread.stackTrace.reserve(frameCount);
            for (size_t i = 0; i < frameCount; i++)
            {
                const uintptr_t pc = s_Frames[i];

                NativeStacktraceFrame frame;
                frame.address = static_cast<uint64_t>(pc);

                // dladdr takes the loader lock, which is why it runs here and not in the handler.
                Dl_info info;
                if (dladdr(reinterpret_cast<void*>(pc), &info) != 0 && info.dli_fbase != nullptr)
                {
                    frame.address = static_cast<uint64_t>(pc - reinterpret_cast<uintptr_t>(info.dli_fbase));
                    if (info.dli_fname != nullptr)
                        frame.libraryName = info.dli_fname;
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

        ANR_LOG_INFO("Collecting stacktraces for %zu native threads", threads.size());

        if (sem_init(&s_CaptureDone, 0, 0) == -1)
        {
            ANR_LOG_ERROR("sem_init failed: %s", strerror(errno));
            return threads;
        }

        struct sigaction newAction;
        struct sigaction oldAction;
        memset(&newAction, 0, sizeof(newAction));
        memset(&oldAction, 0, sizeof(oldAction));
        newAction.sa_sigaction = CaptureSignalHandler;
        newAction.sa_flags = SA_SIGINFO | SA_RESTART | SA_ONSTACK;
        sigemptyset(&newAction.sa_mask);

        if (sigaction(CaptureSignal(), &newAction, &oldAction) == -1)
        {
            ANR_LOG_ERROR("Error setting up signal handler for signal %d: %s", CaptureSignal(), strerror(errno));
            sem_destroy(&s_CaptureDone);
            return threads;
        }

        for (NativeThread& thread : threads)
        {
            // Drop anything a previous, timed-out capture may have posted late.
            while (sem_trywait(&s_CaptureDone) == 0)
            {
            }

            s_FrameCount.store(0, std::memory_order_relaxed);
            s_TargetTid.store(thread.id, std::memory_order_release);

            if (!SendSignalToThread(thread.id))
            {
                s_TargetTid.store(0, std::memory_order_release);
                continue;
            }

            if (!WaitForCapture(captureTimeoutMs))
            {
                ANR_LOG_ERROR("Timeout while waiting for stacktrace from thread %d ('%s')",
                    thread.id, thread.name.c_str());
                s_TargetTid.store(0, std::memory_order_release);
                continue;
            }

            const size_t frameCount = s_FrameCount.load(std::memory_order_acquire);
            s_TargetTid.store(0, std::memory_order_release);

            ResolveFrames(thread, frameCount);
        }

        if (sigaction(CaptureSignal(), &oldAction, nullptr) == -1)
            ANR_LOG_ERROR("Error restoring signal handler for signal %d: %s", CaptureSignal(), strerror(errno));

        sem_destroy(&s_CaptureDone);

        return threads;
    }
}
