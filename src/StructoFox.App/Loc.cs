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

        // Common
        ["Common_Untitled"] = "Untitled",
        ["Flow_EditText"]   = "✎ Edit text…",

        // Structogram editor
        ["Struct_Title"]          = "▦  Structogram — {0}",
        ["Struct_Hint"]           = "Right-click any block to edit / insert / wrap. Use ＋ to add.",
        ["Struct_PhStatement"]    = "(statement)",
        ["Struct_PhCondition"]    = "(condition)",
        ["Struct_True"]           = "true",
        ["Struct_False"]          = "false",
        ["Struct_PhWhile"]        = "while (…)",
        ["Struct_PhDoWhile"]      = "do … while (…)",
        ["Struct_PhSelector"]     = "(selector)",
        ["Struct_Case"]           = "case",
        ["Struct_InsertAbove"]    = "Insert above",
        ["Struct_InsertBelow"]    = "Insert below",
        ["Struct_AddLoopBody"]    = "Add to loop body",
        ["Struct_AddTrue"]        = "Add to TRUE branch",
        ["Struct_AddFalse"]       = "Add to FALSE branch",
        ["Struct_AddArm"]         = "Add case arm",
        ["Struct_DeleteBlock"]    = "✕ Delete block",
        ["Struct_KStatement"]     = "Statement",
        ["Struct_KIf"]            = "If / Else",
        ["Struct_KWhile"]         = "While loop (pre-test)",
        ["Struct_KDoWhile"]       = "Do-While loop (post-test)",
        ["Struct_KCase"]          = "Case (multi-way)",
        ["Struct_AddBlockTip"]    = "Add a block",
        ["Struct_AddInline"]      = "＋ add",
        ["Struct_PromptStatement"] = "Statement",
        ["Struct_PromptCondition"] = "Condition / expression",
        ["Struct_PromptCaseLabel"] = "Case label",
        ["Struct_DefCondition"]   = "condition",
        ["Struct_DefWhile"]       = "while (condition)",
        ["Struct_DefDoWhile"]     = "do … while (condition)",
        ["Struct_DefSelector"]    = "selector",
        ["Struct_DefStatement"]   = "statement",
        ["Struct_FlaggedTip"]     = "This region could not be structured from the flowchart — review and rewrite it manually.",

        // Per-element styling
        ["Style_Menu"]      = "🎨 Style",
        ["Style_Line"]      = "Line colour",
        ["Style_Fill"]      = "Fill colour",
        ["Style_Text"]      = "Text colour",
        ["Style_Thickness"] = "Line thickness",
        ["Style_Reset"]     = "Reset style",
        ["Style_Inherit"]   = "Inherit (default)",
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

        // Common
        ["Common_Untitled"] = "Unbenannt",
        ["Flow_EditText"]   = "✎ Text bearbeiten…",

        // Structogram editor
        ["Struct_Title"]          = "▦  Struktogramm — {0}",
        ["Struct_Hint"]           = "Rechtsklick auf einen Block: bearbeiten / einfügen / umschließen. ＋ zum Hinzufügen.",
        ["Struct_PhStatement"]    = "(Anweisung)",
        ["Struct_PhCondition"]    = "(Bedingung)",
        ["Struct_True"]           = "wahr",
        ["Struct_False"]          = "falsch",
        ["Struct_PhWhile"]        = "solange (…)",
        ["Struct_PhDoWhile"]      = "tue … solange (…)",
        ["Struct_PhSelector"]     = "(Selektor)",
        ["Struct_Case"]           = "Fall",
        ["Struct_InsertAbove"]    = "Darüber einfügen",
        ["Struct_InsertBelow"]    = "Darunter einfügen",
        ["Struct_AddLoopBody"]    = "Zum Schleifenkörper hinzufügen",
        ["Struct_AddTrue"]        = "Zum WAHR-Zweig hinzufügen",
        ["Struct_AddFalse"]       = "Zum FALSCH-Zweig hinzufügen",
        ["Struct_AddArm"]         = "Fall-Zweig hinzufügen",
        ["Struct_DeleteBlock"]    = "✕ Block löschen",
        ["Struct_KStatement"]     = "Anweisung",
        ["Struct_KIf"]            = "Wenn / Sonst",
        ["Struct_KWhile"]         = "While-Schleife (kopfgesteuert)",
        ["Struct_KDoWhile"]       = "Do-While-Schleife (fußgesteuert)",
        ["Struct_KCase"]          = "Fallauswahl (mehrfach)",
        ["Struct_AddBlockTip"]    = "Einen Block hinzufügen",
        ["Struct_AddInline"]      = "＋ hinzufügen",
        ["Struct_PromptStatement"] = "Anweisung",
        ["Struct_PromptCondition"] = "Bedingung / Ausdruck",
        ["Struct_PromptCaseLabel"] = "Fall-Beschriftung",
        ["Struct_DefCondition"]   = "Bedingung",
        ["Struct_DefWhile"]       = "solange (Bedingung)",
        ["Struct_DefDoWhile"]     = "tue … solange (Bedingung)",
        ["Struct_DefSelector"]    = "Selektor",
        ["Struct_DefStatement"]   = "Anweisung",
        ["Struct_FlaggedTip"]     = "Dieser Bereich konnte nicht aus dem Ablaufplan strukturiert werden — bitte manuell prüfen und umschreiben.",

        // Per-element styling
        ["Style_Menu"]      = "🎨 Stil",
        ["Style_Line"]      = "Linienfarbe",
        ["Style_Fill"]      = "Füllfarbe",
        ["Style_Text"]      = "Textfarbe",
        ["Style_Thickness"] = "Linienstärke",
        ["Style_Reset"]     = "Stil zurücksetzen",
        ["Style_Inherit"]   = "Erben (Standard)",
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
