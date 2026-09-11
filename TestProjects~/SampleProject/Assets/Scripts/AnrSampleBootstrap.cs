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
        Object.DontDestroyOnLoad(host);

        var document = host.AddComponent<UIDocument>();
        document.panelSettings = CreatePanelSettings();
        document.visualTreeAsset = visualTree;

        // Named differently from the UXML on purpose - Resources.Load resolves by path first, so
        // two assets sharing a name would be ambiguous.
        var styleSheet = Resources.Load<StyleSheet>("AnrSampleStyles");
        if (styleSheet != null)
            document.rootVisualElement.styleSheets.Add(styleSheet);

        ApplyFallbackFont(document.rootVisualElement);

        // No EventSystem is set up here on purpose - UI Toolkit falls back to its own internal
        // event system, so the sample does not need com.unity.ugui.
        host.AddComponent<AnrSampleWindow>();
    }

    static PanelSettings CreatePanelSettings()
    {
        var panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
        panelSettings.name = "ANR Sample Panel Settings";
        panelSettings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
        panelSettings.referenceResolution = new Vector2Int(1080, 1920);
        panelSettings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
        panelSettings.match = 0.5f;

        // Only present if the project happens to contain one. The sample's style sheet sets every
        // value it needs, so running without a theme is fine.
        var theme = Resources.Load<ThemeStyleSheet>("UnityDefaultRuntimeTheme");
        if (theme != null)
            panelSettings.themeStyleSheet = theme;

        return panelSettings;
    }

    /// <summary>
    /// Without a theme style sheet there is no default font, and every label would render empty.
    /// </summary>
    static void ApplyFallbackFont(VisualElement root)
    {
        if (root == null)
            return;

        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null)
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        if (font != null)
            root.style.unityFontDefinition = FontDefinition.FromFont(font);
    }

}
