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

    // Strings live in a separate file, so the boolean settings.json schema stays untouched.
    static readonly string StrPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StructoFox", "prefs.json");

    static Dictionary<string, bool>? _flags;
    static Dictionary<string, string>? _strings;

    static Dictionary<string, bool> Load()
    {
        if (_flags is not null) return _flags;
        try { _flags = File.Exists(Path) ? JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(Path)) ?? new() : new(); }
        catch { _flags = new(); }
        return _flags;
    }

    static Dictionary<string, string> LoadStr()
    {
        if (_strings is not null) return _strings;
        try { _strings = File.Exists(StrPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StrPath)) ?? new() : new(); }
        catch { _strings = new(); }
        return _strings;
    }

    public static string GetStr(string key, string dflt) => LoadStr().TryGetValue(key, out var v) ? v : dflt;

    public static void SetStr(string key, string value)
    {
        LoadStr()[key] = value;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(StrPath)!);
            File.WriteAllText(StrPath, JsonSerializer.Serialize(_strings));
        }
        catch { /* best-effort */ }
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

    // ── UI language ───────────────────────────────────────────────────────────
    // Persisted language code ("en"/"de"), or "" when the user has never chosen one
    // (in which case Loc falls back to the OS culture).
    public const string LangKey = "lang";
    public static string Lang { get => GetStr(LangKey, ""); set => SetStr(LangKey, value); }

    // The user's name — suggested as the header's "author" field on new diagrams.
    public const string UserNameKey = "user_name";
    public static string UserName { get => GetStr(UserNameKey, ""); set => SetStr(UserNameKey, value); }
}
