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
        /// report directory can read them. Off by default; reports hold thread names and stacks,
        /// no user data, so turning it on costs little.
        /// <para>
        /// Turn this on to pull reports with "adb pull": adb runs as the shell user rather than
        /// as the app, and cannot read a file written owner-only.
        /// </para>
        /// <para>
        /// App processes run with umask 0077, so this takes an explicit fchmod - and the emulated
        /// storage volume synthesizes its own permissions and may ignore it, so check the result
        /// with "adb shell ls -l". Set it to false to leave the files owner-only.
        /// </para>
        /// </summary>
        public bool worldReadableReports;

        /// <summary>
        /// Write reports indented, so they can be read as they are. Off by default: indentation
        /// roughly doubles the file - a report with a hundred threads already runs to a few hundred
        /// kilobytes - and a shipping app uploads reports rather than reading them by hand. The
        /// Editor's report window does not care either way.
        /// </summary>
        public bool prettyJson;

        public static AnrWatchdogSettings Default => new AnrWatchdogSettings
        {
            anrTimeoutMs = 3000,
            pollIntervalMs = 300,
            reportIntervalMs = 10000,

            // Both off by default: the conservative choice for a shipping app, where reports are
            // uploaded by code rather than read by hand. Turn them on while debugging on a device.
            worldReadableReports = false,
            prettyJson = false
        };
    }
}
