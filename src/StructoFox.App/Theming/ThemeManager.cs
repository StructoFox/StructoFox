using Avalonia;
using Avalonia.Controls;
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

    // Fluent paints some control glyphs (the ComboBox chevron, the CheckBox box + label) from its own
    // light brushes — direct template values that outside styles can't override — so they vanished on
    // light themes. Point those resource keys at the active theme's brushes instead, on every swap.
    static void FixFluentBrushes(Application app)
    {
        object? B(string key) => app.TryGetResource(key, null, out var v) ? v : null;
        var text     = B("SidebarTextBrush");   // glyphs + label text
        var accent   = B("AccentBgBrush");       // checked box fill
        var onAccent = B("ContentBgBrush");      // the check mark sitting on the accent fill

        void Set(object? val, params string[] keys)
        {
            if (val is null) return;
            foreach (var k in keys) app.Resources[k] = val;
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
