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

    /// <summary>All sketches, newest first. Self-heals: diagram files present on disk but missing from the index
    /// (e.g. PAPs/structograms/boards copied in from another machine) are picked up and added.</summary>
    public static List<Sketch> Load()
    {
        try
        {
            var list = File.Exists(IndexPath)
                ? JsonSerializer.Deserialize<List<Sketch>>(File.ReadAllText(IndexPath), ReadOpts) ?? []
                : [];
            // Drop any duplicate-id entries a past bug may have written (keep the first), then pick up new files.
            int before = list.Count;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            list = list.Where(s => seen.Add(s.Id)).ToList();
            bool changed = list.Count != before;
            changed |= Reconcile(list);
            if (changed) SaveAll(list);
            return list.OrderByDescending(s => s.UpdatedAt).ToList();
        }
        catch { return []; }
    }

    /// <summary>Adds an index entry for every diagram file on disk that has none — so copied-in diagrams appear on
    /// the home. One entry per id (an id with both a flow and a struct file lists as the PAP). Returns true if any
    /// were added. The name is taken from the diagram's own title where available.</summary>
    static bool Reconcile(List<Sketch> list)
    {
        var known = list.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dir = CodeEntityService.StructureFolder(Root);
        bool added = false;

        void Add(string id, SketchType type, Func<string> title)
        {
            if (id.Length == 0 || !known.Add(id)) return;   // already indexed, or claimed earlier this pass
            string n = ""; try { n = title(); } catch { }
            list.Add(new Sketch { Id = id, Type = type, Name = string.IsNullOrWhiteSpace(n) ? DefaultName(type) : n.Trim() });
            added = true;
        }
        static string IdFrom(string file, string prefix)
        {
            var b = System.IO.Path.GetFileNameWithoutExtension(file);
            return b.StartsWith(prefix, StringComparison.Ordinal) ? b[prefix.Length..] : "";
        }

        try
        {
            var flowDir = System.IO.Path.Combine(dir, "flow");
            if (Directory.Exists(flowDir))
                foreach (var f in Directory.EnumerateFiles(flowDir, "_flow_*.json"))
                { var id = IdFrom(f, "_flow_"); Add(id, SketchType.Pap, () => FlowChartService.Load(Root, id).Title); }

            var structDir = System.IO.Path.Combine(dir, "struct");
            if (Directory.Exists(structDir))
                foreach (var f in Directory.EnumerateFiles(structDir, "_struct_*.json"))
                { var id = IdFrom(f, "_struct_"); Add(id, SketchType.Structogram, () => StructogramService.Load(Root, id).Title); }

            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir, "_board_*.json"))
                    Add(IdFrom(f, "_board_"), SketchType.Board, () => "");
        }
        catch { }
        return added;
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

    /// <summary>Creates and records a new sketch, returning it. Its Id is a readable, unique key derived from the
    /// name (the Id doubles as the diagram filename, so files read like the sketch and copy cleanly between machines).</summary>
    public static Sketch Create(SketchType type, string name)
    {
        var list = Load();
        var display = string.IsNullOrWhiteSpace(name) ? DefaultName(type) : name.Trim();
        var s = new Sketch { Type = type, Name = display, Id = NameKeys.From(display, list.Select(x => x.Id)) };
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
        // Keep the readable filename in step with the name: derive a new unique key and move the backing diagram file.
        var newId = NameKeys.From(s.Name, list.Where(x => x != s).Select(x => x.Id));
        if (newId != s.Id)
        {
            FlowChartService.Rename(Root, s.Id, newId);
            StructogramService.Rename(Root, s.Id, newId);
            CodeBoardDataService.Rename(Root, s.Id, newId);
            s.Id = newId;
        }
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
