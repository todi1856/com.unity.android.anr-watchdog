using Unity.Android;

/// <summary>
/// The settings this sample runs the watchdog with, in one place so that Entry - which starts it -
/// and the viewer's Start/Stop button - which restarts it - cannot drift apart.
/// </summary>
static class AnrSampleSettings
{
    public static AnrWatchdogSettings Create()
    {
        var settings = AnrWatchdogSettings.Default;

        // Both default to off, which is what a shipping app wants. This sample exists to be poked
        // at by hand, so it writes reports indented and readable by adb pull.
        settings.prettyJson = true;
        settings.worldReadableReports = true;

        return settings;
    }
}
