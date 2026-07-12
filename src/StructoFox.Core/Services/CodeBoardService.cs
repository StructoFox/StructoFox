using System.IO;
using System.Text.Json;
using StructoFox.Core.Models;

namespace StructoFox.Core;

/// <summary>
/// Saves/loads CodeEntity JSON files under structure/{EntityType}/.
/// </summary>
public static class CodeEntityService
{
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOpts  = new() { PropertyNameCaseInsensitive = true };

    // The plan's structure folder (entities + diagrams + boards). Named "structure", not "code": it holds
    // the design/model, while generated code is an export-time output that lives elsewhere.
    public static string StructureFolder(string projFolder) =>
        Path.Combine(projFolder, "structure");

    private static string EntityFolder(string projFolder, string entityType) =>
        Path.Combine(StructureFolder(projFolder), entityType);

    private static string EntityFilePath(string projFolder, string entityType, string entityId) =>
        Path.Combine(EntityFolder(projFolder, entityType), entityId + ".json");

    public static List<CodeEntity> LoadAll(string projFolder, string entityType)
    {
        var dir = EntityFolder(projFolder, entityType);
        if (!Directory.Exists(dir)) return [];
        var result = new List<CodeEntity>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
        {
            // Skip service files (_board_/_flow_/_struct_/_boards): on a case-insensitive filesystem the
            // "Struct" entity folder and the "struct" structogram folder are the same directory, so the
            // structogram files would otherwise be loaded as blank "Class Entity" phantoms.
            if (Path.GetFileName(f).StartsWith('_')) continue;
            try
            {
                var e = JsonSerializer.Deserialize<CodeEntity>(File.ReadAllText(f), ReadOpts);
                if (e is not null) result.Add(e);
            }
            catch { }
        }
        return result;
    }

    public static void Save(string projFolder, string entityType, CodeEntity entity)
    {
        try
        {
            Directory.CreateDirectory(EntityFolder(projFolder, entityType));
            File.WriteAllText(EntityFilePath(projFolder, entityType, entity.Id),
                JsonSerializer.Serialize(entity, WriteOpts));
        }
        catch { }
    }

    public static void Delete(string projFolder, string entityType, string entityId)
    {
        try { File.Delete(EntityFilePath(projFolder, entityType, entityId)); } catch { }
    }

    /// <summary>Last-write time of the entity's JSON file (UTC), or MinValue if missing.</summary>
    public static DateTime FileTime(string projFolder, string entityType, string entityId)
    {
        try
        {
            var p = EntityFilePath(projFolder, entityType, entityId);
            return File.Exists(p) ? File.GetLastWriteTimeUtc(p) : DateTime.MinValue;
        }
        catch { return DateTime.MinValue; }
    }

    public static readonly string[] EntityTypes =
        ["Namespace", "Class", "Struct", "Interface", "Enum", "Function", "Object"];
}

/// <summary>
/// Saves/loads CodeBoardData under structure/.
/// </summary>
public static class CodeBoardDataService
{
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOpts  = new() { PropertyNameCaseInsensitive = true };

    private static string BoardFilePath(string projFolder, string boardId) =>
        Path.Combine(CodeEntityService.StructureFolder(projFolder), $"_board_{boardId}.json");

    public static CodeBoardData Load(string projFolder, string boardId)
    {
        var path = BoardFilePath(projFolder, boardId);
        if (!File.Exists(path)) return new CodeBoardData();
        try { return JsonSerializer.Deserialize<CodeBoardData>(File.ReadAllText(path), ReadOpts) ?? new CodeBoardData(); }
        catch { return new CodeBoardData(); }
    }

    public static void Save(string projFolder, string boardId, CodeBoardData data)
    {
        try
        {
            Directory.CreateDirectory(CodeEntityService.StructureFolder(projFolder));
            File.WriteAllText(BoardFilePath(projFolder, boardId),
                JsonSerializer.Serialize(data, WriteOpts));
        }
        catch { }
    }

    /// <summary>Moves a board's data file when the board is renamed to a new readable id.</summary>
    public static void Rename(string projFolder, string oldId, string newId)
    {
        if (oldId == newId) return;
        var from = BoardFilePath(projFolder, oldId);
        if (!File.Exists(from)) return;
        try
        {
            var to = BoardFilePath(projFolder, newId);
            if (File.Exists(to)) File.Delete(to);
            File.Move(from, to);
        }
        catch { }
    }

    /// <summary>Ids of every board-data file on disk — used to sweep boards when cascading an entity-key rename into
    /// their card positions and relations.</summary>
    public static List<string> AllBoardIds(string projFolder)
    {
        var dir = CodeEntityService.StructureFolder(projFolder);
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "_board_*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f)["_board_".Length..]).ToList();
    }
}

/// <summary>
/// Saves/loads FlowChartData under structure/flow/.
/// Key is the entity ID for a standalone function, or "{entityId}#{methodId}" for a method.
/// </summary>
public static class FlowChartService
{
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOpts  = new() { PropertyNameCaseInsensitive = true };

    private static string SafeKey(string key) =>
        key.Replace('#', '_').Replace(':', '_');

    private static string FlowFilePath(string projFolder, string key) =>
        Path.Combine(CodeEntityService.StructureFolder(projFolder), "flow", $"_flow_{SafeKey(key)}.json");

    public static bool Exists(string projFolder, string key) =>
        File.Exists(FlowFilePath(projFolder, key));

    public static Models.FlowChartData Load(string projFolder, string key)
    {
        var path = FlowFilePath(projFolder, key);
        if (!File.Exists(path)) return new Models.FlowChartData();
        try { return JsonSerializer.Deserialize<Models.FlowChartData>(File.ReadAllText(path), ReadOpts) ?? new Models.FlowChartData(); }
        catch { return new Models.FlowChartData(); }
    }

    public static void Save(string projFolder, string key, Models.FlowChartData data)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(CodeEntityService.StructureFolder(projFolder), "flow"));
            File.WriteAllText(FlowFilePath(projFolder, key),
                JsonSerializer.Serialize(data, WriteOpts));
        }
        catch { }
    }

    /// <summary>Moves the diagram file from one key to another (readable-key rename). No-op if there's nothing to move.</summary>
    public static void Rename(string projFolder, string oldKey, string newKey)
    {
        if (oldKey == newKey) return;
        var from = FlowFilePath(projFolder, oldKey);
        if (!File.Exists(from)) return;
        try
        {
            var to = FlowFilePath(projFolder, newKey);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (File.Exists(to)) File.Delete(to);
            File.Move(from, to);
        }
        catch { }
    }
}

/// <summary>
/// Saves/loads StructogramData under structure/struct/.
/// Same key scheme as flowcharts: entityId or "{entityId}#{methodId}".
/// </summary>
public static class StructogramService
{
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOpts  = new() { PropertyNameCaseInsensitive = true };

    private static string SafeKey(string key) => key.Replace('#', '_').Replace(':', '_');

    private static string FilePath(string projFolder, string key) =>
        Path.Combine(CodeEntityService.StructureFolder(projFolder), "struct", $"_struct_{SafeKey(key)}.json");

    public static bool Exists(string projFolder, string key) => File.Exists(FilePath(projFolder, key));

    public static Models.StructogramData Load(string projFolder, string key)
    {
        var path = FilePath(projFolder, key);
        if (!File.Exists(path)) return new Models.StructogramData();
        try { return JsonSerializer.Deserialize<Models.StructogramData>(File.ReadAllText(path), ReadOpts) ?? new Models.StructogramData(); }
        catch { return new Models.StructogramData(); }
    }

    public static void Save(string projFolder, string key, Models.StructogramData data)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(CodeEntityService.StructureFolder(projFolder), "struct"));
            File.WriteAllText(FilePath(projFolder, key), JsonSerializer.Serialize(data, WriteOpts));
        }
        catch { }
    }

    /// <summary>Moves the structogram file from one key to another (readable-key rename).</summary>
    public static void Rename(string projFolder, string oldKey, string newKey)
    {
        if (oldKey == newKey) return;
        var from = FilePath(projFolder, oldKey);
        if (!File.Exists(from)) return;
        try
        {
            var to = FilePath(projFolder, newKey);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (File.Exists(to)) File.Delete(to);
            File.Move(from, to);
        }
        catch { }
    }

    /// <summary>Filename-derived keys of every structogram on disk (the '#'-collapsed safe form; Load/Save round-trip
    /// with these unchanged). Used to sweep all structograms when cascading a rename into their subroutine LinkKeys.</summary>
    public static List<string> AllFileKeys(string projFolder)
    {
        var dir = Path.Combine(CodeEntityService.StructureFolder(projFolder), "struct");
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "_struct_*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f)["_struct_".Length..]).ToList();
    }
}

/// <summary>
/// Registry of CodeBoards (list of boards) for a project.
/// File: structure/_boards.json
/// </summary>
public static class CodeBoardRegistryService
{
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOpts  = new() { PropertyNameCaseInsensitive = true };

    private static string RegistryPath(string projFolder) =>
        Path.Combine(CodeEntityService.StructureFolder(projFolder), "_boards.json");

    public static List<CodeBoard> Load(string projFolder)
    {
        var path = RegistryPath(projFolder);
        if (!File.Exists(path)) return [];
        try
        {
            var list = JsonSerializer.Deserialize<List<CodeBoard>>(File.ReadAllText(path), ReadOpts);
            return list ?? [];
        }
        catch { return []; }
    }

    public static void Save(string projFolder, List<CodeBoard> boards)
    {
        try
        {
            Directory.CreateDirectory(CodeEntityService.StructureFolder(projFolder));
            File.WriteAllText(RegistryPath(projFolder),
                JsonSerializer.Serialize(boards, WriteOpts));
        }
        catch { }
    }

    /// <summary>Boards still carrying a legacy assignment to an entity or any of its methods (TargetKey ==
    /// id or "id#…"). Kept only so the "this still has a board attached" delete-warning can surface old data.</summary>
    public static List<CodeBoard> BoardsAssignedTo(string projFolder, string entityId) =>
        Load(projFolder).Where(b => b.TargetKey == entityId || b.TargetKey.StartsWith(entityId + "#")).ToList();

    public static readonly string[] SymbolPalette =
    [
        // Programming / structure
        "💻", "🖥", "⌨", "🧩", "📦", "📚", "🗂", "🗃", "📂", "🏗",
        "🏛", "🧱", "🔷", "🔶", "🔺", "🔻", "⬡", "⬢", "◆", "◇",
        // Functions / flow / logic
        "⚡", "🔁", "🔀", "🔂", "↩", "↪", "⤴", "⤵", "➰", "➿",
        "🎯", "🧮", "🔢", "🔣", "{ }", "( )", "< >", "[ ]", "λ", "ƒ",
        // Connections / data
        "🔗", "🪢", "📡", "🌐", "🛰", "🔌", "🧬", "🗜", "💾", "🗄",
        // Tools / build
        "⚙", "🔧", "🔩", "🛠", "🪛", "🔨", "🧰", "📐", "📏", "✏",
        // Security / access
        "🔐", "🔑", "🔒", "🛡", "🪪", "🚪", "🚦", "🚧", "⛔", "✅",
        // Concepts
        "🧠", "🤖", "💡", "🔬", "🧪", "📊", "📈", "🗺", "🧭", "🎛",
        // Greek / math (type-theory flavour)
        "α", "β", "γ", "δ", "Σ", "Π", "Δ", "Ω", "∞", "∑",
    ];
}
