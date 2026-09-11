using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.Android
{
    /// <summary>
    /// Opens an ANR report written by the watchdog and, given the unstripped libraries from the
    /// build's symbols.zip, resolves its native stacks to function names and source lines.
    /// </summary>
    public class AnrReportWindow : EditorWindow
    {
        const string k_ReportPathKey = "Unity.Android.AnrWatchdog.ReportPath";
        const string k_SymbolsPathKey = "Unity.Android.AnrWatchdog.SymbolsPath";
        const string k_FallbackUxml = "Packages/com.unity.android.anr-watchdog/Editor/AnrReportWindow.uxml";

        // Column names, as the UXML declares them.
        const string k_Name = "Name";
        const string k_Value = "Value";
        const string k_Id = "Id";
        const string k_State = "State";
        const string k_Priority = "Priority";
        const string k_Frames = "Frames";
        const string k_Frame = "Frame";
        const string k_Class = "Class";
        const string k_Method = "Method";
        const string k_Source = "Source";
        const string k_Address = "Address";
        const string k_Library = "Library";
        const string k_Function = "Function";

        TextField m_ReportPath;
        TextField m_SymbolsPath;
        Label m_Status;
        MultiColumnListView m_Metadata;
        MultiColumnListView m_Threads;
        MultiColumnListView m_JavaStack;
        MultiColumnListView m_NativeStack;
        TabView m_Tabs;

        AnrReport m_Report;
        readonly List<KeyValuePair<string, string>> m_MetadataRows = new List<KeyValuePair<string, string>>();
        readonly AnrSymbolicator m_Symbolicator = new AnrSymbolicator();

        bool NativeTabSelected => m_Tabs.selectedTabIndex == 1;

        [MenuItem("Window/Analysis/Android ANR Report")]
        public static void ShowWindow()
        {
            var window = GetWindow<AnrReportWindow>();
            window.titleContent = new GUIContent("ANR Report");
            window.minSize = new Vector2(900, 500);
        }

        void CreateGUI()
        {
            var tree = LoadVisualTree();
            if (tree == null)
            {
                rootVisualElement.Add(new Label($"Could not load {Path.GetFileName(k_FallbackUxml)}."));
                return;
            }

            tree.CloneTree(rootVisualElement);

            m_ReportPath = rootVisualElement.Q<TextField>("fieldReportPath");
            m_SymbolsPath = rootVisualElement.Q<TextField>("fieldSymbolsPath");
            m_Status = rootVisualElement.Q<Label>("labelStatus");
            m_Metadata = rootVisualElement.Q<MultiColumnListView>("listMetadata");
            m_Threads = rootVisualElement.Q<MultiColumnListView>("listThreads");
            m_JavaStack = rootVisualElement.Q<MultiColumnListView>("listJavaStack");
            m_NativeStack = rootVisualElement.Q<MultiColumnListView>("listNativeStack");
            m_Tabs = rootVisualElement.Q<TabView>("tabThreads");

            m_ReportPath.value = EditorPrefs.GetString(k_ReportPathKey, string.Empty);
            m_SymbolsPath.value = EditorPrefs.GetString(k_SymbolsPathKey, string.Empty);

            rootVisualElement.Q<Button>("btnBrowseReport").clicked += BrowseReport;
            rootVisualElement.Q<Button>("btnBrowseSymbols").clicked += BrowseSymbols;
            rootVisualElement.Q<Button>("btnReload").clicked += () => LoadReport(m_ReportPath.value);
            rootVisualElement.Q<Button>("btnResolve").clicked += ResolveSymbols;

            SetUpMetadataColumns();
            SetUpThreadColumns();
            SetUpJavaStackColumns();
            SetUpNativeStackColumns();

            m_Tabs.activeTabChanged += (_, __) => RefreshThreads();
            m_Threads.selectionChanged += _ => RefreshStack();

            LoadReport(m_ReportPath.value);
        }

        VisualTreeAsset LoadVisualTree()
        {
            // Resolve the UXML next to this script, so the window works from an embedded package,
            // a registry package or a project folder alike.
            var script = MonoScript.FromScriptableObject(this);
            var scriptPath = script != null ? AssetDatabase.GetAssetPath(script) : null;
            if (!string.IsNullOrEmpty(scriptPath))
            {
                var beside = $"{Path.GetDirectoryName(scriptPath)?.Replace('\\', '/')}/{nameof(AnrReportWindow)}.uxml";
                var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(beside);
                if (tree != null)
                    return tree;
            }

            return AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(k_FallbackUxml);
        }

        void BrowseReport()
        {
            var directory = SafeDirectory(m_ReportPath.value);
            var path = EditorUtility.OpenFilePanel("Open ANR report", directory, "json");
            if (!string.IsNullOrEmpty(path))
                LoadReport(path);
        }

        void BrowseSymbols()
        {
            var directory = SafeDirectory(m_SymbolsPath.value);
            var path = EditorUtility.OpenFolderPanel("Select unstripped libraries (symbols.zip contents)", directory, string.Empty);
            if (string.IsNullOrEmpty(path))
                return;

            m_SymbolsPath.value = path;
            EditorPrefs.SetString(k_SymbolsPathKey, path);
        }

        static string SafeDirectory(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;

            return Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? string.Empty;
        }

        void LoadReport(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;

            try
            {
                m_Report = JsonUtility.FromJson<AnrReport>(File.ReadAllText(path));
            }
            catch (Exception exception)
            {
                m_Report = null;
                SetStatus($"Failed to read '{path}': {exception.Message}");
                return;
            }

            if (m_Report == null)
            {
                SetStatus($"'{Path.GetFileName(path)}' is not an ANR report.");
                return;
            }

            m_ReportPath.value = path;
            EditorPrefs.SetString(k_ReportPathKey, path);
            m_Symbolicator.Clear();

            RefreshMetadata();
            RefreshThreads();

            SetStatus($"{Path.GetFileName(path)} - {CountFrames()} native frames. " +
                      "Pick the symbols folder and press Resolve symbols to see function names.");
        }

        void RefreshMetadata()
        {
            m_MetadataRows.Clear();

            var skipped = new[]
            {
                nameof(AnrReport.javaThreads),
                nameof(AnrReport.nativeThreads),
                nameof(AnrReport.sourcePath)
            };

            foreach (var field in typeof(AnrReport).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (skipped.Contains(field.Name))
                    continue;

                m_MetadataRows.Add(new KeyValuePair<string, string>(field.Name, field.GetValue(m_Report)?.ToString() ?? "<null>"));
            }

            m_MetadataRows.Add(new KeyValuePair<string, string>("javaThreads", $"{m_Report.javaThreads?.Length ?? 0}"));
            m_MetadataRows.Add(new KeyValuePair<string, string>("nativeThreads", $"{m_Report.nativeThreads?.Length ?? 0}"));

            m_Metadata.itemsSource = m_MetadataRows;
            m_Metadata.Rebuild();
        }

        void RefreshThreads()
        {
            if (m_Report == null)
                return;

            m_Threads.itemsSource = NativeTabSelected
                ? (System.Collections.IList)(m_Report.nativeThreads ?? Array.Empty<AnrReport.NativeThread>())
                : (m_Report.javaThreads ?? Array.Empty<AnrReport.JavaThread>());

            m_Threads.Rebuild();
            RefreshStack();
        }

        void RefreshStack()
        {
            m_JavaStack.style.display = NativeTabSelected ? DisplayStyle.None : DisplayStyle.Flex;
            m_NativeStack.style.display = NativeTabSelected ? DisplayStyle.Flex : DisplayStyle.None;

            var index = m_Threads.selectedIndex;
            if (m_Report == null || index < 0 || m_Threads.itemsSource == null || index >= m_Threads.itemsSource.Count)
                return;

            if (NativeTabSelected)
            {
                m_NativeStack.itemsSource = m_Report.nativeThreads[index].stackTrace ?? Array.Empty<AnrReport.NativeStackFrame>();
                m_NativeStack.Rebuild();
            }
            else
            {
                m_JavaStack.itemsSource = m_Report.javaThreads[index].stackTrace ?? Array.Empty<AnrReport.JavaStackFrame>();
                m_JavaStack.Rebuild();
            }
        }

        void ResolveSymbols()
        {
            if (m_Report == null)
            {
                SetStatus("Open a report first.");
                return;
            }

            EditorPrefs.SetString(k_SymbolsPathKey, m_SymbolsPath.value);

            AnrSymbolicator.Summary summary;
            try
            {
                EditorUtility.DisplayProgressBar("ANR Report", "Resolving native symbols...", 0.5f);
                summary = m_Symbolicator.Resolve(m_Report, m_SymbolsPath.value);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            foreach (var message in summary.messages)
                Debug.LogWarning($"ANR symbolication: {message}");

            var status = summary.total > 0
                ? $"Resolved {summary.resolved} of {summary.total} distinct addresses."
                : "Nothing to resolve.";

            if (summary.messages.Count > 0)
                status += $" {summary.messages[0]}";
            if (summary.messages.Count > 1)
                status += $" (+{summary.messages.Count - 1} more in the console)";

            SetStatus(status);
            m_NativeStack.RefreshItems();
        }

        int CountFrames() =>
            (m_Report.nativeThreads ?? Array.Empty<AnrReport.NativeThread>())
            .Sum(thread => thread.stackTrace?.Length ?? 0);

        void SetStatus(string message) => m_Status.text = message;

        void SetUpMetadataColumns()
        {
            m_Metadata.columns[k_Name].makeCell = MakeCell;
            m_Metadata.columns[k_Value].makeCell = MakeCell;
            m_Metadata.columns[k_Name].bindCell = (element, i) => Text(element, m_MetadataRows[i].Key);
            m_Metadata.columns[k_Value].bindCell = (element, i) => Text(element, m_MetadataRows[i].Value);
        }

        void SetUpThreadColumns()
        {
            foreach (var column in new[] { k_Name, k_Id, k_State, k_Priority, k_Frames })
                m_Threads.columns[column].makeCell = MakeCell;

            m_Threads.columns[k_Name].bindCell = (element, i) => Text(element,
                NativeTabSelected ? m_Report.nativeThreads[i].name : m_Report.javaThreads[i].name);
            m_Threads.columns[k_Id].bindCell = (element, i) => Text(element,
                NativeTabSelected ? m_Report.nativeThreads[i].id.ToString() : m_Report.javaThreads[i].id.ToString());
            m_Threads.columns[k_State].bindCell = (element, i) => Text(element,
                NativeTabSelected ? m_Report.nativeThreads[i].state : m_Report.javaThreads[i].state);
            m_Threads.columns[k_Priority].bindCell = (element, i) => Text(element,
                NativeTabSelected ? m_Report.nativeThreads[i].priority.ToString() : m_Report.javaThreads[i].priority.ToString());
            m_Threads.columns[k_Frames].bindCell = (element, i) => Text(element,
                NativeTabSelected
                    ? (m_Report.nativeThreads[i].stackTrace?.Length ?? 0).ToString()
                    : (m_Report.javaThreads[i].stackTrace?.Length ?? 0).ToString());
        }

        void SetUpJavaStackColumns()
        {
            foreach (var column in new[] { k_Frame, k_Class, k_Method, k_Source })
                m_JavaStack.columns[column].makeCell = MakeCell;

            m_JavaStack.columns[k_Frame].bindCell = (element, i) => Text(element, $"{i:00}");
            m_JavaStack.columns[k_Class].bindCell = (element, i) => Text(element, JavaFrame(i).className);
            m_JavaStack.columns[k_Method].bindCell = (element, i) => Text(element, JavaFrame(i).methodName);
            m_JavaStack.columns[k_Source].bindCell = (element, i) =>
            {
                var frame = JavaFrame(i);
                Text(element, frame.lineNumber >= 0 ? $"{frame.fileName}:{frame.lineNumber}" : frame.fileName);
            };
        }

        void SetUpNativeStackColumns()
        {
            foreach (var column in new[] { k_Frame, k_Address, k_Library, k_Function, k_Source })
                m_NativeStack.columns[column].makeCell = MakeCell;

            m_NativeStack.columns[k_Frame].bindCell = (element, i) => Text(element, $"{i:00}");
            m_NativeStack.columns[k_Address].bindCell = (element, i) => Text(element, $"0x{NativeFrame(i).address:x16}");
            m_NativeStack.columns[k_Library].bindCell = (element, i) => Text(element, Path.GetFileName(NativeFrame(i).libraryName ?? string.Empty));
            m_NativeStack.columns[k_Function].bindCell = (element, i) =>
            {
                var frame = NativeFrame(i);
                if (!m_Symbolicator.TryGetSymbol(frame.libraryName, frame.address, out var symbol))
                {
                    Text(element, "<unresolved>");
                    return;
                }

                Text(element, symbol.inlinedFrames > 0
                    ? $"{symbol.function}  (+{symbol.inlinedFrames} inlined)"
                    : symbol.function);
            };
            m_NativeStack.columns[k_Source].bindCell = (element, i) =>
            {
                var frame = NativeFrame(i);
                Text(element, m_Symbolicator.TryGetSymbol(frame.libraryName, frame.address, out var symbol) ? symbol.source : string.Empty);
            };
        }

        AnrReport.JavaStackFrame JavaFrame(int index) => (AnrReport.JavaStackFrame)m_JavaStack.itemsSource[index];

        AnrReport.NativeStackFrame NativeFrame(int index) => (AnrReport.NativeStackFrame)m_NativeStack.itemsSource[index];

        static VisualElement MakeCell()
        {
            var label = new Label { style = { marginLeft = 4, unityTextAlign = TextAnchor.MiddleLeft } };
            return label;
        }

        static void Text(VisualElement element, string text) => ((Label)element).text = text;
    }
}
