using Unity.Android;
using UnityEngine;

/// <summary>
/// Owns the watchdog's lifecycle for the sample, in the shape a real integration would use: read
/// what the previous session left behind, clear it, then start watching.
/// </summary>
public class Entry : MonoBehaviour
{
    void Start()
    {
        // Reports that outlived the session that produced them - including one from an ANR that
        // killed the app, which is the only way such a report is ever seen.
        //
        // This sample only logs them, because it shows the reports on screen instead. A real
        // integration would ship them off the device here for offline diagnostics: upload them to
        // a crash reporting service, your own backend, or anywhere you can read them later. Each
        // report is a self-contained JSON file, still on disk at report.sourcePath, carrying the
        // build ids needed to symbolicate its native frames against symbols.zip once it arrives.
        // The package itself never touches the network, so nothing leaves the device unless you
        // send it.
        foreach (var report in AnrWatchdog.GetReports())
            Debug.Log($"ANR from an earlier session: {report.anrTimeMs} ms stall, {report.reportTimeStamp}");

        // Clearing before starting, so a report written moments from now is not deleted unread.
        AnrWatchdog.ClearReports();
        AnrWatchdog.Start();
    }
}
