# ANR Watchdog sample project

A small Unity project that consumes the package from `../../..` and exercises the watchdog on a device.

## Running it

1. Open the project and switch the platform to **Android**.
2. Make sure `Assets/DefaultScene.unity` is in **File → Build Profiles / Build Settings**.
3. Build and run on a device.

The UI is created at runtime by `AnrSampleBootstrap`, so any scene works - in the Editor you can simply press Play. The watchdog itself only runs in an Android player; in the Editor the UI comes up and reports that it did not start.

## What the UI shows

* **Top panel** - live state: whether the watchdog is running and with what thresholds, current and worst recent frame time, pause/focus/quit counters, how many ANR reports have arrived, and the device. Below that, a stall duration slider and the buttons:
  * **Stall Android UI thread** - posts a sleeping runnable to the activity's UI thread. This is the one that produces a real ANR: Android watches that thread, and so does the watchdog.
  * **Stall Unity main thread** - sleeps in the player loop. Rendering freezes, but Android keeps servicing its own message queue, so no ANR is raised. Included to make the distinction visible.
  * **Start/Stop watchdog** and **Clear log**.
* **Bottom panel** - the event log: lifecycle callbacks, long Unity frames, stalls as they are triggered, and a summary of every ANR report the watchdog produces.

Because the Unity player loop runs on a different thread from the Android UI thread, the app keeps rendering while the UI thread is stalled - you can watch the report arrive in the log a few seconds after pressing the button.
