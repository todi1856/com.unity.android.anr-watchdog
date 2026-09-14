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
    private static UiThreadWatchdog s_Watchdog;

    // Kept here as well as on the watchdog, so it survives a stop/start and can be set before the
    // watchdog is ever started.
    private static String s_GameState = "";

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
        boolean worldReadableReports,
        boolean prettyJson)
    {
        if (s_Watchdog != null)
            return;

        // Logged rather than thrown: this runs from the game's startup path, and a diagnostics
        // tool that cannot start is no reason to take the app down with it.
        Context context = activity != null ? activity.getApplicationContext() : null;
        if (context == null)
        {
            android.util.Log.e("Unity", "ANR watchdog not started: no activity was supplied.");
            return;
        }

        UiThreadWatchdog watchdog = new UiThreadWatchdog(context, activity, new File(reportDirectory));
        watchdog.setANRTimeout(anrTimeoutMs);
        watchdog.setANRPollInterval(pollIntervalMs);
        watchdog.setANRReportInterval(reportIntervalMs);
        watchdog.setEngineMetadata(unityVersion, scriptingBackend, buildType);
        watchdog.setGameState(s_GameState);
        watchdog.setWorldReadableReports(worldReadableReports);
        watchdog.setPrettyJson(prettyJson);
        watchdog.setDaemon(true);
        watchdog.start();

        s_Watchdog = watchdog;
    }

    /**
     * What the game is doing - "loading", "menu", "level 3" - recorded with every report from now
     * on. Can be set before the watchdog is started.
     */
    public static synchronized void setGameState(String state)
    {
        s_GameState = state == null ? "" : state;
        if (s_Watchdog != null)
            s_Watchdog.setGameState(s_GameState);
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
