using System.Text.Json;

namespace StructoFox.App;

/// <summary>
/// Remembers which "don't show again" info messages the user has dismissed for good, by a stable key.
/// Stored in AppData so it persists across runs. The fox only nags once.
/// </summary>
public static class SuppressStore
{
    static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StructoFox", "suppressed.json");

    static HashSet<string>? _set;

    static HashSet<string> Load()
    {
        if (_set is not null) return _set;
        try { _set = File.Exists(Path) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(Path)) ?? new() : new(); }
        catch { _set = new(); }
        return _set;
    }

    public static bool IsSuppressed(string key) => Load().Contains(key);

    public static void Suppress(string key)   { if (Load().Add(key)) Persist(); }

    /// <summary>Re-enables a previously suppressed message so it shows again.</summary>
    public static void Unsuppress(string key) { if (Load().Remove(key)) Persist(); }

    static void Persist()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(_set));
        }
        catch { /* best-effort */ }
    }

    /// <summary>The messages that can be toggled in Options (key + a short label loc-key).</summary>
    public static readonly (string Key, string LabelKey)[] Known =
    {
        ("sub_remove",   "Opt_SubRemove"),
        ("board_remove", "Opt_BoardRemove"),
    };
}
