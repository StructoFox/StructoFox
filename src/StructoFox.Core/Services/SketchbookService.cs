using System.Text.Json;

namespace StructoFox.Core;

/// <summary>The kind of standalone sketch — mirrors the three diagram editors.</summary>
public enum SketchType { Pap, Structogram, Board }

/// <summary>One standalone diagram that belongs to no project. Its <see cref="Id"/> doubles as the
/// diagram key (flow/struct) or the board id, so the existing editors open it unchanged.</summary>
public class Sketch
{
    public string     Id        { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string     Name      { get; set; } = "Sketch";
    public SketchType Type      { get; set; } = SketchType.Pap;
    public DateTime   CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime   UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The "sketchbook": standalone diagrams that live outside any project, under
/// Documents/StructoFox/Sketchbook. The folder doubles as a lightweight project folder, so the diagram
/// services (flow/struct/board) store a sketch's data there with no special casing. An index file lists
/// the sketches for the start screen. Quick doodles you can find again, without ceremony.
/// </summary>
public static class SketchbookService
{
    /// <summary>The sketchbook folder (created on demand): Documents/StructoFox/Sketchbook.</summary>
    public static string Root
    {
        get
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return System.IO.Path.Combine(docs, "StructoFox", "Sketchbook");
        }
    }

    static string IndexPath => System.IO.Path.Combine(Root, "sketches.json");

    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };
    static readonly JsonSerializerOptions ReadOpts  = new() { PropertyNameCaseInsensitive = true };

    /// <summary>All sketches, newest first.</summary>
    public static List<Sketch> Load()
    {
        try
        {
            if (!File.Exists(IndexPath)) return [];
            var list = JsonSerializer.Deserialize<List<Sketch>>(File.ReadAllText(IndexPath), ReadOpts) ?? [];
            return list.OrderByDescending(s => s.UpdatedAt).ToList();
        }
        catch { return []; }
    }

    static void SaveAll(List<Sketch> list)
    {
        try
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(IndexPath, JsonSerializer.Serialize(list, WriteOpts));
        }
        catch { }
    }

    /// <summary>Creates and records a new sketch, returning it.</summary>
    public static Sketch Create(SketchType type, string name)
    {
        var s = new Sketch { Type = type, Name = string.IsNullOrWhiteSpace(name) ? DefaultName(type) : name.Trim() };
        var list = Load();
        list.Add(s);
        SaveAll(list);
        return s;
    }

    /// <summary>Marks a sketch as just used (so it floats to the top of the list).</summary>
    public static void Touch(string id)
    {
        var list = Load();
        var s = list.FirstOrDefault(x => x.Id == id);
        if (s is null) return;
        s.UpdatedAt = DateTime.UtcNow;
        SaveAll(list);
    }

    public static void Rename(string id, string name)
    {
        var list = Load();
        var s = list.FirstOrDefault(x => x.Id == id);
        if (s is null || string.IsNullOrWhiteSpace(name)) return;
        s.Name = name.Trim();
        SaveAll(list);
    }

    /// <summary>Removes a sketch from the index and deletes its diagram file(s).</summary>
    public static void Delete(string id)
    {
        var list = Load();
        list.RemoveAll(x => x.Id == id);
        SaveAll(list);
        // Best-effort cleanup of the backing diagram data.
        var dir = CodeEntityService.StructureFolder(Root);
        foreach (var p in new[]
        {
            System.IO.Path.Combine(dir, "flow",   $"_flow_{id}.json"),
            System.IO.Path.Combine(dir, "struct", $"_struct_{id}.json"),
            System.IO.Path.Combine(dir, $"_board_{id}.json"),
        })
            try { if (File.Exists(p)) File.Delete(p); } catch { }
    }

    static string DefaultName(SketchType t) => t switch
    {
        SketchType.Pap         => "Flowchart",
        SketchType.Structogram => "Structogram",
        _                      => "Board",
    };
}
