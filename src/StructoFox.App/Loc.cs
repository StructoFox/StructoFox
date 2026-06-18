using System.Text.Json;

namespace StructoFox.App;

/// <summary>
/// Tiny localization facade. <c>Loc.S(key)</c> resolves a string: a JSON overlay (Languages/&lt;lang&gt;.json)
/// first, then the built-in table, then English, then the raw key. On startup it exports the built-in
/// English (and German) tables to Languages/*.json so translators have a ready file to edit.
/// </summary>
public static class Loc
{
    // Active UI language code ("en", "de", ...). The fox is bilingual for now; more tongues later.
    public static string Lang { get; set; } = "en";

    // User-editable translations, loaded from Languages/<lang>.json — take priority over the built-ins.
    static Dictionary<string, string> _overlay = new();

    /// <summary>Where the externalised translation files live (next to the app).</summary>
    public static string LangDir => Path.Combine(AppContext.BaseDirectory, "Languages");

    /// <summary>Exports the built-in tables as starter JSON (if missing) and loads the active overlay.
    /// Call once at startup.</summary>
    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(LangDir);
            ExportIfMissing("en", En);
            ExportIfMissing("de", De);
            _overlay = LoadOverlay(Lang);
        }
        catch { /* localization is best-effort; built-ins remain the fallback */ }
    }

    // Writes a language table to Languages/<lang>.json the first time, as a base for translators.
    static void ExportIfMissing(string lang, Dictionary<string, string> dict)
    {
        var path = Path.Combine(LangDir, lang + ".json");
        if (!File.Exists(path)) File.WriteAllText(path, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
    }

    // Reads a Languages/<lang>.json overlay, or an empty map if absent/unreadable.
    static Dictionary<string, string> LoadOverlay(string lang)
    {
        var path = Path.Combine(LangDir, lang + ".json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new() : new(); }
        catch { return new(); }
    }

    // English baseline — the safety net every key falls back to if a translation is missing.
    static readonly Dictionary<string, string> En = new()
    {
        ["App_Title"]    = "StructoFox",
        ["App_Tagline"]  = "Flow · Struct · Code",
        ["Smoke_Header"] = "Core smoke test — C# generated from a sample class:",

        // Home / project browser
        ["Home_NewProject"]    = "➕  New project",
        ["Home_AddLibrary"]    = "📁  Add library",
        ["Home_NewProjectTip"] = "Create a new project — a folder holding its classes, functions, diagrams and boards",
        ["Home_AddLibraryTip"] = "Register a folder that contains several projects — a shelf StructoFox scans",
        ["Home_Recent"]        = "🕘  Recent",
        ["Home_Libraries"]     = "Libraries",
        ["Home_RemoveLibrary"] = "✕  Remove library",
        ["Home_NoProjects"]    = "No projects",
        ["Home_NoProjectsHint"] = "No projects here yet — create one with ➕",

        // Cockpit
        ["Cockpit_Exit"]    = "Exit",
        ["Cockpit_ExitTip"] = "Back to projects",

        // New-project dialog
        ["NewProj_Title"]     = "New project",
        ["NewProj_Name"]      = "Project name:",
        ["NewProj_NoLib"]     = "No project library yet. Choose a folder where your projects will live:",
        ["NewProj_ChooseLib"] = "Create the project in:",
        ["NewProj_Browse"]    = "Browse…",
        ["NewProj_FreeFmt"]   = "{0} MB free on {1}",
        ["Common_OK"]         = "OK",
        ["Common_Cancel"]     = "Cancel",

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

        // Flowchart (PAP) editor
        ["Common_ResetZoomTip"]        = "Reset zoom",
        ["Flow_Title"]                 = "🔁  Flow — {0}",
        ["Flow_Start"]                 = "⬭ Start",
        ["Flow_Process"]               = "▭ Process",
        ["Flow_Decision"]              = "◇ Decision",
        ["Flow_IO"]                    = "▱ I/O",
        ["Flow_Subroutine"]            = "⊟ Subroutine",
        ["Flow_End"]                   = "⬭ End",
        ["Flow_Note"]                  = "✎ Note",
        ["Flow_AddNodeTip"]            = "Add a {0} node",
        ["Flow_Select"]                = "➤ Select/Move",
        ["Flow_SelectTip"]             = "Normal mode: select and drag nodes",
        ["Flow_Connect"]               = "→ Connect",
        ["Flow_ConnectTip"]            = "Click a node, then another, to draw an arrow",
        ["Flow_Remove"]                = "✕ Remove",
        ["Flow_RemoveTip"]             = "Click a node or arrow to delete it",
        ["Flow_DefStart"]              = "Start",
        ["Flow_DefEnd"]                = "End",
        ["Flow_DefDecision"]           = "condition?",
        ["Flow_DefIO"]                 = "input / output",
        ["Flow_DefCall"]               = "call …",
        ["Flow_DefNote"]               = "note",
        ["Flow_DefStep"]               = "step",
        ["Flow_DeleteNode"]            = "✕ Delete node",
        ["Flow_NodeTextPrompt"]        = "Node text",
        ["Flow_BranchPrompt"]          = "Branch label (e.g. yes / no)",
        ["Flow_ArrowPrompt"]           = "Arrow label",
        ["Flow_EditLabel"]             = "✎ Edit label…",
        ["Flow_DeleteArrow"]           = "✕ Delete arrow",
        ["Flow_FlipArrow"]             = "⇄ Flip arrow direction",
        ["Flow_ToStructogramTip"]      = "Convert this flowchart to a structogram",
        ["Flow_ToStructogramTitle"]    = "Convert to structogram",
        ["Flow_ToStructogramOverwrite"] = "A structogram already exists for this function. Overwrite it with the converted flowchart?",
        ["Flow_ToStructogramPartial"]  = "Converted — but some parts of the flowchart could not be structured. They are flagged in amber in the structogram.",
        ["Flow_Background"]            = "Canvas background colour",
        ["NodeTxt_Title"]              = "Node text",
        ["NodeTxt_Font"]               = "Font",
        ["NodeTxt_Size"]               = "Size",
        ["NodeTxt_Bold"]               = "Bold",
        ["NodeTxt_Italic"]             = "Italic",
        ["NodeTxt_Underline"]          = "Underline",
        ["NodeTxt_Strike"]             = "Strikethrough",

        // Per-element styling
        ["Style_Menu"]      = "🎨 Style",
        ["Style_Line"]      = "Line colour",
        ["Style_Fill"]      = "Fill colour",
        ["Style_Text"]      = "Text colour",
        ["Style_Thickness"] = "Line thickness",
        ["Style_Reset"]     = "Reset style",
        ["Style_Inherit"]   = "Inherit (default)",
        ["Style_Open"]      = "🎨 Style…",
        ["Palette_Choose"]  = "Choose palette",

        // Style editor window
        ["StyleEd_Title"]     = "🎨 Element style",
        ["StyleEd_Preset"]    = "Preset slot:",
        ["StyleEd_Apply"]     = "Apply",
        ["StyleEd_SaveSlot"]  = "💾 Save as slot…",
        ["StyleEd_Line"]      = "Line colour",
        ["StyleEd_Fill"]      = "Fill colour",
        ["StyleEd_Text"]      = "Text colour",
        ["StyleEd_Thickness"] = "Line thickness",
        ["StyleEd_Preview"]   = "Preview",
        ["StyleEd_Sample"]    = "Sample text",
        ["StyleEd_SlotName"]  = "Slot name:",
        ["Common_OK"]         = "OK",
        ["Common_Cancel"]     = "Cancel",
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

        // Flowchart (PAP) editor
        ["Common_ResetZoomTip"]        = "Zoom zurücksetzen",
        ["Flow_Title"]                 = "🔁  Ablauf — {0}",
        ["Flow_Start"]                 = "⬭ Start",
        ["Flow_Process"]               = "▭ Prozess",
        ["Flow_Decision"]              = "◇ Verzweigung",
        ["Flow_IO"]                    = "▱ E/A",
        ["Flow_Subroutine"]            = "⊟ Unterprogramm",
        ["Flow_End"]                   = "⬭ Ende",
        ["Flow_Note"]                  = "✎ Notiz",
        ["Flow_AddNodeTip"]            = "Einen {0}-Knoten hinzufügen",
        ["Flow_Select"]                = "➤ Auswählen/Bewegen",
        ["Flow_SelectTip"]             = "Normalmodus: Knoten auswählen und ziehen",
        ["Flow_Connect"]               = "→ Verbinden",
        ["Flow_ConnectTip"]            = "Klicke einen Knoten, dann einen anderen, um einen Pfeil zu ziehen",
        ["Flow_Remove"]                = "✕ Entfernen",
        ["Flow_RemoveTip"]             = "Klicke einen Knoten oder Pfeil, um ihn zu löschen",
        ["Flow_DefStart"]              = "Start",
        ["Flow_DefEnd"]                = "Ende",
        ["Flow_DefDecision"]           = "Bedingung?",
        ["Flow_DefIO"]                 = "Eingabe / Ausgabe",
        ["Flow_DefCall"]               = "Aufruf …",
        ["Flow_DefNote"]               = "Notiz",
        ["Flow_DefStep"]               = "Schritt",
        ["Flow_DeleteNode"]            = "✕ Knoten löschen",
        ["Flow_NodeTextPrompt"]        = "Knotentext",
        ["Flow_BranchPrompt"]          = "Zweig-Beschriftung (z.B. ja / nein)",
        ["Flow_ArrowPrompt"]           = "Pfeil-Beschriftung",
        ["Flow_EditLabel"]             = "✎ Beschriftung bearbeiten…",
        ["Flow_DeleteArrow"]           = "✕ Pfeil löschen",
        ["Flow_FlipArrow"]             = "⇄ Pfeilrichtung umkehren",
        ["Flow_ToStructogramTip"]      = "Diesen Programmablaufplan in ein Struktogramm umwandeln",
        ["Flow_ToStructogramTitle"]    = "In Struktogramm umwandeln",
        ["Flow_ToStructogramOverwrite"] = "Für diese Funktion existiert bereits ein Struktogramm. Mit dem umgewandelten Ablaufplan überschreiben?",
        ["Flow_ToStructogramPartial"]  = "Umgewandelt — aber einige Teile des Ablaufplans ließen sich nicht strukturieren. Sie sind im Struktogramm bernsteinfarben markiert.",
        ["Flow_Background"]            = "Hintergrundfarbe der Zeichenfläche",
        ["NodeTxt_Title"]              = "Knotentext",
        ["NodeTxt_Font"]               = "Schriftart",
        ["NodeTxt_Size"]               = "Größe",
        ["NodeTxt_Bold"]               = "Fett",
        ["NodeTxt_Italic"]             = "Kursiv",
        ["NodeTxt_Underline"]          = "Unterstrichen",
        ["NodeTxt_Strike"]             = "Durchgestrichen",

        // Per-element styling
        ["Style_Menu"]      = "🎨 Stil",
        ["Style_Line"]      = "Linienfarbe",
        ["Style_Fill"]      = "Füllfarbe",
        ["Style_Text"]      = "Textfarbe",
        ["Style_Thickness"] = "Linienstärke",
        ["Style_Reset"]     = "Stil zurücksetzen",
        ["Style_Inherit"]   = "Erben (Standard)",
        ["Style_Open"]      = "🎨 Stil…",
        ["Palette_Choose"]  = "Palette wählen",

        // Style editor window
        ["StyleEd_Title"]     = "🎨 Element-Stil",
        ["StyleEd_Preset"]    = "Stil-Slot:",
        ["StyleEd_Apply"]     = "Anwenden",
        ["StyleEd_SaveSlot"]  = "💾 Als Slot speichern…",
        ["StyleEd_Line"]      = "Linienfarbe",
        ["StyleEd_Fill"]      = "Füllfarbe",
        ["StyleEd_Text"]      = "Textfarbe",
        ["StyleEd_Thickness"] = "Linienstärke",
        ["StyleEd_Preview"]   = "Vorschau",
        ["StyleEd_Sample"]    = "Beispieltext",
        ["StyleEd_SlotName"]  = "Slot-Name:",
        ["Common_OK"]         = "OK",
        ["Common_Cancel"]     = "Abbrechen",
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
        if (_overlay.TryGetValue(key, out var ov)) return ov;
        if (Tables.TryGetValue(Lang, out var t) && t.TryGetValue(key, out var v)) return v;
        if (En.TryGetValue(key, out var en)) return en;
        return key;
    }
}
