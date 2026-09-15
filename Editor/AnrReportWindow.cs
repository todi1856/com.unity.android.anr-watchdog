using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.UIElements;
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

        TextField m_ReportPath;
        TextField m_SymbolsPath;
        Label m_Status;
        ToolbarToggle m_JavaTab;
        ToolbarToggle m_NativeTab;
        ToolbarSearchField m_JavaThreadFilter;
        ToolbarSearchField m_NativeThreadFilter;

        AnrTable m_Metadata;
        AnrTable m_Threads;
        AnrTable m_JavaStack;
        AnrTable m_NativeStack;

        // Serialized so they survive a domain reload - the window is rebuilt from scratch on every
        // script recompile and on entering play mode.
        [SerializeField] string m_JavaThreadFilterValue;
        [SerializeField] string m_NativeThreadFilterValue;
        [SerializeField] bool m_NativeTabSelected;

        AnrReport m_Report;
        readonly List<KeyValuePair<string, string>> m_MetadataRows = new List<KeyValuePair<string, string>>();
        readonly AnrSymbolicator m_Symbolicator = new AnrSymbolicator();

        // What the thread list is showing: the report's threads passed through the name filter.
        // Selection and cell binding both index these, not the report's arrays.
        readonly List<AnrReport.JavaThread> m_JavaThreads = new List<AnrReport.JavaThread>();
        readonly List<AnrReport.NativeThread> m_NativeThreads = new List<AnrReport.NativeThread>();

        AnrReport.JavaStackFrame[] m_JavaFrames = Array.Empty<AnrReport.JavaStackFrame>();
        AnrReport.NativeStackFrame[] m_NativeFrames = Array.Empty<AnrReport.NativeStackFrame>();

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

            var styleSheet = LoadBesideScript<StyleSheet>(".uss");
            if (styleSheet != null)
                rootVisualElement.styleSheets.Add(styleSheet);

            m_ReportPath = rootVisualElement.Q<TextField>("fieldReportPath");
            m_SymbolsPath = rootVisualElement.Q<TextField>("fieldSymbolsPath");
            m_Status = rootVisualElement.Q<Label>("labelStatus");
            m_JavaTab = rootVisualElement.Q<ToolbarToggle>("toggleJavaThreads");
            m_NativeTab = rootVisualElement.Q<ToolbarToggle>("toggleNativeThreads");
            m_JavaThreadFilter = rootVisualElement.Q<ToolbarSearchField>("fieldJavaThreadFilter");
            m_NativeThreadFilter = rootVisualElement.Q<ToolbarSearchField>("fieldNativeThreadFilter");

            m_ReportPath.value = EditorPrefs.GetString(k_ReportPathKey, string.Empty);
            m_SymbolsPath.value = EditorPrefs.GetString(k_SymbolsPathKey, string.Empty);

            rootVisualElement.Q<Button>("btnBrowseReport").clicked += BrowseReport;
            rootVisualElement.Q<Button>("btnBrowseSymbols").clicked += BrowseSymbols;
            rootVisualElement.Q<Button>("btnReload").clicked += () => LoadReport(m_ReportPath.value);
            rootVisualElement.Q<Button>("btnResolve").clicked += ResolveSymbols;

            BuildTables();

            m_JavaThreadFilter.tooltip = "Filter Java threads by name";
            m_NativeThreadFilter.tooltip = "Filter native threads by name";
            m_JavaThreadFilter.SetValueWithoutNotify(m_JavaThreadFilterValue ?? string.Empty);
            m_NativeThreadFilter.SetValueWithoutNotify(m_NativeThreadFilterValue ?? string.Empty);
            m_JavaThreadFilter.RegisterValueChangedCallback(changed =>
            {
                m_JavaThreadFilterValue = changed.newValue;
                RefreshThreads();
            });
            m_NativeThreadFilter.RegisterValueChangedCallback(changed =>
            {
                m_NativeThreadFilterValue = changed.newValue;
                RefreshThreads();
            });

            // Clicking the toggle that is already on turns it off, so both are set explicitly
            // rather than left to the toggles themselves.
            m_JavaTab.RegisterValueChangedCallback(_ => SelectTab(false));
            m_NativeTab.RegisterValueChangedCallback(_ => SelectTab(true));
            SelectTab(m_NativeTabSelected);

            LoadReport(m_ReportPath.value);

            if (m_Report == null)
                SetStatus("Browse to an ANR report - anr-*.json, pulled off the device - to begin.");
        }

        void BuildTables()
        {
            m_Metadata = new AnrTable(
                new AnrTable.Column("Field", 220),
                new AnrTable.Column("Value"));
            m_Metadata.bindRow = (index, cells) =>
            {
                cells[0].text = m_MetadataRows[index].Key;
                cells[1].text = m_MetadataRows[index].Value;
            };
            m_Metadata.itemsSource = m_MetadataRows;
            rootVisualElement.Q<VisualElement>("metadataTable").Add(m_Metadata.root);

            m_Threads = new AnrTable(
                new AnrTable.Column("Thread"),
                new AnrTable.Column("Id", 64),
                new AnrTable.Column("State", 104),
                new AnrTable.Column("Prio", 48),
                new AnrTable.Column("Frames", 58));
            m_Threads.bindRow = BindThreadRow;
            m_Threads.selectionChanged += RefreshStack;
            rootVisualElement.Q<VisualElement>("threadsTable").Add(m_Threads.root);

            m_JavaStack = new AnrTable(
                new AnrTable.Column("#", 38),
                new AnrTable.Column("Class"),
                new AnrTable.Column("Method"),
                new AnrTable.Column("Source", 220));
            m_JavaStack.bindRow = BindJavaFrameRow;
            m_JavaStack.itemsSource = m_JavaFrames;
            rootVisualElement.Q<VisualElement>("javaStackTable").Add(m_JavaStack.root);

            m_NativeStack = new AnrTable(
                new AnrTable.Column("#", 38),
                new AnrTable.Column("Address", 140),
                new AnrTable.Column("Library", 170),
                new AnrTable.Column("Function"),
                new AnrTable.Column("Source"));
            m_NativeStack.bindRow = BindNativeFrameRow;
            m_NativeStack.itemsSource = m_NativeFrames;
            rootVisualElement.Q<VisualElement>("nativeStackTable").Add(m_NativeStack.root);
        }

        void BindThreadRow(int index, Label[] cells)
        {
            if (m_NativeTabSelected)
            {
                var thread = m_NativeThreads[index];
                cells[0].text = thread.name;
                cells[1].text = thread.id.ToString();
                cells[2].text = thread.state;
                cells[3].text = thread.priority.ToString();
                cells[4].text = (thread.stackTrace?.Length ?? 0).ToString();
            }
            else
            {
                var thread = m_JavaThreads[index];
                cells[0].text = thread.name;
                cells[1].text = thread.id.ToString();
                cells[2].text = thread.state;
                cells[3].text = thread.priority.ToString();
                cells[4].text = (thread.stackTrace?.Length ?? 0).ToString();
            }
        }

        void BindJavaFrameRow(int index, Label[] cells)
        {
            var frame = m_JavaFrames[index];
            cells[0].text = $"{index:00}";
            cells[1].text = frame.className;
            cells[2].text = frame.methodName;
            cells[3].text = frame.lineNumber >= 0 ? $"{frame.fileName}:{frame.lineNumber}" : frame.fileName;
        }

        void BindNativeFrameRow(int index, Label[] cells)
        {
            var frame = m_NativeFrames[index];
            cells[0].text = $"{index:00}";
            cells[1].text = $"0x{frame.address:x16}";
            cells[2].text = Path.GetFileName(frame.libraryName ?? string.Empty);

            if (!m_Symbolicator.TryGetSymbol(frame.libraryName, frame.address, out var symbol))
            {
                cells[3].text = "<unresolved>";
                cells[4].text = string.Empty;
                return;
            }

            cells[3].text = symbol.inlinedFrames > 0
                ? $"{symbol.function}  (+{symbol.inlinedFrames} inlined)"
                : symbol.function;

            // Says why there is no file:line rather than leaving the cell blank.
            cells[4].text = symbol.fromSymbolTable ? "<symbol table only, no line info>" : symbol.source;
        }

        void SelectTab(bool native)
        {
            m_NativeTabSelected = native;
            m_JavaTab.SetValueWithoutNotify(!native);
            m_NativeTab.SetValueWithoutNotify(native);

            RefreshThreads();
        }

        VisualTreeAsset LoadVisualTree() =>
            LoadBesideScript<VisualTreeAsset>(".uxml") ?? AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(k_FallbackUxml);

        /// <summary>
        /// Loads an asset sitting next to this script and sharing its name, so the window works
        /// from an embedded package, a registry package or a project folder alike.
        /// </summary>
        T LoadBesideScript<T>(string extension) where T : UnityEngine.Object
        {
            var script = MonoScript.FromScriptableObject(this);
            var scriptPath = script != null ? AssetDatabase.GetAssetPath(script) : null;
            if (string.IsNullOrEmpty(scriptPath))
                return null;

            var beside = $"{Path.GetDirectoryName(scriptPath)?.Replace('\\', '/')}/{nameof(AnrReportWindow)}{extension}";
            return AssetDatabase.LoadAssetAtPath<T>(beside);
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

            m_Metadata.Rebuild();
        }

        void RefreshThreads()
        {
            // Only the filter belonging to the visible tab is shown, but both lists are kept
            // filtered so switching tabs needs no extra work.
            m_JavaThreadFilter.style.display = m_NativeTabSelected ? DisplayStyle.None : DisplayStyle.Flex;
            m_NativeThreadFilter.style.display = m_NativeTabSelected ? DisplayStyle.Flex : DisplayStyle.None;

            if (m_Report == null)
                return;

            var javaFilter = m_JavaThreadFilter?.value ?? string.Empty;
            var nativeFilter = m_NativeThreadFilter?.value ?? string.Empty;

            m_JavaThreads.Clear();
            m_JavaThreads.AddRange((m_Report.javaThreads ?? Array.Empty<AnrReport.JavaThread>())
                .Where(thread => Matches(thread.name, javaFilter)));

            m_NativeThreads.Clear();
            m_NativeThreads.AddRange((m_Report.nativeThreads ?? Array.Empty<AnrReport.NativeThread>())
                .Where(thread => Matches(thread.name, nativeFilter)));

            m_Threads.itemsSource = m_NativeTabSelected
                ? (System.Collections.IList)m_NativeThreads
                : m_JavaThreads;

            m_Threads.Rebuild();
            RefreshStack();
        }

        static bool Matches(string threadName, string filter) =>
            string.IsNullOrEmpty(filter) ||
            (threadName != null && threadName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);

        void RefreshStack()
        {
            m_JavaStack.root.style.display = m_NativeTabSelected ? DisplayStyle.None : DisplayStyle.Flex;
            m_NativeStack.root.style.display = m_NativeTabSelected ? DisplayStyle.Flex : DisplayStyle.None;

            var index = m_Threads.selectedIndex;
            var threadCount = m_NativeTabSelected ? m_NativeThreads.Count : m_JavaThreads.Count;
            if (m_Report == null || index < 0 || index >= threadCount)
                return;

            if (m_NativeTabSelected)
            {
                m_NativeFrames = m_NativeThreads[index].stackTrace ?? Array.Empty<AnrReport.NativeStackFrame>();
                m_NativeStack.itemsSource = m_NativeFrames;
                m_NativeStack.Rebuild();
            }
            else
            {
                m_JavaFrames = m_JavaThreads[index].stackTrace ?? Array.Empty<AnrReport.JavaStackFrame>();
                m_JavaStack.itemsSource = m_JavaFrames;
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
    }
}
