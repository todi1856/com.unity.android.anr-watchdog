#include "ProcUtils.h"

// TEMP_FAILURE_RETRY expands to a loop testing errno against EINTR, so errno.h has to be here
// rather than relied on to arrive through another header.
#include <cerrno>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fcntl.h>
#include <unistd.h>

namespace anrwatchdog
{
    std::string ReadFileToString(const std::string& path)
    {
        int fd = TEMP_FAILURE_RETRY(open(path.c_str(), O_RDONLY | O_CLOEXEC));
        if (fd == -1)
            return std::string();

        std::string content;
        char buffer[512];
        for (;;)
        {
            ssize_t read_bytes = TEMP_FAILURE_RETRY(read(fd, buffer, sizeof(buffer)));
            if (read_bytes <= 0)
                break;
            content.append(buffer, static_cast<size_t>(read_bytes));
        }

        close(fd);
        return content;
    }

    std::string Trim(const std::string& text)
    {
        const char* whitespace = " \t\n\r\f\v";
        size_t begin = text.find_first_not_of(whitespace);
        if (begin == std::string::npos)
            return std::string();

        size_t end = text.find_last_not_of(whitespace);
        return text.substr(begin, end - begin + 1);
    }

    std::string GetThreadName(pid_t tid)
    {
        char path[64];
        snprintf(path, sizeof(path), "/proc/%d/comm", tid);
        std::string name = Trim(ReadFileToString(path));
        return name.empty() ? "<unknown>" : name;
    }

    std::string GetThreadStatus(pid_t tid)
    {
        char path[64];
        snprintf(path, sizeof(path), "/proc/%d/status", tid);
        return ReadFileToString(path);
    }

    std::string GetKeyValueFromThreadStatus(const std::string& name, const std::string& threadStatus)
    {
        // Keys always start a line, so anchoring on the newline avoids matching a key that happens
        // to be a suffix of another one.
        const std::string search = name + ":";
        size_t pos = threadStatus.compare(0, search.size(), search) == 0
            ? 0
            : threadStatus.find("\n" + search);
        if (pos == std::string::npos)
            return std::string();

        if (pos != 0)
            pos += 1; // Skip the newline the search included.
        pos += search.size();

        size_t endOfLine = threadStatus.find('\n', pos);
        if (endOfLine == std::string::npos)
            endOfLine = threadStatus.length();

        return Trim(threadStatus.substr(pos, endOfLine - pos));
    }

    int GetThreadPriority(pid_t tid)
    {
        char path[64];
        snprintf(path, sizeof(path), "/proc/%d/stat", tid);
        const std::string stat = ReadFileToString(path);

        // Field 2 is the thread name in parentheses and may itself contain spaces and parentheses,
        // so parsing starts after the last ')'. The token that follows is field 3, priority is 18.
        size_t pos = stat.rfind(')');
        if (pos == std::string::npos)
            return -1;

        const char* cursor = stat.c_str() + pos + 1;
        char* end = nullptr;
        long value = -1;
        for (int field = 3; field <= 18; field++)
        {
            while (*cursor == ' ')
                cursor++;
            if (*cursor == '\0')
                return -1;

            if (field < 4)
            {
                // Field 3 is the single character state, skip it without parsing.
                while (*cursor != ' ' && *cursor != '\0')
                    cursor++;
                continue;
            }

            value = strtol(cursor, &end, 10);
            if (cursor == end)
                return -1;
            cursor = end;
        }

        return static_cast<int>(value);
    }
}
