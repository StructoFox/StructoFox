using System.IO;
using System.Text.Json;
using StructoFox.Core.Models;

namespace StructoFox.Core;

/// <summary>
/// Saves / loads / lists <see cref="PrintDocument"/>s under a project's <c>print/</c> folder (one JSON file per
/// document, named by the document's NAME — so the files are human-readable, portable and easy to delete/copy).
/// UI-free — mirrors the flow/struct/board persistence services.
/// </summary>
public static class PrintDocumentService
{
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOpts  = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The project's print-document folder (<c>&lt;project&gt;/print</c>).</summary>
    public static string Folder(string projFolder) => Path.Combine(projFolder, "print");

    private static string FilePath(string projFolder, string name) => Path.Combine(Folder(projFolder), Sanitize(name) + ".json");

    /// <summary>Turns a document name into a safe file stem (invalid characters → '_').</summary>
    public static string Sanitize(string s)
    {
        var clean = new string((s ?? "").Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '_').ToArray()).Trim();
        return clean.Length == 0 ? "Print" : clean;
    }

    /// <summary>Every print document in the project, newest-modified first.</summary>
    public static List<PrintDocument> List(string projFolder)
    {
        var dir = Folder(projFolder);
        var result = new List<PrintDocument>();
        if (!Directory.Exists(dir)) return result;
        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<PrintDocument>(File.ReadAllText(f), ReadOpts) is { } doc)
                    result.Add(doc);
            }
            catch { /* skip a broken file */ }
        }
        return result
            .OrderByDescending(d => { try { return File.GetLastWriteTimeUtc(FilePath(projFolder, d.Name)); } catch { return DateTime.MinValue; } })
            .ToList();
    }

    public static PrintDocument? Load(string projFolder, string name)
    {
        var path = FilePath(projFolder, name);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<PrintDocument>(File.ReadAllText(path), ReadOpts); }
        catch { return null; }
    }

    /// <summary>Writes the document as <c>&lt;name&gt;.json</c>. If <paramref name="previousName"/> is given and
    /// its file differs from the new name, that stale file is removed (a rename moves, not duplicates).</summary>
    public static void Save(string projFolder, PrintDocument doc, string? previousName = null)
    {
        try
        {
            Directory.CreateDirectory(Folder(projFolder));
            var target = FilePath(projFolder, doc.Name);
            if (previousName is not null)
            {
                var prev = FilePath(projFolder, previousName);
                if (!string.Equals(prev, target, StringComparison.OrdinalIgnoreCase) && File.Exists(prev))
                    try { File.Delete(prev); } catch { }
            }
            File.WriteAllText(target, JsonSerializer.Serialize(doc, WriteOpts));
        }
        catch { }
    }

    public static void Delete(string projFolder, string name)
    {
        try { var p = FilePath(projFolder, name); if (File.Exists(p)) File.Delete(p); }
        catch { }
    }
}
