# ANR Watchdog for Unity Android

A utility for detecting **ANR** (Application Not Responding) conditions in Unity players running on Android, and for reporting where every thread was when the Android UI thread stopped responding.

[**Walkthrough video**](https://www.youtube.com/watch?v=6cbU-XhTFic) - the watchdog firing on a device, the report it writes, and symbolicating the native stacks in the Editor.

### Which thread?

Two different threads matter here, and the difference is the whole point of this package:

* the **Android UI thread** - the activity's `Looper` thread. Android watches this one, and this is the one the watchdog watches.
* the **Unity main thread** - the player loop, which on Android players runs on a thread of its own (`UnityMain`). Android does not watch it, and blocking it never raises an ANR.

So "main thread" on its own is ambiguous in this codebase and is avoided: everything says which of the two it means.

### What is an ANR?

Android raises an ANR when the app's **Android UI thread** fails to respond to input or to service its message queue within a system-defined timeout (roughly 5 seconds for input). On Unity players this usually means the UI thread is blocked inside a JNI call, an activity callback or something posted to it from script code. The stall is hard to catch with the profiler, so the watchdog observes it from a background thread and captures the state of the process at the moment the UI thread stopped ticking.

__Note:__ Detection and capture both run on a background thread, so they keep working while the Android UI thread is unresponsive - and so does the Unity player loop, which is why the app often keeps rendering through an ANR.

## Requirements

* [Git](https://git-scm.com/install/)
* Unity **6000.0.0f1** or higher
* Android as the active build target
* Android **API level 23** or higher on the device

## Tested on

Exercised end to end - stall the UI thread, capture, report written to disk, symbolicated in the Editor - on:

| Device | Android | API level |
| --- | --- | --- |
| Google Pixel 7 Pro | 16 | 36 |
| Google Pixel 2 | 10 | 29 |
| Oppo R15 Pro | 8.1.0 | 27 |

API 27 is the oldest version verified so far. The module still builds against whatever `minSdkVersion` the project sets, and nothing in the capture path is version specific - `java.time` is guarded for API 26, multi-window for API 24, `getLongVersionCode` for API 28 - but below 27 is untested.

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
        // Whatever the previous session left behind, including the report from an ANR that killed
        // it. Logging is only an example - this is where a real integration ships the report off
        // the device for offline diagnostics: upload it to a crash reporting service, your own
        // backend, or anywhere you can read it later. Each report is a self-contained JSON file,
        // still on disk at report.sourcePath, carrying the build ids needed to symbolicate its
        // native frames against symbols.zip once it arrives. The package itself never touches the
        // network, so nothing leaves the device unless you send it.
        foreach (var report in AnrWatchdog.GetReports())
            Debug.Log($"ANR after {report.anrTimeMs} ms, {report.javaThreads.Length} java / " +
                      $"{report.nativeThreads.Length} native threads captured");

        AnrWatchdog.ClearReports();
        AnrWatchdog.Start();
    }
}
```

The watchdog is not started automatically - nothing happens until `AnrWatchdog.Start()` is called. Read before clearing, and clear before starting, so a report written moments after startup is not deleted unread.

Nothing watches the report directory for you: the package writes reports and leaves them there. To notice an ANR while the app is still running - the process often survives the stall - poll `GetReports()` yourself and skip the ones whose `sourcePath` you have already handled. `TestProjects~/SampleProject` does exactly that.

## Options

`AnrWatchdog.Start(AnrWatchdogSettings)` takes:

* **anrTimeoutMs** (default `3000`) - how long the Android UI thread must be stuck before it counts as an ANR. Lower than Android's own threshold, so the stall is captured before the system kills the app.
* **pollIntervalMs** (default `300`) - how often the watchdog thread checks the Android UI thread.
* **reportIntervalMs** (default `10000`) - minimum interval between two reports, so a UI thread that stays stuck does not produce a report on every check.
* **prettyJson** (default `true`) - write reports indented, so they can be read as they are. Turning it off roughly halves the file at the cost of needing a formatter to read one by hand; the Editor's report window does not care either way.
* **worldReadableReports** (default `true`) - write reports as `0644` rather than owner-only `0600`.

  **Leave this on if you want to pull reports off the device with `adb pull`.** `adb` runs as the `shell` user, not as your app, so it cannot read a file the app wrote owner-only:

  ```
  adb pull /storage/emulated/0/Android/data/<package>/files/anr/anr-20260911-143114-797.json
  ```

  Two things make this less straightforward than it looks. App processes run with `umask 0077`, which masks the mode passed to `open()` back down to `0600` - the package therefore calls `fchmod` explicitly, which the umask does not apply to. And the emulated storage volume synthesizes its own permissions, so it is free to ignore the request; check what actually landed with `adb shell ls -l` on the report directory.

  Set it to `false` to keep the files owner-only. Reports contain thread names and stacks, no user data, and under scoped storage other apps cannot reach your `Android/data` directory whatever the mode says - so the practical exposure is small either way. If your project writes to internal storage instead (`/data/user/0/<package>/files`), the mode is honoured properly there, and reports come off the device with `adb exec-out run-as <package> cat ...` rather than `adb pull`.

## Reports

Reports are written as JSON to `Application.persistentDataPath/anr/anr-<timestamp>.json`, atomically (written to `.part` and renamed), so a report is never read half-written.

`AnrWatchdog.GetReports()` reads every report on disk, oldest first, and leaves them there; `AnrWatchdog.ClearReports()` deletes them when you are done. Nothing removes them on your behalf, so reports survive across sessions and can be pulled off the device with adb. Each report carries the file it came from in `sourcePath`, which is how a caller tells apart the ones it has already handled.

Reading happens from C#, on the Unity main thread. An Android UI thread stall does not block the player loop, so a report written during one can usually be read in the same session, seconds after it happened. A stall of the Unity main thread blocks your code instead, and its report is read once the player loop runs again.

A report contains device and build context (`packageName`, `entry`, `appVersion`, `unityVersion`, `deviceModel`, `deviceApiLevel`, `abi`, `orientation`, window size, `foreground`, ...) plus two thread dumps:

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

## Report viewer (Editor)

**Window → Analysis → Android ANR Report** opens a report from disk and symbolicates it.

1. **Browse** to an `anr-*.json` pulled off the device.
2. **Browse** to the folder holding the unstripped libraries - the contents of the build's `symbols.zip`, with its per-ABI subfolders left as they are.
3. **Resolve symbols**.

The metadata table shows every field of the report; the tabs switch between Java and native threads, and selecting a thread shows its stack. After resolving, native frames gain a function name and a `file:line`, with `(+N inlined)` where the compiler inlined further frames into one address.

A library stripped of DWARF but still carrying a symbol table - which is what `libunity.so` normally is - resolves to function names anyway: anything `llvm-symbolizer` leaves unresolved gets a second pass through `llvm-nm`, naming the function an address falls inside plus its offset (`SomeFunction +0x24`). Those frames show `<symbol table only, no line info>` as their source, since a symbol table has no file or line data and cannot recover inlined frames.

Resolution runs `llvm-symbolizer` from the NDK the editor is configured with - one process per library, with all of that library's addresses on stdin, so a thousand-frame report costs a handful of process launches rather than a thousand. Symbol files are matched by name, accepting `libunity.so`, `libunity.sym.so`, `libunity.so.debug` and similar, preferring the subfolder that matches the report's ABI. Each library's `buildId` is checked against the symbol file with `llvm-readelf`, and a mismatch is reported rather than silently producing plausible but wrong names.

### Recording what the game was doing

A report says which threads were stuck, not what the app was busy with. Only the game knows that, so set it:

```csharp
AnrWatchdog.GameState = "loading level 3";
```

Whatever was set last is stored with every report from then on, in the `gameState` field. It can be set before `Start()`, survives a stop/start, and is free-form - a scene name, a loading step, whatever makes a report legible six months later. Set it from the Unity main thread.

## How it works

* `UiThreadWatchdog` (Java) posts a ticker `Runnable` to the Android UI thread's `Looper`. If the ticker has not run for `anrTimeoutMs`, it serializes `Thread.getAllStackTraces()` and the device context to JSON and calls into native code.
* The native library enumerates `/proc/self/task`, installs a handler for a real-time signal and interrupts each thread in turn. Each interrupted thread unwinds itself with `_Unwind_Backtrace` into a preallocated buffer, then signals the watchdog thread through a semaphore.
* Addresses are resolved to libraries with `dladdr` afterwards, on the watchdog thread - the signal handler itself allocates nothing and takes no locks, because a thread stalled mid-ANR may well be holding the allocator's.
* Build ids come from one `dl_iterate_phdr` pass per report, read out of each library's mapped `PT_NOTE` segment - no file access, and it works on stripped libraries.
* The native dump is merged into the Java report and written to disk.

## Package layout

```
Editor/
  AnrReportWindow.cs                  # report viewer and symbolication UI
  AnrReportWindow.uxml
  AnrSymbolicator.cs                  # batched llvm-symbolizer calls, build id verification
  AndroidToolchain.cs                 # finds the NDK and its LLVM tools
Runtime/
  AnrWatchdog.cs                      # public API
  AnrWatchdogSettings.cs
  AnrReport.cs                        # report deserialized with JsonUtility
  Plugins/Android/UnityAnrWatchdog.androidlib/
    build.gradle                      # Gradle library module, builds the native code with CMake
    src/main/java/com/unity3d/anrwatchdog/
      UnityAnrWatchdog.java           # entry point called from C#
      UiThreadWatchdog.java           # the watchdog thread
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
* A thread wedged in an uninterruptible kernel state - flash I/O, a page fault on a file mapping, some binder transactions - cannot run the capture signal handler: the kernel holds the signal pending until the syscall returns. Such a thread is reported with an empty stack after a 500 ms timeout, alongside the `/proc/<tid>/status` state that explains it, usually `D (disk sleep)`. Threads are captured one at a time, so this timeout is paid per unresponsive thread.
* At most 256 threads are captured; beyond that they are still listed, without stacks.
