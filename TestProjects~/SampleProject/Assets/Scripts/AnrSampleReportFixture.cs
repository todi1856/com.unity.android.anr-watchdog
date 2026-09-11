#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Unity.Android;

/// <summary>
/// Editor-only synthetic ANR report. The watchdog itself only runs in an Android player, so
/// without this there is nothing to show in the Editor and the report viewer can only be worked
/// on by deploying to a device. The shape and the volume match a real report from a phone
/// (roughly twenty Java threads, sixty native ones, a thousand lines in total) so that layout and
/// scrolling behave the same here as they do on device.
/// </summary>
static class AnrSampleReportFixture
{
    const int k_ProcessId = 25206;
    const string k_UnityLib = "/data/app/~~Qr7gK1vQ==/com.UnityTechnologies.ANRWatchdogDemo-9fA==/lib/arm64/libunity.so";
    const string k_Il2CppLib = "/data/app/~~Qr7gK1vQ==/com.UnityTechnologies.ANRWatchdogDemo-9fA==/lib/arm64/libil2cpp.so";

    static readonly string[] k_SystemLibraries =
    {
        "/apex/com.android.runtime/lib64/bionic/libc.so",
        "/apex/com.android.art/lib64/libart.so",
        "/system/lib64/libutils.so",
        "/system/lib64/libgui.so",
        "/vendor/lib64/egl/libGLESv2_adreno.so",
        "/system/lib64/libEGL.so"
    };

    public static AnrReport Create()
    {
        // Fixed seed: the same report every time, so a layout change is easy to compare.
        var random = new Random(20260911);

        return new AnrReport
        {
            reportType = "ANR (sample data, Editor only)",
            reportTimeStamp = "2026-09-11 13:26:41 UTC",
            anrTimeMs = 3005,
            packageName = "com.UnityTechnologies.ANRWatchdogDemo",
            contextClassName = "UnityPlayerGameActivity",
            unityVersion = "6000.0.74f1",
            deviceModel = "Google Pixel 9 Pro Fold",
            deviceFingerPrint = "google/comet/comet:16/CP1A.260305.018/14887507:user/release-keys",
            deviceApiLevel = 36,
            buildType = "Development",
            scriptingBackend = "IL2CPP",
            multiWindow = false,
            orientation = "Portrait",
            abi = "arm64-v8a",
            processId = k_ProcessId,
            userId = 10234,
            javaThreads = CreateJavaThreads(random),
            nativeThreads = CreateNativeThreads(random)
        };
    }

    static AnrReport.JavaThread[] CreateJavaThreads(Random random)
    {
        var threads = new List<AnrReport.JavaThread>
        {
            JavaThread("main", 2, "TIMED_WAITING", 5,
                JavaFrame("java.lang.Thread", "sleep", "Thread.java", -2),
                JavaFrame("java.lang.Thread", "sleep", "Thread.java", 450),
                JavaFrame("java.lang.Thread", "sleep", "Thread.java", 355),
                JavaFrame("com.unity3d.player.ReflectionHelper", "nativeProxyInvoke", "SourceFile", -2),
                JavaFrame("com.unity3d.player.ReflectionHelper", "invoke", "SourceFile", 0),
                JavaFrame("java.lang.reflect.Proxy", "invoke", "Proxy.java", 1006),
                JavaFrame("android.os.Handler", "handleCallback", "Handler.java", 958),
                JavaFrame("android.os.Handler", "dispatchMessage", "Handler.java", 99),
                JavaFrame("android.os.Looper", "loopOnce", "Looper.java", 230),
                JavaFrame("android.os.Looper", "loop", "Looper.java", 319),
                JavaFrame("android.app.ActivityThread", "main", "ActivityThread.java", 8919),
                JavaFrame("java.lang.reflect.Method", "invoke", "Method.java", -2),
                JavaFrame("com.android.internal.os.RuntimeInit$MethodAndArgsCaller", "run", "RuntimeInit.java", 578),
                JavaFrame("com.android.internal.os.ZygoteInit", "main", "ZygoteInit.java", 1103)),

            JavaThread("com.unity3d.anrwatchdog.MainThreadWatchdog", 70, "RUNNABLE", 10,
                JavaFrame("dalvik.system.VMStack", "getThreadStackTrace", "VMStack.java", -2),
                JavaFrame("java.lang.Thread", "getStackTrace", "Thread.java", 1841),
                JavaFrame("java.lang.Thread", "getAllStackTraces", "Thread.java", 1917),
                JavaFrame("com.unity3d.anrwatchdog.MainThreadWatchdog", "onApplicationNotResponding", "MainThreadWatchdog.java", 145),
                JavaFrame("com.unity3d.anrwatchdog.MainThreadWatchdog", "run", "MainThreadWatchdog.java", 128)),

            JavaThread("FinalizerDaemon", 64, "WAITING", 5,
                JavaFrame("java.lang.Object", "wait", "Object.java", -2),
                JavaFrame("java.lang.Object", "wait", "Object.java", 490),
                JavaFrame("java.lang.ref.ReferenceQueue", "remove", "ReferenceQueue.java", 135),
                JavaFrame("java.lang.ref.ReferenceQueue", "remove", "ReferenceQueue.java", 207),
                JavaFrame("java.lang.Daemons$FinalizerDaemon", "runInternal", "Daemons.java", 342),
                JavaFrame("java.lang.Daemons$Daemon", "run", "Daemons.java", 145),
                JavaFrame("java.lang.Thread", "run", "Thread.java", 1012)),

            JavaThread("FinalizerWatchdogDaemon", 65, "WAITING", 5,
                JavaFrame("java.lang.Object", "wait", "Object.java", -2),
                JavaFrame("java.lang.Daemons$FinalizerWatchdogDaemon", "sleepUntilNeeded", "Daemons.java", 403),
                JavaFrame("java.lang.Daemons$FinalizerWatchdogDaemon", "runInternal", "Daemons.java", 383),
                JavaFrame("java.lang.Daemons$Daemon", "run", "Daemons.java", 145),
                JavaFrame("java.lang.Thread", "run", "Thread.java", 1012)),

            JavaThread("ReferenceQueueDaemon", 63, "WAITING", 5,
                JavaFrame("java.lang.Object", "wait", "Object.java", -2),
                JavaFrame("java.lang.Daemons$ReferenceQueueDaemon", "runInternal", "Daemons.java", 262),
                JavaFrame("java.lang.Daemons$Daemon", "run", "Daemons.java", 145),
                JavaFrame("java.lang.Thread", "run", "Thread.java", 1012)),

            JavaThread("HeapTaskDaemon", 66, "WAITING", 5,
                JavaFrame("dalvik.system.VMRuntime", "runHeapTasks", "VMRuntime.java", -2),
                JavaFrame("java.lang.Daemons$HeapTaskDaemon", "runInternal", "Daemons.java", 566),
                JavaFrame("java.lang.Daemons$Daemon", "run", "Daemons.java", 145)),

            // Threads the runtime keeps busy without a Java stack of their own.
            JavaThread("Signal Catcher", 61, "RUNNABLE", 10),
            JavaThread("Jit thread pool worker thread 0", 62, "RUNNABLE", 5),
            JavaThread("Profile Saver", 68, "RUNNABLE", 5),
            JavaThread("RenderThread", 71, "RUNNABLE", 7),

            JavaThread("UnityMain", 80, "RUNNABLE", 5,
                JavaFrame("com.unity3d.player.UnityPlayer", "nativeRender", "SourceFile", -2),
                JavaFrame("com.unity3d.player.UnityPlayer$UnityThread", "run", "SourceFile", 129)),

            JavaThread("queued-work-looper", 82, "WAITING", 5,
                JavaFrame("android.os.MessageQueue", "nativePollOnce", "MessageQueue.java", -2),
                JavaFrame("android.os.MessageQueue", "next", "MessageQueue.java", 335),
                JavaFrame("android.os.Looper", "loopOnce", "Looper.java", 161),
                JavaFrame("android.os.Looper", "loop", "Looper.java", 319),
                JavaFrame("android.os.HandlerThread", "run", "HandlerThread.java", 85))
        };

        for (var i = 1; i <= 6; i++)
        {
            threads.Add(JavaThread($"Binder:{k_ProcessId}_{i}", 90 + i, "RUNNABLE", 5,
                JavaFrame("android.os.BinderProxy", "transactNative", "Binder.java", -2),
                JavaFrame("android.os.BinderProxy", "transact", "Binder.java", 1218),
                JavaFrame("android.os.Binder", "execTransactInternal", "Binder.java", 1385),
                JavaFrame("android.os.Binder", "execTransact", "Binder.java", 1344)));
        }

        for (var i = 0; i < 3; i++)
        {
            threads.Add(JavaThread($"pool-{i + 1}-thread-1", 100 + i, "WAITING", 5,
                JavaFrame("jdk.internal.misc.Unsafe", "park", "Unsafe.java", -2),
                JavaFrame("java.util.concurrent.locks.LockSupport", "park", "LockSupport.java", 194),
                JavaFrame("java.util.concurrent.LinkedBlockingQueue", "take", "LinkedBlockingQueue.java", 435),
                JavaFrame("java.util.concurrent.ThreadPoolExecutor", "getTask", "ThreadPoolExecutor.java", 1062),
                JavaFrame("java.util.concurrent.ThreadPoolExecutor", "runWorker", "ThreadPoolExecutor.java", 1122),
                JavaFrame("java.lang.Thread", "run", "Thread.java", 1012)));
        }

        _ = random; // Java stacks are handcrafted; only the native ones are generated.
        return threads.ToArray();
    }

    static AnrReport.NativeThread[] CreateNativeThreads(Random random)
    {
        var names = new List<string>
        {
            "AnrWatchdogDemo", "UnityMain", "UnityGfxDeviceW", "UnityChoreograp", "UnityGfxDeviceW",
            "RenderThread", "Signal Catcher", "perfetto_hprof_", "ADB-JDWP Connec", "Jit thread pool",
            "HeapTaskDaemon", "ReferenceQueueD", "FinalizerDaemon", "FinalizerWatchd", "Profile Saver",
            "hwuiTask0", "hwuiTask1", "GoogleApiHandle", "queued-work-loo", "InputSampling"
        };

        for (var i = 1; i <= 6; i++)
            names.Add($"binder:{k_ProcessId}_{i}");
        for (var i = 1; i <= 12; i++)
            names.Add($"Job.worker {i}");
        for (var i = 1; i <= 8; i++)
            names.Add($"BackgroundJobWo{i}");
        for (var i = 1; i <= 15; i++)
            names.Add($"Thread-{i}");

        var states = new[] { "S (sleeping)", "R (running)", "D (disk sleep)" };
        var threads = new List<AnrReport.NativeThread>(names.Count);

        for (var i = 0; i < names.Count; i++)
        {
            var isMain = i == 0;

            // A couple of threads never answer the capture signal - that case is worth seeing.
            var frameCount = i % 17 == 5 ? 0 : random.Next(6, 26);

            threads.Add(new AnrReport.NativeThread
            {
                name = names[i],
                id = isMain ? k_ProcessId : k_ProcessId + 10 + i,
                state = isMain ? "S (sleeping)" : states[random.Next(states.Length)],
                priority = isMain ? 10 : 20 + random.Next(0, 11),
                stackTrace = CreateNativeFrames(random, frameCount)
            });
        }

        return threads.ToArray();
    }

    static AnrReport.NativeStackFrame[] CreateNativeFrames(Random random, int count)
    {
        var frames = new AnrReport.NativeStackFrame[count];
        for (var i = 0; i < count; i++)
        {
            var library = random.Next(4) switch
            {
                0 => k_UnityLib,
                1 => k_Il2CppLib,
                _ => k_SystemLibraries[random.Next(k_SystemLibraries.Length)]
            };

            frames[i] = new AnrReport.NativeStackFrame
            {
                address = (long)random.Next(0x1000, 0x7FFFFFF) * 4,
                libraryName = library
            };
        }

        return frames;
    }

    static AnrReport.JavaThread JavaThread(string name, long id, string state, int priority,
        params AnrReport.JavaStackFrame[] stackTrace) =>
        new AnrReport.JavaThread
        {
            name = name,
            id = id,
            state = state,
            priority = priority,
            stackTrace = stackTrace
        };

    static AnrReport.JavaStackFrame JavaFrame(string className, string methodName, string fileName, int lineNumber) =>
        new AnrReport.JavaStackFrame
        {
            className = className,
            methodName = methodName,
            fileName = fileName,
            lineNumber = lineNumber
        };
}
#endif
