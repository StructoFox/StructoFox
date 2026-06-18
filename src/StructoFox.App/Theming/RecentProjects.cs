using System.Text.Json;

namespace StructoFox.App;

/// <summary>One remembered project: its folder path and when it was last opened.</summary>
public class RecentEntry
{
    public string   Path   { get; set; } = "";
    public DateTime Opened { get; set; }
}

/// <summary>
/// Remembers recently-opened projects (newest first, capped at ten) with their last-opened time,
/// persisted as JSON in the per-user app folder. Backs the home screen's Recent list + date sort.
/// </summary>
public static class RecentProjects
{
    const int Max = 10;

    static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StructoFox");
    static string StorePath => Path.Combine(Dir, "recent.json");

    /// <summary>The recent entries, newest first; empty if none/unreadable (incl. old formats).</summary>
    public static List<RecentEntry> Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<List<RecentEntry>>(File.ReadAllText(StorePath)) ?? new();
        }
        catch { /* old/!valid format → start fresh */ }
        return new();
    }

    /// <summary>Records a freshly-opened project at the top with the current time, capped at ten.</summary>
    public static void Add(string path)
    {
        var list = Load();
        list.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, new RecentEntry { Path = path, Opened = DateTime.Now });
        if (list.Count > Max) list = list.GetRange(0, Max);
        try { Directory.CreateDirectory(Dir); File.WriteAllText(StorePath, JsonSerializer.Serialize(list)); }
        catch { /* best-effort */ }
    }
}
