#pragma once

#include <cstdint>
#include <string>
#include <vector>
#include <sys/types.h>

namespace anrwatchdog
{
    struct NativeStacktraceFrame
    {
        // Address relative to the load address of the library, so it can be symbolicated offline
        // against the unstripped binary. Absolute pc when the address maps to no known library.
        uint64_t address = 0;
        std::string libraryName;
    };

    struct NativeThread
    {
        pid_t id = 0;
        std::string name;
        std::string state;
        int priority = -1;
        std::vector<NativeStacktraceFrame> stackTrace;
    };

    // Captures every native thread of this process except the calling one. Each thread is
    // interrupted with a signal and unwinds itself; captureTimeoutMs bounds the wait per thread so
    // a thread that is wedged in an uninterruptible state cannot stall the report.
    std::vector<NativeThread> CaptureNativeThreads(int captureTimeoutMs);
}
