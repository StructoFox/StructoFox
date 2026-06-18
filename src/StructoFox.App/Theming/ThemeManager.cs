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
        FixComboGlyph(app);
    }

    // Fluent paints the ComboBox drop-down chevron from its own light brush (a direct template value
    // that outside styles can't override), so it vanished on light themes. Point those resource keys
    // at the active theme's sidebar text colour instead — re-applied on every theme swap.
    static void FixComboGlyph(Application app)
    {
        if (!app.TryGetResource("SidebarTextBrush", null, out var fg) || fg is null) return;
        foreach (var key in new[]
                 {
                     "ComboBoxDropDownGlyphForeground",
                     "ComboBoxDropDownGlyphForegroundDisabled",
                     "ComboBoxDropDownGlyphForegroundFocused",
                     "ComboBoxDropDownGlyphForegroundFocusedPressed",
                 })
            app.Resources[key] = fg;
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
