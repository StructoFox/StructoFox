using System.Text.Json;

namespace StructoFox.App;

/// <summary>
/// Tiny persisted set of boolean preferences (AppData/StructoFox/settings.json). Used for global
/// toggles like the DIN-norm warnings/markings. Defaults apply until the user flips them.
/// </summary>
public static class AppSettings
{
    static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StructoFox", "settings.json");

    static Dictionary<string, bool>? _flags;

    static Dictionary<string, bool> Load()
    {
        if (_flags is not null) return _flags;
        try { _flags = File.Exists(Path) ? JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(Path)) ?? new() : new(); }
        catch { _flags = new(); }
        return _flags;
    }

    public static bool Get(string key, bool dflt) => Load().TryGetValue(key, out var v) ? v : dflt;

    public static void Set(string key, bool value)
    {
        Load()[key] = value;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(_flags));
        }
        catch { /* best-effort */ }
    }

    // ── Norm-compliance preferences ──────────────────────────────────────────
    public const string NormWarnKey = "norm_warn";
    public const string NormMarkKey = "norm_mark";
    public static bool NormWarn => Get(NormWarnKey, true);
    public static bool NormMark => Get(NormMarkKey, true);
}
