# ANR Watchdog for Unity Android

A utility for detecting **ANR** (Application Not Responding) conditions in Unity players running on Android, and for reporting where the main thread was stuck when it stopped responding.

### What is an ANR?

Android raises an ANR when the app's main thread fails to respond to input or to service its message queue within a system-defined timeout. On Unity players this usually means the main thread is blocked inside script code, a native call or a synchronous load. Because the process is frozen, the stall is hard to catch with the profiler - the watchdog observes it from a background thread instead and captures the state of the main thread at the moment it stopped ticking.

__Note:__ Detection is done from a background thread, so it keeps working while the main thread is unresponsive.

## Requirements

* [Git](https://git-scm.com/install/)
* Unity **6000.0.0f1** or higher
* Android as the active build target

## Installation

1. In Unity, open **Window → Package Manager**.
2. In the top-left corner, click the **+** button and select **Install package from Git URL**.
3. Enter the following HTTPS url:
   ```
   https://github.com/todi1856/com.unity.android.anr-watchdog.git
   ```

## Quick Start

1. Add the package to your project.
2. Build and run on an Android device.
3. Reports are written to logcat - filter by the watchdog tag to inspect them.

## Options

_TODO: document the watchdog configuration (timeout threshold, reporting sink, whether to start automatically)._

## Reports

_TODO: document the report format and how to read the captured main thread state._
