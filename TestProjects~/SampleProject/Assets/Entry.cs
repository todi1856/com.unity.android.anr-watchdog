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
        foreach (var report in AnrWatchdog.GetReports())
            Debug.Log($"ANR from an earlier session: {report.anrTimeMs} ms stall, {report.reportTimeStamp}");

        // Clearing before starting, so a report written moments from now is not deleted unread.
        AnrWatchdog.ClearReports();
        AnrWatchdog.Start();
    }
}
