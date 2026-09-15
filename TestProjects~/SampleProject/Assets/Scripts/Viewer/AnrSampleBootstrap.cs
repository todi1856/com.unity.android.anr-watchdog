using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Builds the sample UI at runtime, so the project works from any scene and needs no wiring in
/// the inspector. Everything is loaded from Resources and the panel is created in code.
/// </summary>
static class AnrSampleBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var visualTree = Resources.Load<VisualTreeAsset>("AnrSample");
        if (visualTree == null)
        {
            Debug.LogError("AnrSample.uxml was not found in Resources, the sample UI cannot be created.");
            return;
        }

        var host = new GameObject("ANR Watchdog Sample");
        UnityEngine.Object.DontDestroyOnLoad(host);

        var document = host.AddComponent<UIDocument>();
        document.panelSettings = CreatePanelSettings();
        document.visualTreeAsset = visualTree;

        // Named differently from the UXML on purpose - Resources.Load resolves by path first, so
        // two assets sharing a name would be ambiguous.
        var styleSheet = Resources.Load<StyleSheet>("AnrSampleStyles");
        if (styleSheet != null)
            document.rootVisualElement.styleSheets.Add(styleSheet);

        StretchToPanel(document.rootVisualElement);

        // No EventSystem is set up here on purpose - UI Toolkit falls back to its own internal
        // event system, so the sample does not need com.unity.ugui.
        host.AddComponent<AnrSampleWindow>();
    }

    static PanelSettings CreatePanelSettings()
    {
        var panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
        panelSettings.name = "ANR Sample Panel Settings";
        panelSettings.scaleMode = PanelScaleMode.ScaleWithScreenSize;

        // Everything in the UI is sized against this, so it is the one knob for how large the
        // whole thing renders on device: a smaller reference resolution scales text, buttons and
        // padding up together. 810x1440 is three quarters of a 1080p phone, so roughly a third
        // larger than sizing against the real thing.
        panelSettings.referenceResolution = new Vector2Int(810, 1440);
        panelSettings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
        panelSettings.match = 0.5f;

        // PanelSettings warns and renders controls unstyled without a theme. The project ships
        // Resources/UnityDefaultRuntimeTheme.tss for exactly this.
        var theme = Resources.Load<ThemeStyleSheet>("UnityDefaultRuntimeTheme");
        if (theme != null)
            panelSettings.themeStyleSheet = theme;
        else
            Debug.LogWarning("Resources/UnityDefaultRuntimeTheme.tss is missing, the sample UI will render unstyled.");

        return panelSettings;
    }

    /// <summary>
    /// UIDocument clones the UXML into a TemplateContainer, and neither it nor the document root
    /// grows by default - without this the UI is only as tall as its content and the event log
    /// stops short of the bottom of the screen.
    /// </summary>
    static void StretchToPanel(VisualElement root)
    {
        if (root == null)
            return;

        root.style.flexGrow = 1;
        root.Query<TemplateContainer>().ForEach(container => container.style.flexGrow = 1);
    }

    // The font comes from the theme style sheet the panel is given; nothing is set up here.

}
