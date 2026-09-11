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

        /// <summary>
        /// Raised on the main thread once it recovers, for every report the watchdog produced
        /// while it was stuck. Requires <see cref="AnrWatchdogSettings.reportPollIntervalSeconds"/>
        /// to be greater than zero.
        /// </summary>
        public static event Action<AnrReport> AnrDetected;

        /// <summary>Directory the watchdog writes reports to.</summary>
        public static string ReportDirectory => Path.Combine(Application.persistentDataPath, k_ReportDirectoryName);

        /// <summary>Whether the watchdog thread is currently running.</summary>
        public static bool IsRunning
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                using (var watchdog = new AndroidJavaClass(k_JavaClass))
                    return watchdog.CallStatic<bool>("isRunning");
#else
                return false;
#endif
            }
        }

        /// <summary>Starts the watchdog with <see cref="AnrWatchdogSettings.Default"/>.</summary>
        public static void Start() => Start(AnrWatchdogSettings.Default);

        /// <summary>
        /// Starts the watchdog. Calling this while it is already running does nothing - stop it
        /// first to apply different settings.
        /// </summary>
        public static void Start(AnrWatchdogSettings settings)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
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
                    BuildType);
            }

            if (settings.reportPollIntervalSeconds > 0.0f)
                AnrReportPoller.Run(settings.reportPollIntervalSeconds);
#else
            Debug.LogWarning($"{nameof(AnrWatchdog)} only runs in an Android player.");
#endif
        }

        /// <summary>Stops the watchdog. Reports already on disk are left untouched.</summary>
        public static void Stop()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            AnrReportPoller.Shutdown();

            using (var watchdog = new AndroidJavaClass(k_JavaClass))
                watchdog.CallStatic("stop");
#endif
        }

        /// <summary>
        /// Reads and removes every report written so far. Reports are only readable once the main
        /// thread recovers, since the stall blocks script execution as well.
        /// </summary>
        public static AnrReport[] TakePendingReports()
        {
            if (!Directory.Exists(ReportDirectory))
                return Array.Empty<AnrReport>();

            var files = Directory.GetFiles(ReportDirectory, k_ReportSearchPattern);
            Array.Sort(files, StringComparer.Ordinal); // File names are timestamps, so this is chronological.

            var reports = new List<AnrReport>(files.Length);
            foreach (var file in files)
            {
                try
                {
                    var report = JsonUtility.FromJson<AnrReport>(File.ReadAllText(file));
                    if (report != null)
                        reports.Add(report);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"Failed to read ANR report '{file}': {exception.Message}");
                }

                try
                {
                    File.Delete(file);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"Failed to delete ANR report '{file}': {exception.Message}");
                }
            }

            return reports.ToArray();
        }

        internal static void RaiseAnrDetected(AnrReport report) => AnrDetected?.Invoke(report);

        static string ScriptingBackend =>
#if ENABLE_IL2CPP
            "IL2CPP";
#else
            "Mono";
#endif

        static string BuildType => Debug.isDebugBuild ? "Development" : "Release";
    }
}
