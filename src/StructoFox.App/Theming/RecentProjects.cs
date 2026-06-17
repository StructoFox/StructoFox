using System.Text.Json;

namespace StructoFox.App;

/// <summary>
/// Remembers the most-recently-opened project folders (most-recent first, capped at ten), persisted
/// as JSON in the per-user app folder. Backs the project browser on the home screen.
/// </summary>
public static class RecentProjects
{
    const int Max = 10;

    static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StructoFox");
    static string StorePath => Path.Combine(Dir, "recent.json");

    /// <summary>The recent project paths, newest first; empty if none/unreadable.</summary>
    public static List<string> Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(StorePath)) ?? new();
        }
        catch { /* fall through to empty */ }
        return new();
    }

    /// <summary>Records a freshly-opened project at the top, de-duplicated and capped at ten.</summary>
    public static void Add(string path)
    {
        var list = Load();
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > Max) list = list.GetRange(0, Max);
        try { Directory.CreateDirectory(Dir); File.WriteAllText(StorePath, JsonSerializer.Serialize(list)); }
        catch { /* best-effort */ }
    }
}
