#include "JsonWriter.h"
#include "Log.h"
#include "NativeThreads.h"
#include "ProcUtils.h"

#include <cerrno>
#include <cstring>
#include <fcntl.h>
#include <jni.h>
#include <string>
#include <unistd.h>

#if defined(__aarch64__)
    #define ANR_ABI_STRING "arm64-v8a"
#elif defined(__arm__)
    #define ANR_ABI_STRING "armeabi-v7a"
#elif defined(__x86_64__)
    #define ANR_ABI_STRING "x86_64"
#elif defined(__i386__)
    #define ANR_ABI_STRING "x86"
#else
    #define ANR_ABI_STRING "unknown"
#endif

namespace
{
    using namespace anrwatchdog;

    // How long to wait for a single thread to unwind itself before giving up on it.
    constexpr int kCaptureTimeoutMs = 3000;

    std::string BuildNativeReport()
    {
        std::vector<NativeThread> threads = CaptureNativeThreads(kCaptureTimeoutMs);

        JsonWriter writer;
        writer.BeginObject();

        writer.Key("abi");
        writer.Value(ANR_ABI_STRING);
        writer.Key("processId");
        writer.Value(static_cast<uint64_t>(getpid()));
        writer.Key("userId");
        writer.Value(static_cast<uint64_t>(getuid()));

        writer.Key("nativeThreads");
        writer.BeginArray();
        for (const NativeThread& thread : threads)
        {
            writer.BeginObject();
            writer.Key("name");
            writer.Value(thread.name);
            writer.Key("id");
            writer.Value(static_cast<int>(thread.id));
            writer.Key("state");
            writer.Value(thread.state);
            writer.Key("priority");
            writer.Value(thread.priority);

            writer.Key("stackTrace");
            writer.BeginArray();
            for (const NativeStacktraceFrame& frame : thread.stackTrace)
            {
                writer.BeginObject();
                writer.Key("address");
                writer.Value(frame.address);
                writer.Key("libraryName");
                writer.Value(frame.libraryName);
                writer.Key("buildId");
                writer.Value(frame.buildId);
                writer.EndObject();
            }
            writer.EndArray();

            writer.EndObject();
        }
        writer.EndArray();

        writer.EndObject();
        return writer.Result();
    }

    // Splices two JSON objects into one. Both sides are produced by this package, so this is a
    // textual merge rather than a parse - it keeps the Java report verbatim and avoids pulling a
    // JSON parser into the module.
    bool MergeJsonObjects(const std::string& first, const std::string& second, std::string& merged)
    {
        const std::string left = Trim(first);
        const std::string right = Trim(second);

        if (left.size() < 2 || left.front() != '{' || left.back() != '}' ||
            right.size() < 2 || right.front() != '{' || right.back() != '}')
        {
            ANR_LOG_ERROR("Cannot merge ANR report, expected two JSON objects");
            return false;
        }

        const std::string leftBody = Trim(left.substr(1, left.size() - 2));
        const std::string rightBody = Trim(right.substr(1, right.size() - 2));

        merged = "{";
        merged += leftBody;
        if (!leftBody.empty() && !rightBody.empty())
            merged += ",\n";
        merged += rightBody;
        merged += "}";
        return true;
    }

    // Writes to a temporary file and renames it into place, so a reader on the C# side never
    // observes a half-written report.
    bool WriteReportAtomically(const std::string& path, const std::string& content)
    {
        const std::string temporaryPath = path + ".part";

        int fd = TEMP_FAILURE_RETRY(open(temporaryPath.c_str(), O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0600));
        if (fd == -1)
        {
            ANR_LOG_ERROR("Failed to open '%s': %s", temporaryPath.c_str(), strerror(errno));
            return false;
        }

        size_t written = 0;
        while (written < content.size())
        {
            ssize_t result = TEMP_FAILURE_RETRY(write(fd, content.data() + written, content.size() - written));
            if (result <= 0)
            {
                ANR_LOG_ERROR("Failed to write '%s': %s", temporaryPath.c_str(), strerror(errno));
                close(fd);
                unlink(temporaryPath.c_str());
                return false;
            }
            written += static_cast<size_t>(result);
        }

        fsync(fd);
        close(fd);

        if (rename(temporaryPath.c_str(), path.c_str()) == -1)
        {
            ANR_LOG_ERROR("Failed to move '%s' to '%s': %s", temporaryPath.c_str(), path.c_str(), strerror(errno));
            unlink(temporaryPath.c_str());
            return false;
        }

        return true;
    }

    std::string ToStdString(JNIEnv* env, jstring value)
    {
        if (value == nullptr)
            return std::string();

        const char* chars = env->GetStringUTFChars(value, nullptr);
        if (chars == nullptr)
            return std::string(); // Out of memory, the pending exception is handled by the caller.

        std::string result(chars);
        env->ReleaseStringUTFChars(value, chars);
        return result;
    }

    // Note: called on the watchdog thread while the main thread is unresponsive.
    jboolean nativeApplicationNotResponding(JNIEnv* env, jobject /*thiz*/, jstring javaThreadsJson, jstring reportPath)
    {
        const std::string javaReport = ToStdString(env, javaThreadsJson);
        const std::string path = ToStdString(env, reportPath);
        if (env->ExceptionCheck())
            return JNI_FALSE;

        if (javaReport.empty() || path.empty())
        {
            ANR_LOG_ERROR("nativeApplicationNotResponding: missing report data");
            return JNI_FALSE;
        }

        std::string merged;
        if (!MergeJsonObjects(javaReport, BuildNativeReport(), merged))
            return JNI_FALSE;

        if (!WriteReportAtomically(path, merged))
            return JNI_FALSE;

        ANR_LOG_INFO("ANR report written to %s", path.c_str());
        return JNI_TRUE;
    }

    const JNINativeMethod kMethods[] = {
        {"nativeApplicationNotResponding", "(Ljava/lang/String;Ljava/lang/String;)Z", reinterpret_cast<void*>(nativeApplicationNotResponding)},
    };
}

extern "C" JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM* vm, void* /*reserved*/)
{
    JNIEnv* env = nullptr;
    if (vm->GetEnv(reinterpret_cast<void**>(&env), JNI_VERSION_1_6) != JNI_OK)
        return JNI_ERR;

    jclass watchdogClass = env->FindClass("com/unity3d/anrwatchdog/MainThreadWatchdog");
    if (watchdogClass == nullptr)
        return JNI_ERR;

    const jint result = env->RegisterNatives(watchdogClass, kMethods, sizeof(kMethods) / sizeof(kMethods[0]));
    env->DeleteLocalRef(watchdogClass);
    if (result != JNI_OK)
        return JNI_ERR;

    return JNI_VERSION_1_6;
}
