package com.unity3d.anrwatchdog;

import android.app.Activity;
import android.content.Context;

import java.io.File;

/**
 * Entry point used from C# (Unity.Android.AnrWatchdog). Everything the watchdog needs is passed
 * in, so this module has no compile-time dependency on the Unity player classes.
 */
public final class UnityAnrWatchdog
{
    private static MainThreadWatchdog s_Watchdog;

    private UnityAnrWatchdog() {}

    public static synchronized void start(
        Activity activity,
        String reportDirectory,
        long anrTimeoutMs,
        long pollIntervalMs,
        long reportIntervalMs,
        String unityVersion,
        String scriptingBackend,
        String buildType,
        boolean worldReadableReports)
    {
        if (s_Watchdog != null)
            return;

        Context context = activity != null ? activity.getApplicationContext() : null;
        if (context == null)
            throw new IllegalArgumentException("activity must not be null");

        MainThreadWatchdog watchdog = new MainThreadWatchdog(context, activity, new File(reportDirectory));
        watchdog.setANRTimeout(anrTimeoutMs);
        watchdog.setANRPollInterval(pollIntervalMs);
        watchdog.setANRReportInterval(reportIntervalMs);
        watchdog.setEngineMetadata(unityVersion, scriptingBackend, buildType);
        watchdog.setWorldReadableReports(worldReadableReports);
        watchdog.setDaemon(true);
        watchdog.start();

        s_Watchdog = watchdog;
    }

    public static synchronized void stop()
    {
        if (s_Watchdog == null)
            return;

        s_Watchdog.interrupt();
        s_Watchdog = null;
    }

    public static synchronized boolean isRunning()
    {
        return s_Watchdog != null;
    }
}
