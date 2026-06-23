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

    /// <summary>Every function/method a board can be assigned to author: standalone functions and the
    /// methods of classes/structs/interfaces. Key is the diagram key, Label a readable name.</summary>
    public static List<(string Key, string Label)> AssignableTargets(string projFolder)
    {
        var list = new List<(string, string)>();
        foreach (var e in CodeEntityService.LoadAll(projFolder, "Function"))
            list.Add((e.Id, e.Name));
        foreach (var typeName in new[] { "Class", "Struct", "Interface" })
            foreach (var e in CodeEntityService.LoadAll(projFolder, typeName))
                foreach (var m in e.Methods)
                    list.Add(($"{e.Id}#{m.Id}", $"{e.Name}.{(string.IsNullOrWhiteSpace(m.Name) ? "(method)" : m.Name)}"));
        return list;
    }

    /// <summary>A readable label for a target key, or the raw key if it can't be resolved.</summary>
    public static string TargetLabel(string projFolder, string key) =>
        string.IsNullOrEmpty(key) ? "" : (AssignableTargets(projFolder).FirstOrDefault(t => t.Key == key).Label ?? key);

    /// <summary>Boards assigned to an entity or any of its methods (TargetKey == id or "id#…").</summary>
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
