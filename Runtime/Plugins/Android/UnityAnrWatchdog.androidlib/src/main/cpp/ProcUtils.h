#pragma once

#include <string>
#include <sys/types.h>

namespace anrwatchdog
{
    // Replacements for the AOSP debuggerd/libdebuggerd helpers, so the module does not have to
    // vendor libbase. All of these read /proc and are safe to call from the watchdog thread while
    // other threads are stuck.

    std::string ReadFileToString(const std::string& path);
    std::string Trim(const std::string& text);

    // /proc/<tid>/comm
    std::string GetThreadName(pid_t tid);

    // /proc/<tid>/status
    std::string GetThreadStatus(pid_t tid);

    // Value of "<name>:" in a /proc/<tid>/status dump, empty when the key is absent.
    std::string GetKeyValueFromThreadStatus(const std::string& name, const std::string& threadStatus);

    // Scheduling priority from /proc/<tid>/stat, -1 when it cannot be read.
    int GetThreadPriority(pid_t tid);
}
