using System;
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
    ScrollView m_LogScroll;
    VisualElement m_LogEntries;

    AnrWatchdogSettings m_Settings = AnrWatchdogSettings.Default;

    float m_NextStatusRefresh;
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
        m_LogScroll = root.Q<ScrollView>("log-scroll");
        m_LogEntries = root.Q<VisualElement>("log-entries");

        m_StallDuration.RegisterValueChangedCallback(_ => RefreshStallDurationLabel());
        RefreshStallDurationLabel();

        root.Q<Button>("stall-ui-thread").clicked += StallAndroidUiThread;
        root.Q<Button>("stall-main-thread").clicked += StallUnityMainThread;
        root.Q<Button>("clear-log").clicked += ClearLog;
        m_ToggleWatchdog.clicked += ToggleWatchdog;

        AnrWatchdog.AnrDetected += OnAnrDetected;

        AppendLog(LogKind.Info, $"Sample started, Unity {Application.unityVersion}");
        StartWatchdog();
        RefreshStatus();
    }

    void OnDestroy()
    {
        AnrWatchdog.AnrDetected -= OnAnrDetected;
    }

    void Update()
    {
        TrackFrameTime();

        if (Time.unscaledTime < m_NextStatusRefresh)
            return;

        m_NextStatusRefresh = Time.unscaledTime + k_StatusRefreshSeconds;
        RefreshStatus();
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
            StartWatchdog();
        }

        RefreshStatus();
    }

    void StartWatchdog()
    {
        AnrWatchdog.Start(m_Settings);
        AppendLog(LogKind.Info,
            AnrWatchdog.IsRunning
                ? $"Watchdog started, reports go to {AnrWatchdog.ReportDirectory}"
                : "Watchdog did not start - an Android player is required");
    }

    void OnAnrDetected(AnrReport report)
    {
        m_ReportCount++;

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
