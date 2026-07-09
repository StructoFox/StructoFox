using System.IO;
using System.IO.Compression;

namespace StructoFox.Core;

/// <summary>
/// Zips a whole project folder into a per-project subfolder of a backup root, keeping only the newest N zips.
/// A backup is only made when the project actually changed since the last one (newest file newer than the
/// last zip). Pure/best-effort — the app reads the user's backup preferences and calls in.
/// </summary>
public static class BackupService
{
    /// <summary>The per-project subfolder under the backup root (named after the project folder).</summary>
    public static string ProjectBackupDir(string backupRoot, string projectFolder) =>
        Path.Combine(backupRoot, Sanitize(new DirectoryInfo(projectFolder.TrimEnd('/', '\\')).Name));

    /// <summary>True when the project changed since its last backup (or has none yet). False if the backup root
    /// sits inside the project (which would recurse) or the project is gone.</summary>
    public static bool NeedsBackup(string projectFolder, string backupRoot)
    {
        if (!Directory.Exists(projectFolder) || string.IsNullOrWhiteSpace(backupRoot)) return false;
        if (IsInside(backupRoot, projectFolder)) return false;

        var latest = LatestZip(ProjectBackupDir(backupRoot, projectFolder));
        if (latest is null) return true;                       // never backed up → do it
        return NewestFileTime(projectFolder) > latest.LastWriteTimeUtc;
    }

    /// <summary>Zips the whole project folder to <c>&lt;root&gt;/&lt;project&gt;/&lt;project&gt;_&lt;timestamp&gt;.zip</c>,
    /// then prunes so at most <paramref name="keep"/> zips remain for that project. Returns the zip path, or null
    /// on failure / when the backup root sits inside the project.</summary>
    public static string? CreateBackup(string projectFolder, string backupRoot, int keep)
    {
        if (!Directory.Exists(projectFolder) || string.IsNullOrWhiteSpace(backupRoot)) return null;
        if (IsInside(backupRoot, projectFolder)) return null;

        var name = Sanitize(new DirectoryInfo(projectFolder.TrimEnd('/', '\\')).Name);
        var dir  = ProjectBackupDir(backupRoot, projectFolder);
        Directory.CreateDirectory(dir);

        var zipPath = Path.Combine(dir, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        for (int n = 1; File.Exists(zipPath); n++)   // same-second collision → suffix
            zipPath = Path.Combine(dir, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}_{n}.zip");

        try { ZipFile.CreateFromDirectory(projectFolder, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true); }
        catch { return null; }

        Prune(dir, Math.Max(1, keep));
        return zipPath;
    }

    // Delete all but the newest `keep` zips in a project's backup folder.
    static void Prune(string dir, int keep)
    {
        foreach (var old in new DirectoryInfo(dir).GetFiles("*.zip").OrderByDescending(f => f.LastWriteTimeUtc).Skip(keep))
            try { old.Delete(); } catch { /* locked → leave it */ }
    }

    static FileInfo? LatestZip(string dir) => Directory.Exists(dir)
        ? new DirectoryInfo(dir).GetFiles("*.zip").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
        : null;

    static DateTime NewestFileTime(string folder)
    {
        var newest = DateTime.MinValue;
        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            try { var t = File.GetLastWriteTimeUtc(f); if (t > newest) newest = t; } catch { }
        return newest;
    }

    /// <summary>True when the backup root can't be used because it sits inside the project being backed up
    /// (zipping would recurse into the growing archive). The caller can surface this as an error.</summary>
    public static bool RootConflicts(string backupRoot, string projectFolder) =>
        !string.IsNullOrWhiteSpace(backupRoot) && IsInside(backupRoot, projectFolder);

    // True if `inner` is the same as, or nested under, `outer` — so we never zip the backup into itself.
    static bool IsInside(string inner, string outer)
    {
        try
        {
            var a = Path.GetFullPath(inner).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
            var b = Path.GetFullPath(outer).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
            return a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static string Sanitize(string s) =>
        new string((s ?? "").Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' or '.' ? c : '_').ToArray()).Trim();
}
