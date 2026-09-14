using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Unity.Android
{
    /// <summary>
    /// Detects ANRs (Application Not Responding) on Android by watching the main thread from a
    /// background thread, and captures the Java and native stacks of every thread when the main
    /// thread stops responding.
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

                using (var watchdog = new AndroidJavaClass(k_JavaClass))
                    return watchdog.CallStatic<bool>("isRunning");
            }
        }

        /// <summary>
        /// Everything below this point talks to Java, which only exists in an Android player -
        /// the Editor reports the Android platform only from a device, never from play mode.
        /// </summary>
        static bool IsAndroidPlayer => Application.platform == RuntimePlatform.Android;

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

            using (var player = new AndroidJavaClass(k_UnityPlayerClass))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var watchdog = new AndroidJavaClass(k_JavaClass))
            {
                watchdog.CallStatic("start",
                    activity,
                    ReportDirectory,
                    settings.anrTimeoutMs,
                    settings.pollIntervalMs,
                    settings.reportIntervalMs,
                    Application.unityVersion,
                    ScriptingBackend,
                    BuildType,
                    settings.worldReadableReports);
            }

        }

        /// <summary>Stops the watchdog. Reports already on disk are left untouched.</summary>
        public static void Stop()
        {
            if (!IsAndroidPlayer)
                return;

            using (var watchdog = new AndroidJavaClass(k_JavaClass))
                watchdog.CallStatic("stop");
        }

        /// <summary>
        /// Reads every report written so far, oldest first, and leaves them on disk - call
        /// <see cref="ClearReports"/> to remove them. Reports are only readable once the main
        /// thread recovers, since the stall blocks script execution as well.
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
