using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Unity.Android
{
    /// <summary>
    /// Detects ANRs (Application Not Responding) on Android by watching the Android UI thread from
    /// a background thread, and captures the Java and native stacks of every thread when that
    /// thread stops responding. The Unity main thread is a different thread and is not watched -
    /// Android only raises an ANR for its own UI thread.
    /// </summary>
    public static class AnrWatchdog
    {
        const string k_JavaClass = "com.unity3d.anrwatchdog.UnityAnrWatchdog";
        const string k_UnityPlayerClass = "com.unity3d.player.UnityPlayer";
        const string k_ReportDirectoryName = "anr";
        const string k_ReportSearchPattern = "anr-*.json";

        /// <summary>Directory the watchdog writes reports to.</summary>
        public static string ReportDirectory => Path.Combine(Application.persistentDataPath, k_ReportDirectoryName);

        /// <summary>Whether the watchdog thread is currently running.</summary>
        public static bool IsRunning
        {
            get
            {
                if (!IsAndroidPlayer)
                    return false;

                return Watchdog.CallStatic<bool>("isRunning");
            }
        }

        /// <summary>
        /// Kept for the lifetime of the process: constructing it costs a JNI class lookup and a
        /// global reference, and <see cref="IsRunning"/> is cheap enough to be polled.
        /// </summary>
        static AndroidJavaClass Watchdog => s_Watchdog ??= new AndroidJavaClass(k_JavaClass);

        static AndroidJavaClass s_Watchdog;

        /// <summary>
        /// The activity the player is running in. Cached: fetching it off UnityPlayer costs a JNI
        /// class lookup every time. (UnityEngine.Android.AndroidApplication would be tidier, but
        /// it only exists from Unity 6 on.)
        /// </summary>
        static AndroidJavaObject CurrentActivity
        {
            get
            {
                if (s_Activity == null)
                {
                    using (var player = new AndroidJavaClass(k_UnityPlayerClass))
                        s_Activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                }

                return s_Activity;
            }
        }

        static AndroidJavaObject s_Activity;

        /// <summary>
        /// Everything below this point talks to Java, which only exists in an Android player -
        /// the Editor reports the Android platform only from a device, never from play mode.
        /// </summary>
        static bool IsAndroidPlayer => Application.platform == RuntimePlatform.Android;

        /// <summary>
        /// What the game is doing - "loading", "menu", "level 3" - recorded with every report from
        /// now on, so a report says what the app was busy with rather than only which threads were
        /// stuck. Only the game knows this, so nothing sets it for you.
        /// <para>
        /// Can be set before <see cref="Start"/> and survives a stop/start. Set it from the Unity
        /// main thread.
        /// </para>
        /// </summary>
        public static string GameState
        {
            get => s_GameState;
            set
            {
                s_GameState = value ?? string.Empty;

                if (IsAndroidPlayer)
                    Watchdog.CallStatic("setGameState", s_GameState);
            }
        }

        static string s_GameState = string.Empty;

        /// <summary>Starts the watchdog with <see cref="AnrWatchdogSettings.Default"/>.</summary>
        public static void Start() => Start(AnrWatchdogSettings.Default);

        /// <summary>
        /// Starts the watchdog. Calling this while it is already running does nothing - stop it
        /// first to apply different settings.
        /// </summary>
        public static void Start(AnrWatchdogSettings settings)
        {
            if (!IsAndroidPlayer)
            {
                Debug.LogWarning($"{nameof(AnrWatchdog)} only runs in an Android player.");
                return;
            }

            Directory.CreateDirectory(ReportDirectory);

            Watchdog.CallStatic("start",
                CurrentActivity,
                ReportDirectory,
                settings.anrTimeoutMs,
                settings.pollIntervalMs,
                settings.reportIntervalMs,
                Application.unityVersion,
                ScriptingBackend,
                BuildType,
                settings.worldReadableReports,
                settings.prettyJson);
        }

        /// <summary>Stops the watchdog. Reports already on disk are left untouched.</summary>
        public static void Stop()
        {
            if (!IsAndroidPlayer)
                return;

            Watchdog.CallStatic("stop");
        }

        /// <summary>
        /// Reads every report written so far, oldest first, and leaves them on disk - call
        /// <see cref="ClearReports"/> to remove them. A report written during an Android UI thread
        /// stall can be read straight away, since the Unity main thread keeps running; one written
        /// while the Unity main thread itself was blocked is only read once it recovers.
        /// </summary>
        public static AnrReport[] GetReports()
        {
            var files = GetReportFiles();
            if (files.Length == 0)
                return Array.Empty<AnrReport>();

            var reports = new List<AnrReport>(files.Length);
            foreach (var file in files)
            {
                try
                {
                    var report = JsonUtility.FromJson<AnrReport>(File.ReadAllText(file));
                    if (report == null)
                        continue;

                    report.sourcePath = file;
                    reports.Add(report);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"Failed to read ANR report '{file}': {exception.Message}");
                }
            }

            return reports.ToArray();
        }

        /// <summary>
        /// Deletes every report on disk. Anything written after the last <see cref="GetReports"/>
        /// call goes with them, so read before clearing.
        /// </summary>
        public static void ClearReports()
        {
            foreach (var file in GetReportFiles())
            {
                try
                {
                    File.Delete(file);
                    Debug.Log($"Deleted ANR report '{file}'.");
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"Failed to delete ANR report '{file}': {exception.Message}");
                }
            }
        }

        static string[] GetReportFiles()
        {
            if (!Directory.Exists(ReportDirectory))
                return Array.Empty<string>();

            var files = Directory.GetFiles(ReportDirectory, k_ReportSearchPattern);
            Array.Sort(files, StringComparer.Ordinal); // File names are timestamps, so this is chronological.
            return files;
        }

        static string ScriptingBackend =>
#if ENABLE_IL2CPP
            "IL2CPP";
#else
            "Mono";
#endif

        static string BuildType => Debug.isDebugBuild ? "Development" : "Release";
    }
}
