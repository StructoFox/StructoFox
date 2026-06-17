namespace StructoFox.App;

/// <summary>
/// Tiny localization facade. Mirrors ClaudetRelay's <c>Properties.Loc.S(key)</c> signature
/// so window code ported from the old app can keep calling <c>Loc.S("Key")</c> untouched.
/// </summary>
public static class Loc
{
    // Active UI language code ("en", "de", ...). The fox is bilingual for now; more tongues later.
    public static string Lang { get; set; } = "en";

    // English baseline — the safety net every key falls back to if a translation is missing.
    static readonly Dictionary<string, string> En = new()
    {
        ["App_Title"]    = "StructoFox",
        ["App_Tagline"]  = "Flow · Struct · Code",
        ["Smoke_Header"] = "Core smoke test — C# generated from a sample class:",

        // Diagram chooser (DiagramLauncher)
        ["Diag_Title"]     = "Diagram",
        ["Diag_SketchOf"]  = "Sketch the flow of:\n{0}",
        ["Diag_Pap"]       = "🔁 Programmablaufplan",
        ["Diag_PapExists"] = "🔁 Programmablaufplan (exists)",
        ["Diag_Ns"]        = "▦ Structogram",
        ["Diag_NsExists"]  = "▦ Structogram (exists)",
        ["Diag_NsTip"]     = "Nassi-Shneiderman structogram editor (DIN 66261)",
    };

    // German overlay; any key not listed here quietly falls through to the English baseline.
    static readonly Dictionary<string, string> De = new()
    {
        ["Smoke_Header"]   = "Core-Rauchtest — aus einer Beispielklasse generiertes C#:",
        ["Diag_Title"]     = "Diagramm",
        ["Diag_SketchOf"]  = "Ablauf skizzieren von:\n{0}",
        ["Diag_PapExists"] = "🔁 Programmablaufplan (vorhanden)",
        ["Diag_Ns"]        = "▦ Struktogramm",
        ["Diag_NsExists"]  = "▦ Struktogramm (vorhanden)",
        ["Diag_NsTip"]     = "Nassi-Shneiderman-Struktogramm-Editor (DIN 66261)",
    };

    // Language code -> its string table. Lookups consult the active table, then English.
    static readonly Dictionary<string, Dictionary<string, string>> Tables = new()
    {
        ["en"] = En,
        ["de"] = De,
    };

    /// <summary>
    /// Resolves a UI string by key for the active language.
    /// Falls back to English, then to the raw key itself — so a missing string shouts, it doesn't vanish.
    /// </summary>
    public static string S(string key)
    {
        if (Tables.TryGetValue(Lang, out var t) && t.TryGetValue(key, out var v)) return v;
        if (En.TryGetValue(key, out var en)) return en;
        return key;
    }
}
