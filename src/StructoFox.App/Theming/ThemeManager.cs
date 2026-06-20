using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using OXSUIT.Loaders.Avalonia;

namespace StructoFox.App;

/// <summary>
/// Owns the one active OXSUIT theme at application scope. Swapping it re-tints every window
/// live, since all controls bind their brushes via DynamicResource. The fox's wardrobe, basically.
/// </summary>
public static class ThemeManager
{
    // Where the shipped .oxsuit themes live (copied next to the app by the build).
    public static string ThemesDir { get; } = Path.Combine(AppContext.BaseDirectory, "Themes");

    // The currently-merged theme dictionary, so we can pull it back out before adding the next.
    static ResourceDictionary? _current;

    /// <summary>Lists the available themes (display name + path), alphabetically. Empty if none are shipped.</summary>
    public static IEnumerable<(string Name, string Path)> Available()
    {
        if (!Directory.Exists(ThemesDir)) yield break;
        foreach (var p in Directory.EnumerateFiles(ThemesDir, "*.oxsuit").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            yield return (Path.GetFileNameWithoutExtension(p), p);
    }

    /// <summary>Loads the .oxsuit at <paramref name="path"/> and swaps it in as THE app theme,
    /// replacing whatever was worn before. A bad file is ignored rather than fatal.</summary>
    public static void Apply(Application app, string path)
    {
        ResourceDictionary dict;
        try { dict = OxsuitLoader.Load(path); } catch { return; }

        if (_current is not null) app.Resources.MergedDictionaries.Remove(_current);
        app.Resources.MergedDictionaries.Add(dict);
        _current = dict;
        FixFluentBrushes(app);
    }

    /// <summary>Writes the Fluent-brush fixes into a specific window's own resources. Secondary windows
    /// (flowchart / structogram / board) merge their theme at window scope; their popups (context menus)
    /// don't reliably resolve the app-level fixes, so apply the same overrides locally.</summary>
    public static void FixFluentBrushes(Window w) => FixFluentBrushes(Application.Current!, w.Resources);

    static void FixFluentBrushes(Application app) => FixFluentBrushes(app, app.Resources);

    // Fluent paints some control glyphs (the ComboBox chevron, the CheckBox box + label) from its own
    // light brushes — direct template values that outside styles can't override — so they vanished on
    // light themes. Point those resource keys at the active theme's brushes instead, on every swap.
    static void FixFluentBrushes(Application app, IResourceDictionary target)
    {
        object? B(string key) => app.TryGetResource(key, null, out var v) ? v : null;
        var text     = B("SidebarTextBrush");   // glyphs + label text
        var accent   = B("AccentBgBrush");       // checked box fill
        var onAccent = B("ContentBgBrush");      // the check mark sitting on the accent fill

        void Set(object? val, params string[] keys)
        {
            if (val is null) return;
            foreach (var k in keys) target[k] = val;
        }

        // ComboBox drop-down chevron
        Set(text,
            "ComboBoxDropDownGlyphForeground", "ComboBoxDropDownGlyphForegroundDisabled",
            "ComboBoxDropDownGlyphForegroundFocused", "ComboBoxDropDownGlyphForegroundFocusedPressed");

        // ComboBox closed-box text + the drop-down popup (was white-on-white on light themes)
        Set(text, "ComboBoxForeground", "ComboBoxForegroundFocused", "ComboBoxForegroundFocusedPressed");
        var surface = B("ControlBgBrush");
        Set(surface, "ComboBoxDropDownBackground");
        Set(text, "ComboBoxItemForeground");
        // Hovered / selected items: accent fill with the background colour as readable text on top.
        Set(accent,
            "ComboBoxItemBackgroundPointerOver", "ComboBoxItemBackgroundPressed",
            "ComboBoxItemBackgroundSelected", "ComboBoxItemBackgroundSelectedPointerOver", "ComboBoxItemBackgroundSelectedPressed");
        Set(onAccent,
            "ComboBoxItemForegroundPointerOver", "ComboBoxItemForegroundPressed",
            "ComboBoxItemForegroundSelected", "ComboBoxItemForegroundSelectedPointerOver", "ComboBoxItemForegroundSelectedPressed");

        // TextBox watermark/placeholder — a faint version of the theme text colour (was white).
        if (text is ISolidColorBrush scb)
        {
            var faint = new SolidColorBrush(scb.Color, 0.55);
            Set(faint, "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver",
                       "TextControlPlaceholderForegroundFocused", "TextControlPlaceholderForegroundDisabled");
        }

        // Expander (the colour-field "cards" in the style editor were Fluent's dark default chrome)
        var border = B("ControlBorderBrush");
        Set(surface,
            "ExpanderHeaderBackground", "ExpanderHeaderBackgroundPointerOver", "ExpanderHeaderBackgroundPressed",
            "ExpanderContentBackground");
        Set(text,
            "ExpanderHeaderForeground", "ExpanderHeaderForegroundPointerOver", "ExpanderHeaderForegroundPressed",
            "ExpanderChevronForeground", "ExpanderChevronForegroundPointerOver", "ExpanderChevronForegroundPressed");
        Set(border, "ExpanderHeaderBorderBrush", "ExpanderContentBorderBrush", "ExpanderChevronBorderBrush");

        // Menu / context-menu items: Fluent flips the label to white on hover/press, unreadable on the
        // light hover highlight. Pin every state to the theme text colour and theme the popup chrome.
        Set(text, "MenuFlyoutItemForeground", "MenuFlyoutItemForegroundPointerOver", "MenuFlyoutItemForegroundPressed");
        Set(surface, "MenuFlyoutPresenterBackground");
        Set(border,  "MenuFlyoutPresenterBorderBrush");

        // CheckBox label text (the "static" caption went white, also on hover)
        Set(text,
            "CheckBoxForegroundUnchecked", "CheckBoxForegroundUncheckedPointerOver", "CheckBoxForegroundUncheckedPressed",
            "CheckBoxForegroundChecked", "CheckBoxForegroundCheckedPointerOver", "CheckBoxForegroundCheckedPressed");

        // CheckBox box outline when unchecked (it was white-on-white)
        Set(text,
            "CheckBoxCheckBackgroundStrokeUnchecked", "CheckBoxCheckBackgroundStrokeUncheckedPointerOver",
            "CheckBoxCheckBackgroundStrokeUncheckedPressed");

        // Checked box: accent fill with a contrasting check mark
        Set(accent,
            "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver",
            "CheckBoxCheckBackgroundFillCheckedPressed");
        Set(onAccent,
            "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver",
            "CheckBoxCheckGlyphForegroundCheckedPressed");
    }

    // The theme worn out of the box: a clean light "drawing board" surface for diagrams & code.
    public const string DefaultThemeName = "PaperWhite";

    /// <summary>Dresses the app in the default theme (PaperWhite) if present, else the first theme found.</summary>
    public static void ApplyDefault(Application app)
    {
        var all = Available().ToList();
        if (all.Count == 0) return;

        var pick = all.FirstOrDefault(t => t.Name.Equals(DefaultThemeName, StringComparison.OrdinalIgnoreCase));
        Apply(app, pick.Path ?? all[0].Path);
    }
}
