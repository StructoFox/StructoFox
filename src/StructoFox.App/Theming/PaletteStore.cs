using StructoFox.Core;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// Where the user's colour palettes live on disk and how the app gets at them. Uses a writable
/// per-user folder (so it works even when the app is installed read-only) and seeds the built-in
/// Standard palette on first run. The shareable side of the palette system.
/// </summary>
public static class PaletteStore
{
    // Per-user, writable, and easy to find/back up — palette files are portable between machines.
    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StructoFox", "Palettes");

    // Drops the built-in Standard palette into an empty store so there's always something to start from.
    public static void EnsureSeeded()
    {
        if (PaletteService.LoadAll(Dir).Count == 0)
            PaletteService.Save(Dir, PaletteService.BuiltIn());
    }

    // Returns every saved palette (seeding first), so callers always get at least the Standard one.
    public static List<ColorPalette> LoadAll()
    {
        EnsureSeeded();
        return PaletteService.LoadAll(Dir);
    }

    // Persists a palette to the store, returning the written file path.
    public static string Save(ColorPalette palette) => PaletteService.Save(Dir, palette);

    static string ActiveFile => Path.Combine(Dir, "active.txt");

    /// <summary>The name of the palette colour pickers should use; null until the user chooses one.</summary>
    public static string? ActiveName
    {
        get { try { return File.Exists(ActiveFile) ? File.ReadAllText(ActiveFile).Trim() : null; } catch { return null; } }
        set { try { Directory.CreateDirectory(Dir); File.WriteAllText(ActiveFile, value ?? ""); } catch { } }
    }

    /// <summary>The active palette: the one named by <see cref="ActiveName"/>, else the first, else built-in.</summary>
    public static ColorPalette Active()
    {
        var all = LoadAll();
        return all.FirstOrDefault(p => p.Name == ActiveName) ?? all.FirstOrDefault() ?? PaletteService.BuiltIn();
    }
}
