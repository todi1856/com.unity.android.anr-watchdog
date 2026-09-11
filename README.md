# ANR Watchdog for Unity Android

A utility for detecting **ANR** (Application Not Responding) conditions in Unity players running on Android, and for reporting where every thread was when the main thread stopped responding.

### What is an ANR?

Android raises an ANR when the app's main thread fails to respond to input or to service its message queue within a system-defined timeout (roughly 5 seconds for input). On Unity players this usually means the main thread is blocked inside script code, a native call or a synchronous load. Because the process is frozen, the stall is hard to catch with the profiler - the watchdog observes it from a background thread instead and captures the state of the process at the moment the main thread stopped ticking.

__Note:__ Detection and capture both run on a background thread, so they keep working while the main thread is unresponsive.

## Requirements

* [Git](https://git-scm.com/install/)
* Unity **6000.0.0f1** or higher
* Android as the active build target
* Android **API level 23** or higher on the device

## Installation

1. In Unity, open **Window → Package Manager**.
2. In the top-left corner, click the **+** button and select **Install package from Git URL**.
3. Enter the following HTTPS url:
   ```
   https://github.com/todi1856/com.unity.android.anr-watchdog.git
   ```

## Quick Start

```csharp
using Unity.Android;
using UnityEngine;

public class AnrWatchdogBootstrap : MonoBehaviour
{
    void Start()
    {
        AnrWatchdog.AnrDetected += report =>
            Debug.Log($"ANR after {report.anrTimeMs} ms, {report.javaThreads.Length} java / " +
                      $"{report.nativeThreads.Length} native threads captured");

        AnrWatchdog.Start();
    }
}
```

The watchdog is not started automatically - nothing happens until `AnrWatchdog.Start()` is called.

## Options

`AnrWatchdog.Start(AnrWatchdogSettings)` takes:

* **anrTimeoutMs** (default `3000`) - how long the main thread must be stuck before it counts as an ANR. Lower than Android's own threshold, so the stall is captured before the system kills the app.
* **pollIntervalMs** (default `300`) - how often the watchdog thread checks the main thread.
* **reportIntervalMs** (default `10000`) - minimum interval between two reports, so a main thread that stays stuck does not produce a report on every check.
* **reportPollIntervalSeconds** (default `1.0`) - how often C# checks for new reports to raise `AnrDetected` for. Set to `0` to disable polling and collect reports yourself with `AnrWatchdog.GetReports()`.
* **worldReadableReports** (default `true`) - write reports as `0644` rather than owner-only `0600`. App processes run with `umask 0077`, so this takes an explicit `fchmod`, and the emulated storage volume synthesizes its own permissions and may ignore it. Set to `false` to leave the files owner-only.

## Reports

Reports are written as JSON to `Application.persistentDataPath/anr/anr-<timestamp>.json`, atomically (written to `.part` and renamed), so a report is never read half-written. They can be collected in two ways:

* the `AnrWatchdog.AnrDetected` event, raised on the main thread once it recovers;
* `AnrWatchdog.GetReports()`, which reads every report on disk, oldest first, and leaves them there. `AnrWatchdog.ClearReports()` deletes them when you are done - nothing removes them on your behalf, so reports survive across sessions and can be pulled off the device with adb.

Both deliver the report only after the main thread starts running again - while it is stuck, no script code executes. Each report carries the file it was read from in `sourcePath`, which is how the `AnrDetected` poller avoids raising the same report twice now that reading is non-destructive.

A report contains device and build context (`packageName`, `unityVersion`, `deviceModel`, `deviceApiLevel`, `abi`, `orientation`, ...) plus two thread dumps:

* **javaThreads** - name, id, state, priority and the Java stack of every thread, from `Thread.getAllStackTraces()`.
* **nativeThreads** - name, id, state (from `/proc/<tid>/status`), priority (from `/proc/<tid>/stat`) and the native stack of every thread in the process.

Native frames carry an `address` relative to the load address of `libraryName` plus the library's GNU `buildId`, rather than a function name: resolving symbols on device costs more than a second for a full process dump, and `.dynsym` - all that is available at runtime - would name internal Unity and IL2CPP frames wrongly rather than not at all.

Symbolicate offline against the unstripped binaries from the build's `symbols.zip`, which gives function names, file and line, and inlined frames:

```
llvm-symbolizer --obj=libunity.sym.so 0x<address>
```

The `buildId` identifies the exact binary an address came from, so a symbol server can pick the matching library even across builds. Compare it against a local file with:

```
llvm-readelf --notes libunity.sym.so | grep "Build ID"
```

## How it works

* `MainThreadWatchdog` (Java) posts a ticker `Runnable` to the main `Looper`. If the ticker has not run for `anrTimeoutMs`, it serializes `Thread.getAllStackTraces()` and the device context to JSON and calls into native code.
* The native library enumerates `/proc/self/task`, installs a handler for a real-time signal and interrupts each thread in turn. Each interrupted thread unwinds itself with `_Unwind_Backtrace` into a preallocated buffer, then signals the watchdog thread through a semaphore.
* Addresses are resolved to libraries with `dladdr` afterwards, on the watchdog thread - the signal handler itself allocates nothing and takes no locks, because a thread stalled mid-ANR may well be holding the allocator's.
* Build ids come from one `dl_iterate_phdr` pass per report, read out of each library's mapped `PT_NOTE` segment - no file access, and it works on stripped libraries.
* The native dump is merged into the Java report and written to disk.

## Package layout

```
Runtime/
  AnrWatchdog.cs                      # public API
  AnrWatchdogSettings.cs
  AnrReport.cs                        # report deserialized with JsonUtility
  AnrReportPoller.cs
  Plugins/Android/UnityAnrWatchdog.androidlib/
    build.gradle                      # Gradle library module, builds the native code with CMake
    src/main/java/com/unity3d/anrwatchdog/
      UnityAnrWatchdog.java           # entry point called from C#
      MainThreadWatchdog.java         # the watchdog thread
    src/main/cpp/
      AnrWatchdogJni.cpp              # JNI entry, report merging and atomic write
      NativeThreads.cpp               # thread enumeration, signalling and unwinding
      ProcUtils.cpp                   # /proc readers
      JsonWriter.h
```

The native library is compiled from source for every ABI of the build, by the Gradle module itself - nothing is prebuilt or committed as a binary.

## Known limitations

* Native frames are addresses only; symbolication is an offline step.
* Unwinding starts inside the signal handler, so the top frames of each native stack are the handler itself.
* The capture signal is `SIGRTMIN + 4`. It deliberately avoids `SIGUSR1`/`SIGUSR2`, which Mono uses, but a third-party library installing a handler for the same real-time signal would conflict.
* A thread wedged in an uninterruptible kernel state cannot respond to the signal; it is reported with an empty stack after a 3 second timeout.
