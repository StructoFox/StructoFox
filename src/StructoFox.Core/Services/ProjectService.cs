using System.IO;
using System.Text.Json;
using StructoFox.Core.Models;

namespace StructoFox.Core;

/// <summary>
/// Defines what a StructoFox project is on disk: a folder containing a <c>project.structofox</c>
/// marker file. Handles detecting, loading, saving, creating projects, and scanning a library folder
/// for the projects inside it.
/// </summary>
public static class ProjectService
{
    public const string Marker = "project.structofox";

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    static string MarkerPath(string folder) => Path.Combine(folder, Marker);

    /// <summary>True if the folder holds a project marker.</summary>
    public static bool IsProject(string folder) => File.Exists(MarkerPath(folder));

    /// <summary>Loads a project's metadata, or null if it's not a project / unreadable.</summary>
    public static ProjectInfo? Load(string folder)
    {
        try { return File.Exists(MarkerPath(folder)) ? JsonSerializer.Deserialize<ProjectInfo>(File.ReadAllText(MarkerPath(folder)), Opts) : null; }
        catch { return null; }
    }

    /// <summary>Writes a project's marker file (creating the folder if needed).</summary>
    public static void Save(string folder, ProjectInfo info)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(MarkerPath(folder), JsonSerializer.Serialize(info, Opts));
    }

    /// <summary>Makes <paramref name="folder"/> a project (creating it + the marker), returning its info.
    /// If a marker already exists it is loaded instead of overwritten.</summary>
    public static ProjectInfo Create(string folder, string name)
    {
        if (Load(folder) is { } existing) return existing;
        var info = new ProjectInfo { Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(folder.TrimEnd('/', '\\')) : name.Trim() };
        Save(folder, info);
        return info;
    }

    /// <summary>The display name for a project folder: its marker name, else the folder name.</summary>
    public static string DisplayName(string folder) =>
        Load(folder)?.Name ?? Path.GetFileName(folder.TrimEnd('/', '\\'));

    /// <summary>Finds projects in a library root: the root itself if it's a project, plus its
    /// immediate sub-folders that are projects. Empty if the root is gone.</summary>
    public static List<string> Scan(string libraryRoot)
    {
        var result = new List<string>();
        if (!Directory.Exists(libraryRoot)) return result;
        if (IsProject(libraryRoot)) result.Add(libraryRoot);
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(libraryRoot))
                if (IsProject(sub)) result.Add(sub);
        }
        catch { /* ignore unreadable roots */ }
        return result;
    }
}
