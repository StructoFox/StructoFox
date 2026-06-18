using System.Text.Json;

namespace StructoFox.App;

/// <summary>
/// The registered "library" folders — parent directories that the project browser scans for
/// projects. Persisted as JSON in the per-user app folder.
/// </summary>
public static class Libraries
{
    static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StructoFox");
    static string StorePath => Path.Combine(Dir, "libraries.json");

    /// <summary>The registered library roots (in add order); empty if none/unreadable.</summary>
    public static List<string> Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(StorePath)) ?? new();
        }
        catch { /* fall through */ }
        return new();
    }

    /// <summary>Registers a library root (de-duplicated).</summary>
    public static void Add(string path)
    {
        var list = Load();
        if (!list.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) list.Add(path);
        Persist(list);
    }

    /// <summary>Removes a library root.</summary>
    public static void Remove(string path)
    {
        var list = Load();
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Persist(list);
    }

    static void Persist(List<string> list)
    {
        try { Directory.CreateDirectory(Dir); File.WriteAllText(StorePath, JsonSerializer.Serialize(list)); }
        catch { /* best-effort */ }
    }
}
