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
        /// How long the main thread has to be stuck before it counts as an ANR. Android itself
        /// raises an ANR after roughly 5 seconds of unresponsive input, so a lower value here
        /// catches the stall before the system does.
        /// </summary>
        public long anrTimeoutMs;

        /// <summary>How often the watchdog thread checks the main thread.</summary>
        public long pollIntervalMs;

        /// <summary>
        /// Minimum interval between two reports, so a main thread that stays stuck does not
        /// produce a report on every check.
        /// </summary>
        public long reportIntervalMs;

        /// <summary>
        /// How often C# checks the report directory for new reports to raise
        /// <see cref="AnrWatchdog.AnrDetected"/> for. Zero disables polling, leaving
        /// <see cref="AnrWatchdog.TakePendingReports"/> as the only way to collect them.
        /// </summary>
        public float reportPollIntervalSeconds;

        public static AnrWatchdogSettings Default => new AnrWatchdogSettings
        {
            anrTimeoutMs = 3000,
            pollIntervalMs = 300,
            reportIntervalMs = 10000,
            reportPollIntervalSeconds = 1.0f
        };
    }
}
