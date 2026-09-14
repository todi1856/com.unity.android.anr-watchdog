using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Android;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Drives the sample UI: the top panel shows what the app and the watchdog are doing right now
/// and offers the buttons that provoke an ANR, the bottom panel logs everything that happened.
/// </summary>
[RequireComponent(typeof(UIDocument))]
public class AnrSampleWindow : MonoBehaviour
{
    enum LogKind
    {
        Info,
        Warning,
        Lifecycle,
        Anr
    }

    const int k_MaxLogEntries = 200;
    const float k_StatusRefreshSeconds = 0.25f;
    const float k_ReportPollSeconds = 1.0f;
    const float k_LongFrameSeconds = 0.5f;
    const float k_WorstFrameWindowSeconds = 3.0f;

    Label m_WatchdogStatus;
    Label m_FrameStatus;
    Label m_LifecycleStatus;
    Label m_ReportStatus;
    Label m_DeviceStatus;
    Label m_StallDurationLabel;
    Label m_LogCount;
    Slider m_StallDuration;
    Button m_ToggleWatchdog;
    Button m_ViewReport;
    ScrollView m_LogScroll;
    VisualElement m_LogEntries;
    VisualElement m_ReportView;
    ListView m_ReportList;
    Label m_ReportTitle;

    AnrReport m_LastReport;
    List<AnrReportView.Line> m_ReportLines = new List<AnrReportView.Line>();

    AnrWatchdogSettings m_Settings = AnrWatchdogSettings.Default;

    float m_NextStatusRefresh;
    float m_NextReportPoll;

    // Reports stay on disk until they are cleared, so the ones already logged are remembered.
    readonly HashSet<string> m_LoggedReports = new HashSet<string>();
    float m_WorstFrameSeconds;
    float m_WorstFrameExpiry;

    int m_PauseCount;
    int m_ResumeCount;
    int m_FocusGainedCount;
    int m_FocusLostCount;
    int m_QuitCount;

    int m_ReportCount;
    string m_LastReportSummary = "none yet";

    void Start()
    {
        var root = GetComponent<UIDocument>().rootVisualElement;

        m_WatchdogStatus = root.Q<Label>("status-watchdog");
        m_FrameStatus = root.Q<Label>("status-frame");
        m_LifecycleStatus = root.Q<Label>("status-lifecycle");
        m_ReportStatus = root.Q<Label>("status-reports");
        m_DeviceStatus = root.Q<Label>("status-device");
        m_StallDurationLabel = root.Q<Label>("stall-duration-label");
        m_LogCount = root.Q<Label>("log-count");
        m_StallDuration = root.Q<Slider>("stall-duration");
        m_ToggleWatchdog = root.Q<Button>("toggle-watchdog");
        m_ViewReport = root.Q<Button>("view-report");
        m_LogScroll = root.Q<ScrollView>("log-scroll");
        m_LogEntries = root.Q<VisualElement>("log-entries");
        m_ReportView = root.Q<VisualElement>("report-view");
        m_ReportList = root.Q<ListView>("report-list");
        m_ReportTitle = root.Q<Label>("report-title");

        SetUpReportList();

        m_StallDuration.RegisterValueChangedCallback(_ => RefreshStallDurationLabel());
        RefreshStallDurationLabel();

        root.Q<Button>("stall-ui-thread").clicked += StallAndroidUiThread;
        root.Q<Button>("stall-main-thread").clicked += StallUnityMainThread;
        root.Q<Button>("clear-log").clicked += ClearLog;
        root.Q<Button>("clear-reports").clicked += ClearReports;
        root.Q<Button>("close-report").clicked += HideReport;
        m_ToggleWatchdog.clicked += ToggleWatchdog;
        m_ViewReport.clicked += ShowReport;
        m_ViewReport.SetEnabled(false);

#if UNITY_EDITOR
        // The watchdog only runs in an Android player, so in the Editor there is never a real
        // report - preload a synthetic one so the viewer can be worked on without deploying.
        m_LastReport = AnrSampleReportFixture.Create();
        m_ViewReport.SetEnabled(true);
#endif

        AppendLog(LogKind.Info, $"Sample started, Unity {Application.unityVersion}");
#if UNITY_EDITOR
        AppendLog(LogKind.Info, "Editor: 'View last report' shows sample data until a real report arrives");
#endif
        // Entry owns starting the watchdog; this window only reports on it and can toggle it.
        AppendLog(LogKind.Info, AnrWatchdog.IsRunning
            ? $"Watchdog is running, reports go to {AnrWatchdog.ReportDirectory}"
            : "Watchdog is not running - an Android player is required");

        RefreshStatus();
    }

    void Update()
    {
        TrackFrameTime();
        PollForReports();

        if (Time.unscaledTime < m_NextStatusRefresh)
            return;

        m_NextStatusRefresh = Time.unscaledTime + k_StatusRefreshSeconds;
        RefreshStatus();
    }

    /// <summary>
    /// The package writes reports and leaves them on disk; noticing them is the app's job. This is
    /// also how reports from an earlier session - one the ANR actually killed - surface here.
    /// </summary>
    void PollForReports()
    {
        if (Time.unscaledTime < m_NextReportPoll)
            return;

        m_NextReportPoll = Time.unscaledTime + k_ReportPollSeconds;

        foreach (var report in AnrWatchdog.GetReports())
        {
            if (m_LoggedReports.Add(report.sourcePath))
                OnAnrDetected(report);
        }
    }

    void TrackFrameTime()
    {
        var frameSeconds = Time.unscaledDeltaTime;

        if (frameSeconds > m_WorstFrameSeconds || Time.unscaledTime > m_WorstFrameExpiry)
        {
            m_WorstFrameSeconds = frameSeconds;
            m_WorstFrameExpiry = Time.unscaledTime + k_WorstFrameWindowSeconds;
        }

        // A frame this long is a hitch worth recording even when Android never noticed it.
        if (frameSeconds > k_LongFrameSeconds)
            AppendLog(LogKind.Warning, $"Unity main thread was blocked for {frameSeconds * 1000.0f:F0} ms");
    }

    void RefreshStatus()
    {
        m_WatchdogStatus.text = AnrWatchdog.IsRunning
            ? $"Watchdog: running (timeout {m_Settings.anrTimeoutMs} ms, poll {m_Settings.pollIntervalMs} ms)"
            : "Watchdog: stopped";

        m_FrameStatus.text =
            $"Frame: {Time.unscaledDeltaTime * 1000.0f:F1} ms   worst of last {k_WorstFrameWindowSeconds:F0} s: {m_WorstFrameSeconds * 1000.0f:F1} ms";

        m_LifecycleStatus.text =
            $"Pause {m_PauseCount}/{m_ResumeCount}   Focus {m_FocusGainedCount}/{m_FocusLostCount}   Quit {m_QuitCount}";

        m_ReportStatus.text = $"Reports: {m_ReportCount}   last: {m_LastReportSummary}";

        m_DeviceStatus.text = $"{SystemInfo.deviceModel}   {SystemInfo.operatingSystem}";

        m_ToggleWatchdog.text = AnrWatchdog.IsRunning ? "Stop watchdog" : "Start watchdog";
    }

    void RefreshStallDurationLabel()
    {
        m_StallDurationLabel.text = $"Stall duration: {m_StallDuration.value:F1} s";
    }

    void StallAndroidUiThread()
    {
        var seconds = m_StallDuration.value;
        AppendLog(LogKind.Info, $"Stalling the Android UI thread for {seconds:F1} s");

        if (!AnrSampleStalls.StallAndroidUiThread(seconds, out var error))
            AppendLog(LogKind.Warning, $"Could not stall the Android UI thread: {error}");
    }

    void StallUnityMainThread()
    {
        var seconds = m_StallDuration.value;
        AppendLog(LogKind.Info, $"Stalling the Unity main thread for {seconds:F1} s - no ANR expected");

        // Nothing renders until this returns, so the entry above is only visible afterwards.
        AnrSampleStalls.StallUnityMainThread(seconds);
    }

    void ToggleWatchdog()
    {
        if (AnrWatchdog.IsRunning)
        {
            AnrWatchdog.Stop();
            AppendLog(LogKind.Info, "Watchdog stopped");
        }
        else
        {
            // Restarting from here applies this window's settings rather than Entry's defaults.
            AnrWatchdog.Start(m_Settings);
            AppendLog(LogKind.Info,
                AnrWatchdog.IsRunning
                    ? $"Watchdog started, reports go to {AnrWatchdog.ReportDirectory}"
                    : "Watchdog did not start - an Android player is required");
        }

        RefreshStatus();
    }

    void SetUpReportList()
    {
        m_ReportList.itemsSource = m_ReportLines;
        m_ReportList.selectionType = SelectionType.None;
        m_ReportList.makeItem = () =>
        {
            var label = new Label();
            label.AddToClassList("report-line");
            return label;
        };
        // Inline styles rather than class toggles: every class change re-runs selector matching
        // for that element, and binding happens for every row that scrolls into view.
        m_ReportList.bindItem = (element, index) =>
        {
            var line = m_ReportLines[index];
            var label = (Label)element;

            label.text = line.Text;
            label.style.color = ColorFor(line.Kind);
            label.style.unityFontStyleAndWeight = line.Kind == AnrReportView.LineKind.Section ||
                                                  line.Kind == AnrReportView.LineKind.Thread ||
                                                  line.Kind == AnrReportView.LineKind.MainThread
                ? FontStyle.Bold
                : FontStyle.Normal;
            label.style.fontSize = line.Kind == AnrReportView.LineKind.Section ? 22 : 18;
        };
    }

    static Color ColorFor(AnrReportView.LineKind kind) => kind switch
    {
        AnrReportView.LineKind.Section => new Color32(240, 243, 247, 255),
        AnrReportView.LineKind.Thread => new Color32(130, 200, 255, 255),
        AnrReportView.LineKind.MainThread => new Color32(255, 138, 128, 255),
        AnrReportView.LineKind.Frame => new Color32(176, 183, 192, 255),
        AnrReportView.LineKind.Note => new Color32(120, 127, 136, 255),
        _ => new Color32(198, 205, 214, 255)
    };

    void ShowReport()
    {
        if (m_LastReport == null)
            return;

        // Show first, fill second: a subtree with display:none is not laid out, and elements
        // measured in that state keep a zero height and end up drawn on top of each other.
        m_ReportView.RemoveFromClassList("hidden");

        m_ReportTitle.text = AnrReportView.Title(m_LastReport);

        m_ReportLines.Clear();
        m_ReportLines.AddRange(AnrReportView.Build(m_LastReport));
        m_ReportList.Rebuild();
        m_ReportList.ScrollToItem(0);
    }

    void HideReport()
    {
        m_ReportView.AddToClassList("hidden");
    }

    void OnAnrDetected(AnrReport report)
    {
        m_ReportCount++;
        m_LastReport = report;
        m_ViewReport.SetEnabled(true);

        // Keep the overlay on the newest report if it happens to be open.
        if (!m_ReportView.ClassListContains("hidden"))
            ShowReport();

        var mainThread = report.javaThreads?.FirstOrDefault(thread => thread.name == "main");
        var topFrame = mainThread?.stackTrace is { Length: > 0 }
            ? $"{mainThread.stackTrace[0].className}.{mainThread.stackTrace[0].methodName}"
            : "unknown";

        m_LastReportSummary = $"{report.anrTimeMs} ms stall, {report.javaThreads?.Length ?? 0} java / {report.nativeThreads?.Length ?? 0} native threads";

        AppendLog(LogKind.Anr, $"ANR after {report.anrTimeMs} ms - main thread in {topFrame}");
        AppendLog(LogKind.Anr, $"   {report.javaThreads?.Length ?? 0} java threads, {report.nativeThreads?.Length ?? 0} native threads, abi {report.abi}");

        RefreshStatus();
    }

    void AppendLog(LogKind kind, string message)
    {
        if (m_LogEntries == null)
            return;

        var entry = new Label($"{DateTime.Now:HH:mm:ss.fff}  {message}");
        entry.AddToClassList("log-entry");
        switch (kind)
        {
            case LogKind.Anr: entry.AddToClassList("log-entry--anr"); break;
            case LogKind.Warning: entry.AddToClassList("log-entry--warning"); break;
            case LogKind.Lifecycle: entry.AddToClassList("log-entry--lifecycle"); break;
        }

        m_LogEntries.Add(entry);

        while (m_LogEntries.childCount > k_MaxLogEntries)
            m_LogEntries.RemoveAt(0);

        m_LogCount.text = $"{m_LogEntries.childCount} entries";

        // Layout has not run yet for the new entry, so scrolling waits a frame.
        m_LogScroll.schedule.Execute(() => m_LogScroll.scrollOffset =
            new Vector2(0, m_LogScroll.contentContainer.layout.height));
    }

    void ClearReports()
    {
        var reportCount = AnrWatchdog.GetReports().Length;
        AnrWatchdog.ClearReports();
        m_LoggedReports.Clear();

        AppendLog(LogKind.Info, reportCount > 0
            ? $"Deleted {reportCount} report(s) from {AnrWatchdog.ReportDirectory}"
            : "No reports on disk to delete");
    }

    void ClearLog()
    {
        m_LogEntries.Clear();
        m_LogCount.text = "0 entries";
    }

    void OnApplicationPause(bool paused)
    {
        if (paused)
            m_PauseCount++;
        else
            m_ResumeCount++;

        AppendLog(LogKind.Lifecycle, paused ? "OnApplicationPause(true)" : "OnApplicationPause(false)");
    }

    void OnApplicationFocus(bool focused)
    {
        if (focused)
            m_FocusGainedCount++;
        else
            m_FocusLostCount++;

        AppendLog(LogKind.Lifecycle, focused ? "OnApplicationFocus(true)" : "OnApplicationFocus(false)");
    }

    void OnApplicationQuit()
    {
        m_QuitCount++;
        AppendLog(LogKind.Lifecycle, "OnApplicationQuit()");
    }
}
