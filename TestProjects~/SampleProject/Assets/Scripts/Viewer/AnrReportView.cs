using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Android;
using UnityEngine.UIElements;

/// <summary>
/// Flattens an <see cref="AnrReport"/> - metadata plus every thread and its stack - into one line
/// per row, ready to be shown in a virtualized <see cref="ListView"/>.
/// </summary>
static class AnrReportView
{
    public enum LineKind
    {
        Field,
        Section,
        Thread,

        /// <summary>The Android UI thread - the one the report is about.</summary>
        UiThread,
        Frame,
        Note
    }

    public readonly struct Line
    {
        public readonly string Text;
        public readonly LineKind Kind;

        public Line(string text, LineKind kind)
        {
            Text = text;
            Kind = kind;
        }
    }

    public static string Title(AnrReport report) => $"ANR report - {report.reportTimeStamp}";

    public static List<Line> Build(AnrReport report)
    {
        var lines = new List<Line>(512);

        Add(lines, LineKind.Field, $"Type: {report.reportType}");
        Add(lines, LineKind.Field, $"Stalled for {report.anrTimeMs} ms");
        Add(lines, LineKind.Field, $"Package: {report.packageName}   Entry: {report.entry}");
        Add(lines, LineKind.Field, $"App version: {report.appVersion} ({report.appVersionCode})");
        Add(lines, LineKind.Field, $"Game state: {(string.IsNullOrEmpty(report.gameState) ? "<not set>" : report.gameState)}");
        Add(lines, LineKind.Field, $"Unity {report.unityVersion}   {report.scriptingBackend}   {report.buildType}");
        Add(lines, LineKind.Field, $"Device: {report.deviceModel}   API {report.deviceApiLevel}   {report.abi}");
        Add(lines, LineKind.Field, $"Fingerprint: {report.deviceFingerPrint}");
        Add(lines, LineKind.Field, $"Foreground: {report.foreground} (importance {report.processImportance})");
        Add(lines, LineKind.Field, $"Orientation: {report.orientation}   Multi window: {report.multiWindow}");
        Add(lines, LineKind.Field, $"Window: {report.windowWidthPx}x{report.windowHeightPx} px, " +
                                   $"{report.windowWidthDp}x{report.windowHeightDp} dp, {report.densityDpi} dpi");
        Add(lines, LineKind.Field, $"Process {report.processId}   User {report.userId}");

        AddModules(lines, report);
        AddJavaThreads(lines, report);
        AddNativeThreads(lines, report);

        return lines;
    }

    /// <summary>
    /// Build ids are per library, so they are listed once here rather than repeated on every
    /// frame - this is what an offline symbolicator needs to pick the matching binary.
    /// </summary>
    static void AddModules(List<Line> lines, AnrReport report)
    {
        var modules = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var thread in report.nativeThreads ?? Array.Empty<AnrReport.NativeThread>())
        {
            foreach (var frame in thread.stackTrace ?? Array.Empty<AnrReport.NativeStackFrame>())
            {
                if (!string.IsNullOrEmpty(frame.libraryName))
                    modules[frame.libraryName] = frame.buildId;
            }
        }

        if (modules.Count == 0)
            return;

        Add(lines, LineKind.Section, $"Modules ({modules.Count})");
        foreach (var module in modules)
        {
            var buildId = string.IsNullOrEmpty(module.Value) ? "<no build id>" : module.Value;
            Add(lines, LineKind.Frame, $"    {Path.GetFileName(module.Key)}   {buildId}");
        }
    }

    static void AddJavaThreads(List<Line> lines, AnrReport report)
    {
        var threads = report.javaThreads ?? Array.Empty<AnrReport.JavaThread>();
        Add(lines, LineKind.Section, $"Java threads ({threads.Length})");

        // The stuck one is what the report is about, so it goes first.
        foreach (var thread in threads.OrderBy(t => IsAndroidUiThread(t) ? 0 : 1).ThenBy(t => t.name))
        {
            Add(lines, IsAndroidUiThread(thread) ? LineKind.UiThread : LineKind.Thread,
                $"{thread.name}  (id {thread.id}, {thread.state}, priority {thread.priority})");

            AddFrames(lines, thread.stackTrace?.Select(FormatJavaFrame));
        }
    }

    static void AddNativeThreads(List<Line> lines, AnrReport report)
    {
        var threads = report.nativeThreads ?? Array.Empty<AnrReport.NativeThread>();
        Add(lines, LineKind.Section, $"Native threads ({threads.Length})");

        // A tid equal to the pid is the process's first thread, which on Android is the UI thread.
        foreach (var thread in threads.OrderBy(t => t.id == report.processId ? 0 : 1).ThenBy(t => t.name))
        {
            Add(lines, thread.id == report.processId ? LineKind.UiThread : LineKind.Thread,
                $"{thread.name}  (tid {thread.id}, {thread.state}, priority {thread.priority})");

            AddFrames(lines, thread.stackTrace?.Select(FormatNativeFrame));
        }
    }

    static void AddFrames(List<Line> lines, IEnumerable<string> frames)
    {
        var count = 0;
        if (frames != null)
        {
            foreach (var frame in frames)
            {
                Add(lines, LineKind.Frame, frame);
                count++;
            }
        }

        // An empty native stack is information in itself - that thread never answered the
        // capture signal.
        if (count == 0)
            Add(lines, LineKind.Note, "    <no frames captured>");
    }

    static string FormatJavaFrame(AnrReport.JavaStackFrame frame)
    {
        var location = frame.lineNumber >= 0
            ? $"{frame.fileName}:{frame.lineNumber}"
            : string.IsNullOrEmpty(frame.fileName) ? "Native Method" : frame.fileName;

        return $"    at {frame.className}.{frame.methodName}({location})";
    }

    static string FormatNativeFrame(AnrReport.NativeStackFrame frame, int index)
    {
        var library = string.IsNullOrEmpty(frame.libraryName) ? "<unknown>" : Path.GetFileName(frame.libraryName);
        return $"    #{index:00}  0x{frame.address:x16}  {library}";
    }

    // Android names its UI thread "main" in the Java thread dump. The Unity player loop shows up
    // separately as "UnityMain".
    static bool IsAndroidUiThread(AnrReport.JavaThread thread) => thread.name == "main";

    static void Add(List<Line> lines, LineKind kind, string text) => lines.Add(new Line(text, kind));
}
