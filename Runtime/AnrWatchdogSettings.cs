using System;

namespace Unity.Android
{
    /// <summary>
    /// Tuning for <see cref="AnrWatchdog.Start(AnrWatchdogSettings)"/>.
    /// </summary>
    [Serializable]
    public struct AnrWatchdogSettings
    {
        /// <summary>
        /// How long the Android UI thread has to be stuck before it counts as an ANR. Android
        /// itself raises an ANR after roughly 5 seconds of unresponsive input, so a lower value
        /// here catches the stall before the system does.
        /// </summary>
        public long anrTimeoutMs;

        /// <summary>How often the watchdog thread checks the Android UI thread.</summary>
        public long pollIntervalMs;

        /// <summary>
        /// Minimum interval between two reports, so an Android UI thread that stays stuck does not
        /// produce a report on every check.
        /// </summary>
        public long reportIntervalMs;

        /// <summary>
        /// Write reports as 0644 instead of 0600, so anything on the device that can reach the
        /// report directory can read them. Reports hold thread names and stacks, no user data.
        /// <para>
        /// Leave this on to pull reports with "adb pull": adb runs as the shell user rather than
        /// as the app, and cannot read a file written owner-only.
        /// </para>
        /// <para>
        /// App processes run with umask 0077, so this takes an explicit fchmod - and the emulated
        /// storage volume synthesizes its own permissions and may ignore it, so check the result
        /// with "adb shell ls -l". Set it to false to leave the files owner-only.
        /// </para>
        /// </summary>
        public bool worldReadableReports;

        public static AnrWatchdogSettings Default => new AnrWatchdogSettings
        {
            anrTimeoutMs = 3000,
            pollIntervalMs = 300,
            reportIntervalMs = 10000,
            worldReadableReports = true
        };
    }
}
