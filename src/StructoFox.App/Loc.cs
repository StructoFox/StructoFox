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
            // Active language: the user's saved choice if any, otherwise the OS culture
            // (German system → German UI), otherwise English. This is why everything used to
            // come out English — nothing ever set Lang away from its "en" default.
            var pref = AppSettings.Lang;
            Lang = !string.IsNullOrWhiteSpace(pref) ? pref : DetectCulture();

            Directory.CreateDirectory(LangDir);
            ExportIfMissing("en", En);
            ExportIfMissing("de", De);
            _overlay = LoadOverlay(Lang);
        }
        catch { /* localization is best-effort; built-ins remain the fallback */ }
    }

    /// <summary>Switches the active UI language, persists the choice and reloads its overlay. Callers
    /// rebuild their UI afterwards to re-resolve every string.</summary>
    public static void SetLanguage(string code)
    {
        Lang = code;
        AppSettings.Lang = code;
        _overlay = LoadOverlay(code);
    }

    // Maps the OS UI culture to one of our supported language codes (German → "de", else "en").
    static string DetectCulture()
    {
        try { return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "de" : "en"; }
        catch { return "en"; }
    }

    /// <summary>The languages StructoFox ships built-in (code → display name), for the switcher.</summary>
    public static IReadOnlyList<(string Code, string Name)> Builtins { get; } =
        new[] { ("en", "English"), ("de", "Deutsch") };

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

        // Main (entry point) tab
        ["Main_Blurb"]  = "The program's entry point — its main(). One per project; it gets its own tab so it stays in sight.",
        ["Main_Create"] = "▶ Create main",
        ["Main_Unset"]  = "Unset as main",
        ["Main_SetAs"]  = "▶ Set as main",

        // Cockpit
        ["Cockpit_Exit"]    = "Back",
        ["Cockpit_ExitTip"] = "Back to projects",
        ["Sec_New"]         = "＋ New {0}",
        ["Sec_NewPrompt"]   = "{0} name:",
        // Section type labels (singular for "New …", plural for headings/empties).
        ["SecSg_Boards"] = "Board", ["SecSg_Main"] = "Main", ["SecSg_Namespace"] = "Namespace",
        ["SecSg_Class"] = "Class", ["SecSg_Struct"] = "Struct", ["SecSg_Interface"] = "Interface",
        ["SecSg_Enum"] = "Enum", ["SecSg_Function"] = "Function", ["SecSg_Object"] = "Object", ["SecSg_Export"] = "Export",
        ["SecPl_Boards"] = "Boards", ["SecPl_Main"] = "Main", ["SecPl_Namespace"] = "Namespaces",
        ["SecPl_Class"] = "Classes", ["SecPl_Struct"] = "Structs", ["SecPl_Interface"] = "Interfaces",
        ["SecPl_Enum"] = "Enums", ["SecPl_Function"] = "Functions", ["SecPl_Object"] = "Objects", ["SecPl_Export"] = "Export",
        ["Sec_FilterName"]  = "Filter by name…",
        ["Sec_FilterSortTip"] = "Filter by name & sort",
        ["Sec_NsAll"]       = "(all namespaces)",
        ["Sec_NsNone"]      = "(no namespace)",
        ["Sec_SetNs"]       = "🏷 Set namespace…",
        ["Sec_NsPrompt"]    = "Namespace (empty = none):",
        ["Sec_DupMsg"]      = "A {0} named “{1}” already exists in this namespace. Create another one anyway?",
        ["Sec_DupTitle"]    = "Name already exists",
        ["Sec_Empty"]       = "No {0} yet.",
        ["Sec_Delete"]      = "✕  Delete",
        ["Sec_DeleteTitle"]    = "Delete",
        ["Sec_DeleteConfirm1"] = "Delete “{0}”? Everything attached to it (members, diagrams) goes too.",
        ["Sec_DeleteConfirmN"] = "Delete {0} selected entities? Everything attached to them goes too.",
        ["Sec_DeleteBoardWarn"] = "Note: board(s) are assigned to this ({0}). They are NOT deleted — remove them manually in the Boards tab.",
        ["Sec_BoardsBlurb"] = "Structure boards — arrange entities on a canvas. (Board canvas port coming.)",
        ["Sec_ExportBlurb"] = "Generate source from your structures in 10 languages. (Wiring coming.)",

        // New-project dialog
        ["NewProj_Title"]     = "New project",
        ["NewProj_Name"]      = "Project name:",
        ["NewProj_NoLib"]     = "No project library yet. Choose a folder where your projects will live:",
        ["NewProj_ChooseLib"] = "Create the project in:",
        ["NewProj_Browse"]    = "Browse…",
        ["NewProj_FreeFmt"]   = "{0} MB free on {1}",
        ["Common_OK"]         = "OK",
        ["Common_Cancel"]     = "Cancel",
        ["Common_DontShowAgain"] = "Don't show this again",

        // Diagram chooser (DiagramLauncher)
        ["Diag_Title"]     = "Diagram",
        ["Diag_SketchOf"]  = "Sketch the flow of:\n{0}",
        ["Diag_Pap"]       = "🔁 Programmablaufplan",
        ["Diag_PapExists"] = "🔁 Programmablaufplan (exists)",
        ["Diag_Ns"]        = "▦ Structogram",
        ["Diag_NsExists"]  = "▦ Structogram (exists)",
        ["Diag_NsTip"]     = "Nassi-Shneiderman structogram editor (DIN 66261)",
        ["Diag_Board"]     = "🗺 Board",
        ["Diag_BoardExists"] = "🗺 Board (exists)",
        ["Diag_BoardTip"]  = "Dataflow board — wire functions together to generate this body",
        ["Code_GenBody"]    = "⚙ Generate function",
        ["Code_GenBodyTip"] = "Turn the board's wiring into this function/method's structogram",
        ["Code_GenTitle"]   = "Generate from board",
        ["Code_GenDone"]    = "Generated a structogram with {0} step(s) from the board's wiring.",

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
        ["Struct_KSubroutine"]    = "Subroutine (links to a diagram)",
        ["Struct_PhSubroutine"]   = "(subprogram)",
        ["Struct_DefSubroutine"]  = "subprogram",
        ["Struct_ShowChart"]      = "🗺 Show chart…",
        ["Struct_ShowChartTip"]   = "Double-click to open this subprogram's diagram",
        ["Sub_NamePrompt"]        = "Subprogram (function) name:",
        ["Sub_LinkTitle"]         = "Link subprogram",
        ["Sub_Namespace"]         = "Namespace (to pick from / create in):",
        ["Sub_PickExisting"]      = "Pick an existing function",
        ["Sub_CreateNew"]         = "Create a new function:",
        ["Sub_DuplicateTitle"]    = "Function already exists",
        ["Sub_DuplicateMsg"]      = "A function with this name already exists in this namespace.\n\nYes = use the existing one,  No = create another anyway,  Cancel = go back.",
        ["Sub_RemoveInfo"]        = "The subprogram stays in the Functions library — only this block/node was removed. To delete it for good, remove it in the Functions tab.",
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
        ["Flow_Connector"]             = "◯ Connector",
        ["Flow_DefConnector"]          = "A",
        ["Flow_Junction"]              = "• Junction",
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
        ["Flow_Transmission"]          = "⚡ Make transmission path",
        ["Flow_TransmissionOff"]       = "⚡ Back to normal flow line",
        ["Flow_ToStructogramTip"]      = "Convert this flowchart to a structogram",
        ["Flow_ToStructogramTitle"]    = "Convert to structogram",
        ["Flow_ToStructogramOverwrite"] = "A structogram already exists for this function. Overwrite it with the converted flowchart?",
        ["Flow_ToStructogramPartial"]  = "Converted — but some parts of the flowchart could not be structured. They are flagged in amber in the structogram.",
        ["Flow_Background"]            = "Canvas background colour",
        ["Flow_LinesOrtho"]           = "⌐ Lines: orthogonal",
        ["Flow_LinesDiagonal"]        = "⟍ Lines: diagonal",
        ["Flow_LinesTip"]             = "Switch between DIN orthogonal flow lines and direct diagonal arrows",
        ["Flow_Symbol"]               = "DIN symbol",
        ["Flow_SymAuto"]              = "▱ Default (input/output)",
        ["Flow_SymDocument"]          = "📄 Document (printout)",
        ["Flow_SymDisplay"]           = "🖥 Display",
        ["Flow_SymManualInput"]       = "⌨ Manual input",
        ["Flow_SymPunchedCard"]       = "🗒 Punched card",
        ["Flow_SymMagneticTape"]      = "🎞 Magnetic tape",
        ["Flow_SymMagneticDisk"]      = "🛢 Magnetic disk / database",
        ["Flow_SymStoredData"]        = "🗄 Stored data",
        ["Flow_SymOffPage"]           = "⬠ Off-page connector (next page)",
        ["Flow_CatStartEnd"]          = "⬭ Start / End",
        ["Flow_CatProcess"]           = "▭ Process",
        ["Flow_CatIO"]                = "▱ I/O",
        ["Flow_CatConnect"]           = "🔗 Connect",
        ["Flow_CatTip"]               = "Hover to choose a variant",
        ["Flow_ArrowDin"]             = "➜ Arrow (DIN, orthogonal)",
        ["Flow_ArrowFree"]            = "⟍ Arrow (free, diagonal)",
        ["NodeTxt_Title"]              = "Node text",
        ["NodeTxt_Font"]               = "Font",
        ["NodeTxt_Size"]               = "Size",
        ["NodeTxt_Bold"]               = "Bold",
        ["NodeTxt_Italic"]             = "Italic",
        ["NodeTxt_Underline"]          = "Underline",
        ["NodeTxt_Strike"]             = "Strikethrough",

        // Diagram decoration (title / watermark / logo)
        ["Decor_Title"]      = "Plan decoration",
        ["Decor_Open"]       = "🏷 Decoration…",
        ["Decor_OpenTip"]    = "Title, watermark and logo on the plan",
        ["Decor_TitleText"]  = "Title:",
        ["Decor_ShowTitle"]  = "Show the title on the plan",
        ["Decor_TitlePos"]   = "Position:",
        ["Decor_TitleSize"]  = "Size:",
        ["Decor_TitleBold"]  = "Bold",
        ["Decor_TitleColor"] = "Title colour",
        ["Decor_TitleEditTip"] = "Right-click: edit title properties",
        ["Decor_WmAngle"]    = "Watermark angle (°):",
        ["Decor_Watermark"]  = "Watermark (faint diagonal text):",
        ["Decor_WatermarkImg"] = "Watermark image (faint, centred):",
        ["Decor_Logo"]       = "Logo image:",
        ["Decor_Browse"]     = "Browse…",
        ["Decor_ClearLogo"]  = "Clear",
        ["Decor_Corner"]     = "Logo corner:",

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

        // Entity editor dialog
        ["Common_Name"]            = "Name",
        ["Common_None"]            = "(none)",
        ["Common_Save"]            = "Save",
        ["Common_AddPlus"]         = "＋ Add",
        ["CodeEdit_Title"]         = "Edit {0}",
        ["CodeEdit_Type"]          = "Type",
        ["CodeEdit_Namespace"]     = "Namespace",
        ["CodeEdit_ParentNamespace"] = "Parent namespace",
        ["CodeEdit_Description"]   = "Description",
        ["CodeEdit_Inherits"]      = "Inherits from",
        ["CodeEdit_Implements"]    = "Implements",
        ["CodeEdit_NoInterfaces"]  = "(no interfaces defined yet)",
        ["CodeEdit_InstanceOf"]    = "Instance of",
        ["CodeEdit_Methods"]       = "Methods",
        ["CodeEdit_Fields"]        = "Fields",
        ["CodeEdit_EnumValues"]    = "Enum values",
        ["CodeEdit_DataPorts"]     = "Data ports",
        ["CodeEdit_ConstructorLbl"]= "constructor",
        ["CodeEdit_DestructorLbl"] = "destructor",
        ["CodeEdit_MethodFlowTip"] = "Sketch this method's flow / structogram",
        ["CodeEdit_AddParam"]      = "＋ param",
        ["CodeEdit_AddMethod"]     = "Method",
        ["CodeEdit_AddConstructor"]= "Constructor",
        ["CodeEdit_AddDestructor"] = "Destructor",
        ["CodeEdit_AddGetter"]     = "Add getter (Get…)",
        ["CodeEdit_AddSetter"]     = "Add setter (Set…)",
        ["CodeEdit_DefaultValue"]  = "Value",
        ["CodeEdit_Delete"]        = "✕  Delete",
        ["CodeEdit_DeleteConfirm"] = "Delete “{0}”? Anything attached to it (parameters, diagrams, …) goes with it.",
        ["CodeEdit_DeleteTitle"]   = "Delete",
        ["CodeEdit_DeleteBoardWarn"] = "A board is assigned to this method ({0}). It is NOT deleted — remove it manually in the Boards tab.",
        ["CodeEdit_AssignBoardTip"]  = "Assign an existing board to author this method",
        ["CodeEdit_AssignBoardTitle"] = "Pick a board for this method",
        ["Common_NameColon"]       = "Name:",
        ["Common_Add"]             = "Add",

        // Boards gallery (cockpit Boards section)
        ["Boards_New"]        = "＋ New board",
        ["Boards_NewPrompt"]  = "Board name:",
        ["Boards_Default"]    = "Board",
        ["Boards_Empty"]      = "No boards yet — create one with ＋.",
        ["Boards_Open"]       = "Open",
        ["Boards_Assign"]     = "🎯 Assign to function…",
        ["Boards_AssignTitle"] = "Assign this board to author…",
        ["Boards_ClearAssign"] = "Clear assignment",
        ["Boards_NoTargets"]  = "No functions or methods to assign to yet.",
        ["Boards_NoBoards"]   = "No boards yet — create one in the Boards tab first.",
        ["Boards_HasNonFunc"] = "This board contains classes/objects, so it can't author a function body (only function-wiring boards can). Use a board with functions only.",
        ["Boards_Rename"]     = "✎ Rename…",
        ["Boards_Delete"]     = "✕  Delete board",
        ["Boards_DeleteConfirm"] = "Delete the board \"{0}\"? (Entities stay; only this board's layout is removed.)",
        ["Boards_DeleteTitle"]   = "Delete board",

        // Code board canvas
        ["Code_AddToBoard"]       = "＋ Add",
        ["Code_AddToBoardTip"]    = "Add a new or existing entity to this board",
        ["Code_AllTypes"]         = "(all types)",
        ["Code_ConnectPorts"]     = "→ Connect ports",
        ["Code_ConnectPortsTip"]  = "Click an output port, then a matching input port, to wire them",
        ["Code_RemoveCards"]      = "✕ Remove from board",
        ["Code_RemoveCardsTip"]   = "Remove the selected cards from this board (entities are kept)",
        ["Code_ExportAll"]        = "⇩ Export all",
        ["Code_ExportAllTip"]     = "Generate source for every entity on this board",
        ["Code_ExportSelected"]   = "⇩ Export selected",
        ["Code_ExportSelectedTip"]= "Generate source for the selected entities",
        ["Code_NoSelection"]      = "Nothing selected.",
        ["Code_ConnStartOutput"]  = "Start a connection at an output port (right side).",
        ["Code_ConnEndInput"]     = "Finish a connection at an input port (left side).",
        ["Code_ConnTitle"]        = "Connect ports",
        ["Code_MismatchConv"]     = "Passing convention mismatch: {0} is {1} but {2} is {3}.",
        ["Code_MismatchType"]     = "Type mismatch: {0} is {1} but {2} is {3}.",
        ["Code_MismatchTitle"]    = "Cannot connect",
        ["Code_AddType"]          = "Add {0}…",
        ["Code_Edit"]             = "✎ Edit…",
        ["Code_SketchFlow"]       = "🔁 Sketch flow…",
        ["Code_ExportThis"]       = "⇩ Export this",
        ["Code_SwitchVertical"]   = "Ports: switch to vertical",
        ["Code_SwitchHorizontal"] = "Ports: switch to horizontal",
        ["Code_RemoveFromBoard"]  = "Remove from board",
        ["Board_RemoveInfo"]      = "These entities stay in the project libraries — only removed from this board. To delete one for good, use its right-click → Delete in the library.",
        ["Code_DeletePerm"]       = "✕ Delete entity (permanent)",
        ["Code_DeletePermConfirm"]= "Permanently delete \"{0}\" from the project? This cannot be undone.",
        ["Code_DeleteEntityTitle"]= "Delete entity",
        ["Code_DeleteConnection"] = "✕ Delete connection",
        ["Code_AddExisting"]      = "Add existing…",
        ["Code_AddExistingTitle"] = "Add existing entity",
        ["Code_AllOnBoard"]       = "Every entity is already on this board.",
        ["Code_AddEntityTitle"]   = "Add entity",
        ["Code_NewTypeTitle"]     = "New {0}",
        ["Code_BodyFuncOnly"]     = "This board authors a function body, so it takes function cards only (no classes/objects).",

        // Code exporter
        ["Export_Title"]    = "Export — {0}",
        ["Export_Language"] = "Language:",
        ["Export_Count"]    = "{0} entities",
        ["Export_Copy"]     = "📋 Copy",
        ["Export_Save"]     = "💾 Save…",
        ["Export_Close"]    = "Close",
        ["Export_Open"]     = "⇩ Open exporter",
        ["Export_Empty"]    = "No entities to export yet — add some in the structure sections first.",
        ["Export_Intro"]    = "Generate source skeletons from your structures in 10 languages. Function/method bodies come from their structograms where you've drawn them.",

        // App menu
        ["Menu_About"]      = "ℹ Info  (v{0})",
        ["Menu_AboutTitle"] = "About StructoFox",
        ["Menu_Language"]   = "🌐 Language",
        ["Menu_Options"]    = "⚙ Options",
        ["Opt_NormWarn"]    = "Warn about non-DIN elements",
        ["Opt_NormMark"]    = "Mark non-DIN elements (N̶)",
        ["Opt_SubRemove"]   = "Subroutine-removal note",
        ["Opt_BoardRemove"] = "Board-removal note",
        ["Norm_Title"]      = "Not DIN-compliant",
        ["Norm_DiagonalWarn"] = "Diagonal centre-to-centre arrows aren't DIN 66001-compliant. Use orthogonal flow lines for a norm-compliant chart. (You can turn this warning off in Options.)",

        // Project context menu (home)
        ["Proj_Rename"]       = "✎ Rename…",
        ["Proj_RenamePrompt"] = "Project name:",
        ["Proj_OpenFolder"]   = "📂 Open folder",
    };

    // German overlay; any key not listed here quietly falls through to the English baseline.
    static readonly Dictionary<string, string> De = new()
    {
        // Cockpit / section chrome
        ["Sec_New"]         = "＋ Neu: {0}",
        ["Sec_NewPrompt"]   = "{0}-Name:",
        ["Sec_Empty"]       = "Noch keine {0}.",
        ["SecSg_Boards"] = "Board", ["SecSg_Main"] = "Main", ["SecSg_Namespace"] = "Namespace",
        ["SecSg_Class"] = "Klasse", ["SecSg_Struct"] = "Struct", ["SecSg_Interface"] = "Interface",
        ["SecSg_Enum"] = "Enum", ["SecSg_Function"] = "Funktion", ["SecSg_Object"] = "Objekt", ["SecSg_Export"] = "Export",
        ["SecPl_Boards"] = "Boards", ["SecPl_Main"] = "Main", ["SecPl_Namespace"] = "Namespaces",
        ["SecPl_Class"] = "Klassen", ["SecPl_Struct"] = "Structs", ["SecPl_Interface"] = "Interfaces",
        ["SecPl_Enum"] = "Enums", ["SecPl_Function"] = "Funktionen", ["SecPl_Object"] = "Objekte", ["SecPl_Export"] = "Export",

        ["Smoke_Header"]   = "Core-Rauchtest — aus einer Beispielklasse generiertes C#:",
        ["Diag_Title"]     = "Diagramm",
        ["Diag_SketchOf"]  = "Ablauf skizzieren von:\n{0}",
        ["Diag_PapExists"] = "🔁 Programmablaufplan (vorhanden)",
        ["Diag_Ns"]        = "▦ Struktogramm",
        ["Diag_NsExists"]  = "▦ Struktogramm (vorhanden)",
        ["Diag_NsTip"]     = "Nassi-Shneiderman-Struktogramm-Editor (DIN 66261)",
        ["Diag_Board"]     = "🗺 Board",
        ["Diag_BoardExists"] = "🗺 Board (vorhanden)",
        ["Diag_BoardTip"]  = "Datenfluss-Board — Funktionen verdrahten, um diesen Rumpf zu erzeugen",
        ["Code_GenBody"]    = "⚙ Funktion erzeugen",
        ["Code_GenBodyTip"] = "Board-Verdrahtung in das Struktogramm dieser Funktion/Methode umwandeln",
        ["Code_GenTitle"]   = "Aus Board erzeugen",
        ["Code_GenDone"]    = "Struktogramm mit {0} Schritt(en) aus der Board-Verdrahtung erzeugt.",

        // Common
        ["Common_Untitled"] = "Unbenannt",
        ["Flow_EditText"]   = "✎ Text bearbeiten…",

        // Main (entry point) tab
        ["Main_Blurb"]  = "Der Einstiegspunkt des Programms — die main(). Eine pro Projekt; sie bekommt einen eigenen Reiter, damit sie sichtbar bleibt.",
        ["Main_Create"] = "▶ Main anlegen",
        ["Main_Unset"]  = "Als Main entfernen",
        ["Main_SetAs"]  = "▶ Als Main setzen",

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
        ["Struct_KSubroutine"]    = "Unterprogramm (verlinkt ein Diagramm)",
        ["Struct_PhSubroutine"]   = "(Unterprogramm)",
        ["Struct_DefSubroutine"]  = "Unterprogramm",
        ["Struct_ShowChart"]      = "🗺 Diagramm zeigen…",
        ["Struct_ShowChartTip"]   = "Doppelklick öffnet das Diagramm dieses Unterprogramms",
        ["Sub_NamePrompt"]        = "Unterprogramm-(Funktions-)Name:",
        ["Sub_LinkTitle"]         = "Unterprogramm verknüpfen",
        ["Sub_Namespace"]         = "Namespace (zum Auswählen / Anlegen):",
        ["Sub_PickExisting"]      = "Vorhandene Funktion wählen",
        ["Sub_CreateNew"]         = "Neue Funktion anlegen:",
        ["Sub_DuplicateTitle"]    = "Funktion existiert bereits",
        ["Sub_DuplicateMsg"]      = "Eine Funktion mit diesem Namen existiert in diesem Namespace bereits.\n\nJa = die vorhandene verwenden,  Nein = trotzdem eine weitere anlegen,  Abbrechen = zurück.",
        ["Sec_DupMsg"]            = "Ein/e {0} mit dem Namen „{1}“ existiert in diesem Namespace bereits. Trotzdem eine weitere anlegen?",
        ["Sec_DupTitle"]          = "Name existiert bereits",
        ["Sub_RemoveInfo"]        = "Das Unterprogramm bleibt in der Functions-Bibliothek — entfernt wurde nur dieser Block/Knoten. Zum endgültigen Löschen im Functions-Reiter entfernen.",
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
        ["Flow_Connector"]             = "◯ Konnektor",
        ["Flow_DefConnector"]          = "A",
        ["Flow_Junction"]              = "• Sammelpunkt",
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
        ["Flow_Transmission"]          = "⚡ Zu Übertragungsweg machen",
        ["Flow_TransmissionOff"]       = "⚡ Zurück zur normalen Flusslinie",
        ["Flow_ToStructogramTip"]      = "Diesen Programmablaufplan in ein Struktogramm umwandeln",
        ["Flow_ToStructogramTitle"]    = "In Struktogramm umwandeln",
        ["Flow_ToStructogramOverwrite"] = "Für diese Funktion existiert bereits ein Struktogramm. Mit dem umgewandelten Ablaufplan überschreiben?",
        ["Flow_ToStructogramPartial"]  = "Umgewandelt — aber einige Teile des Ablaufplans ließen sich nicht strukturieren. Sie sind im Struktogramm bernsteinfarben markiert.",
        ["Flow_Background"]            = "Hintergrundfarbe der Zeichenfläche",
        ["Flow_LinesOrtho"]           = "⌐ Linien: orthogonal",
        ["Flow_LinesDiagonal"]        = "⟍ Linien: diagonal",
        ["Flow_LinesTip"]             = "Zwischen DIN-orthogonalen Flusslinien und direkten Diagonalpfeilen wechseln",
        ["Flow_Symbol"]               = "DIN-Symbol",
        ["Flow_SymAuto"]              = "▱ Standard (Ein-/Ausgabe)",
        ["Flow_SymDocument"]          = "📄 Dokument (Ausdruck)",
        ["Flow_SymDisplay"]           = "🖥 Anzeige",
        ["Flow_SymManualInput"]       = "⌨ Handeingabe",
        ["Flow_SymPunchedCard"]       = "🗒 Lochkarte",
        ["Flow_SymMagneticTape"]      = "🎞 Magnetband",
        ["Flow_SymMagneticDisk"]      = "🛢 Magnetplatte / Datenbank",
        ["Flow_SymStoredData"]        = "🗄 Gespeicherte Daten",
        ["Flow_SymOffPage"]           = "⬠ Verbindung (Folgeseite)",
        ["Flow_CatStartEnd"]          = "⬭ Start / Ende",
        ["Flow_CatProcess"]           = "▭ Prozess",
        ["Flow_CatIO"]                = "▱ E/A",
        ["Flow_CatConnect"]           = "🔗 Verbinder",
        ["Flow_CatTip"]               = "Zum Wählen einer Variante mit der Maus drüberfahren",
        ["Flow_ArrowDin"]             = "➜ Pfeil (DIN, orthogonal)",
        ["Flow_ArrowFree"]            = "⟍ Pfeil (frei, schräg)",
        ["NodeTxt_Title"]              = "Knotentext",
        ["NodeTxt_Font"]               = "Schriftart",
        ["NodeTxt_Size"]               = "Größe",
        ["NodeTxt_Bold"]               = "Fett",
        ["NodeTxt_Italic"]             = "Kursiv",
        ["NodeTxt_Underline"]          = "Unterstrichen",
        ["NodeTxt_Strike"]             = "Durchgestrichen",

        // Diagramm-Deko (Titel / Wasserzeichen / Logo)
        ["Decor_Title"]      = "Plan-Dekoration",
        ["Decor_Open"]       = "🏷 Dekoration…",
        ["Decor_OpenTip"]    = "Titel, Wasserzeichen und Logo auf dem Plan",
        ["Decor_TitleText"]  = "Titel:",
        ["Decor_ShowTitle"]  = "Titel auf dem Plan anzeigen",
        ["Decor_TitlePos"]   = "Position:",
        ["Decor_TitleSize"]  = "Größe:",
        ["Decor_TitleBold"]  = "Fett",
        ["Decor_TitleColor"] = "Titelfarbe",
        ["Decor_TitleEditTip"] = "Rechtsklick: Titel-Eigenschaften bearbeiten",
        ["Decor_WmAngle"]    = "Wasserzeichen-Winkel (°):",
        ["Decor_Watermark"]  = "Wasserzeichen (blasser diagonaler Text):",
        ["Decor_WatermarkImg"] = "Wasserzeichen-Bild (blass, zentriert):",
        ["Decor_Logo"]       = "Logo-Bild:",
        ["Decor_Browse"]     = "Durchsuchen…",
        ["Decor_ClearLogo"]  = "Leeren",
        ["Decor_Corner"]     = "Logo-Ecke:",

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
        ["Common_DontShowAgain"] = "Nicht wieder anzeigen",

        // Entity editor dialog
        ["Common_Name"]            = "Name",
        ["Common_None"]            = "(keine)",
        ["Common_Save"]            = "Speichern",
        ["Common_AddPlus"]         = "＋ Hinzufügen",
        ["CodeEdit_Title"]         = "{0} bearbeiten",
        ["CodeEdit_Type"]          = "Typ",
        ["CodeEdit_Namespace"]     = "Namespace",
        ["CodeEdit_ParentNamespace"] = "Übergeordneter Namespace",
        ["CodeEdit_Description"]   = "Beschreibung",
        ["CodeEdit_Inherits"]      = "Erbt von",
        ["CodeEdit_Implements"]    = "Implementiert",
        ["CodeEdit_NoInterfaces"]  = "(noch keine Interfaces definiert)",
        ["CodeEdit_InstanceOf"]    = "Instanz von",
        ["CodeEdit_Methods"]       = "Methoden",
        ["CodeEdit_Fields"]        = "Felder",
        ["CodeEdit_EnumValues"]    = "Enum-Werte",
        ["CodeEdit_DataPorts"]     = "Datenports",
        ["CodeEdit_ConstructorLbl"]= "Konstruktor",
        ["CodeEdit_DestructorLbl"] = "Destruktor",
        ["CodeEdit_MethodFlowTip"] = "Ablauf / Struktogramm dieser Methode skizzieren",
        ["CodeEdit_AddParam"]      = "＋ Parameter",
        ["CodeEdit_AddMethod"]     = "Methode",
        ["CodeEdit_AddConstructor"]= "Konstruktor",
        ["CodeEdit_AddDestructor"] = "Destruktor",
        ["CodeEdit_AddGetter"]     = "Getter hinzufügen (Get…)",
        ["CodeEdit_AddSetter"]     = "Setter hinzufügen (Set…)",
        ["CodeEdit_DefaultValue"]  = "Wert",
        ["CodeEdit_Delete"]        = "✕  Löschen",
        ["CodeEdit_DeleteConfirm"] = "„{0}“ löschen? Alles, was daran hängt (Parameter, Diagramme, …), geht mit.",
        ["CodeEdit_DeleteTitle"]   = "Löschen",
        ["CodeEdit_DeleteBoardWarn"] = "Dieser Methode ist ein Board zugewiesen ({0}). Es wird NICHT gelöscht — bitte manuell im Boards-Reiter entfernen.",
        ["CodeEdit_AssignBoardTip"]  = "Ein vorhandenes Board dieser Methode als Rumpf zuweisen",
        ["CodeEdit_AssignBoardTitle"] = "Board für diese Methode wählen",
        ["Common_NameColon"]       = "Name:",
        ["Common_Add"]             = "Hinzufügen",

        // Boards-Galerie (Cockpit-Sektion)
        ["Boards_New"]        = "＋ Neues Board",
        ["Boards_NewPrompt"]  = "Board-Name:",
        ["Boards_Default"]    = "Board",
        ["Boards_Empty"]      = "Noch keine Boards — mit ＋ eines anlegen.",
        ["Boards_Open"]       = "Öffnen",
        ["Boards_Assign"]     = "🎯 Funktion zuweisen…",
        ["Boards_AssignTitle"] = "Dieses Board zuordnen zu…",
        ["Boards_ClearAssign"] = "Zuweisung entfernen",
        ["Boards_NoTargets"]  = "Noch keine Funktionen/Methoden zum Zuweisen.",
        ["Boards_NoBoards"]   = "Noch keine Boards — lege zuerst eines im Boards-Reiter an.",
        ["Boards_HasNonFunc"] = "Dieses Board enthält Klassen/Objekte und kann daher keinen Funktionsrumpf erzeugen (nur Funktions-Verdrahtungs-Boards). Nimm ein Board nur mit Funktionen.",
        ["Boards_Rename"]     = "✎ Umbenennen…",
        ["Boards_Delete"]     = "✕  Board löschen",
        ["Boards_DeleteConfirm"] = "Board „{0}“ löschen? (Entities bleiben; nur das Layout dieses Boards wird entfernt.)",
        ["Boards_DeleteTitle"]   = "Board löschen",

        // Code-Board-Leinwand
        ["Code_AddToBoard"]       = "＋ Hinzufügen",
        ["Code_AddToBoardTip"]    = "Eine neue oder vorhandene Entity zu diesem Board hinzufügen",
        ["Code_AllTypes"]         = "(alle Typen)",
        ["Code_ConnectPorts"]     = "→ Ports verbinden",
        ["Code_ConnectPortsTip"]  = "Erst einen Ausgangs-Port, dann einen passenden Eingangs-Port klicken",
        ["Code_RemoveCards"]      = "✕ Vom Board entfernen",
        ["Code_RemoveCardsTip"]   = "Ausgewählte Karten vom Board entfernen (Entities bleiben erhalten)",
        ["Code_ExportAll"]        = "⇩ Alle exportieren",
        ["Code_ExportAllTip"]     = "Quellcode für jede Entity auf diesem Board erzeugen",
        ["Code_ExportSelected"]   = "⇩ Auswahl exportieren",
        ["Code_ExportSelectedTip"]= "Quellcode für die ausgewählten Entities erzeugen",
        ["Code_NoSelection"]      = "Nichts ausgewählt.",
        ["Code_ConnStartOutput"]  = "Eine Verbindung beginnt an einem Ausgangs-Port (rechte Seite).",
        ["Code_ConnEndInput"]     = "Eine Verbindung endet an einem Eingangs-Port (linke Seite).",
        ["Code_ConnTitle"]        = "Ports verbinden",
        ["Code_MismatchConv"]     = "Übergabekonvention passt nicht: {0} ist {1}, aber {2} ist {3}.",
        ["Code_MismatchType"]     = "Typ passt nicht: {0} ist {1}, aber {2} ist {3}.",
        ["Code_MismatchTitle"]    = "Verbindung nicht möglich",
        ["Code_AddType"]          = "{0} hinzufügen…",
        ["Code_Edit"]             = "✎ Bearbeiten…",
        ["Code_SketchFlow"]       = "🔁 Ablauf skizzieren…",
        ["Code_ExportThis"]       = "⇩ Diese exportieren",
        ["Code_SwitchVertical"]   = "Ports: auf vertikal umstellen",
        ["Code_SwitchHorizontal"] = "Ports: auf horizontal umstellen",
        ["Code_RemoveFromBoard"]  = "Vom Board entfernen",
        ["Board_RemoveInfo"]      = "Diese Entities bleiben in den Projekt-Bibliotheken — nur vom Board entfernt. Zum endgültigen Löschen in der Bibliothek per Rechtsklick → Löschen.",
        ["Code_DeletePerm"]       = "✕ Entity löschen (dauerhaft)",
        ["Code_DeletePermConfirm"]= "„{0}“ dauerhaft aus dem Projekt löschen? Das kann nicht rückgängig gemacht werden.",
        ["Code_DeleteEntityTitle"]= "Entity löschen",
        ["Code_DeleteConnection"] = "✕ Verbindung löschen",
        ["Code_AddExisting"]      = "Vorhandene hinzufügen…",
        ["Code_AddExistingTitle"] = "Vorhandene Entity hinzufügen",
        ["Code_AllOnBoard"]       = "Alle Entities sind bereits auf diesem Board.",
        ["Code_AddEntityTitle"]   = "Entity hinzufügen",
        ["Code_NewTypeTitle"]     = "Neue(s) {0}",
        ["Code_BodyFuncOnly"]     = "Dieses Board erzeugt einen Funktionsrumpf und nimmt daher nur Funktions-Karten auf (keine Klassen/Objekte).",

        // Code-Export
        ["Export_Title"]    = "Export — {0}",
        ["Export_Language"] = "Sprache:",
        ["Export_Count"]    = "{0} Entities",
        ["Export_Copy"]     = "📋 Kopieren",
        ["Export_Save"]     = "💾 Speichern…",
        ["Export_Close"]    = "Schließen",
        ["Export_Open"]     = "⇩ Exporter öffnen",
        ["Export_Empty"]    = "Noch nichts zu exportieren — lege zuerst in den Struktur-Sektionen etwas an.",
        ["Export_Intro"]    = "Erzeuge Quellcode-Gerüste aus deinen Strukturen in 10 Sprachen. Funktions-/Methodenrümpfe stammen aus den jeweiligen Struktogrammen, wo du sie gezeichnet hast.",

        // App-Menü
        ["Menu_About"]      = "ℹ Info  (v{0})",
        ["Menu_AboutTitle"] = "Über StructoFox",
        ["Menu_Language"]   = "🌐 Sprache",
        ["Menu_Options"]    = "⚙ Optionen",
        ["Opt_NormWarn"]    = "Meldung wenn nicht normgerecht",
        ["Opt_NormMark"]    = "Markierung wenn nicht normgerecht (N̶)",
        ["Opt_SubRemove"]   = "Hinweis: Unterprogramm entfernen",
        ["Opt_BoardRemove"] = "Hinweis: vom Board entfernen",
        ["Norm_Title"]      = "Nicht normgerecht",
        ["Norm_DiagonalWarn"] = "Diagonale Mitte-zu-Mitte-Pfeile sind nicht DIN-66001-konform. Für ein normgerechtes Diagramm orthogonale Flusslinien verwenden. (Diese Meldung lässt sich in den Optionen abschalten.)",

        // Projekt-Kontextmenü (Home)
        ["Proj_Rename"]       = "✎ Umbenennen…",
        ["Proj_RenamePrompt"] = "Projektname:",
        ["Proj_OpenFolder"]   = "📂 Ordner öffnen",
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
