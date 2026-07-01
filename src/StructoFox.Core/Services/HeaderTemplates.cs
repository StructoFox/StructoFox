using System.IO;
using System.Text.Json;
using StructoFox.Core.Models;

namespace StructoFox.Core;

/// <summary>
/// The header (title / info field / watermark / logo) of a diagram, separated from the diagram's surface look
/// so it can be saved as a reusable template (a school's or company's letterhead) and applied to new diagrams.
/// Captures the header-related fields of a <see cref="DiagramStyle"/> only — never the colours/grid/lines.
/// </summary>
public sealed class HeaderData
{
    public bool     ShowTitle      { get; set; }
    public DecorPos TitlePosition  { get; set; } = DecorPos.TopCenter;
    public double   TitleFontSize  { get; set; } = 20;
    public bool     TitleBold      { get; set; } = true;
    public string   TitleColor     { get; set; } = "";

    public string   Watermark      { get; set; } = "";
    public string   WatermarkImage { get; set; } = "";
    public double   WatermarkAngle { get; set; } = -30;

    public string   LogoPath       { get; set; } = "";
    public DecorPos LogoPosition   { get; set; } = DecorPos.TopLeft;

    public bool     ShowInfo       { get; set; }
    public DecorPos InfoPosition   { get; set; } = DecorPos.BottomCenter;
    public string   InfoName       { get; set; } = "";
    public string   InfoProject    { get; set; } = "";
    public string   InfoProjectNo  { get; set; } = "";
    public string   InfoVersion    { get; set; } = "";
    public string   InfoDate       { get; set; } = "";
    public string   InfoAuthor     { get; set; } = "";
    public string   InfoDepartment { get; set; } = "";
    public string   InfoExtra      { get; set; } = "";

    /// <summary>Reads the header fields out of a diagram style.</summary>
    public static HeaderData Capture(DiagramStyle s) => new()
    {
        ShowTitle = s.ShowTitle, TitlePosition = s.TitlePosition, TitleFontSize = s.TitleFontSize,
        TitleBold = s.TitleBold, TitleColor = s.TitleColor,
        Watermark = s.Watermark, WatermarkImage = s.WatermarkImage, WatermarkAngle = s.WatermarkAngle,
        LogoPath = s.LogoPath, LogoPosition = s.LogoPosition,
        ShowInfo = s.ShowInfo, InfoPosition = s.InfoPosition, InfoName = s.InfoName, InfoProject = s.InfoProject,
        InfoProjectNo = s.InfoProjectNo, InfoVersion = s.InfoVersion, InfoDate = s.InfoDate,
        InfoAuthor = s.InfoAuthor, InfoDepartment = s.InfoDepartment, InfoExtra = s.InfoExtra,
    };

    /// <summary>Writes these header fields into a diagram style (leaving colours/grid untouched).</summary>
    public void ApplyTo(DiagramStyle s)
    {
        s.ShowTitle = ShowTitle; s.TitlePosition = TitlePosition; s.TitleFontSize = TitleFontSize;
        s.TitleBold = TitleBold; s.TitleColor = TitleColor;
        s.Watermark = Watermark; s.WatermarkImage = WatermarkImage; s.WatermarkAngle = WatermarkAngle;
        s.LogoPath = LogoPath; s.LogoPosition = LogoPosition;
        s.ShowInfo = ShowInfo; s.InfoPosition = InfoPosition; s.InfoName = InfoName; s.InfoProject = InfoProject;
        s.InfoProjectNo = InfoProjectNo; s.InfoVersion = InfoVersion; s.InfoDate = InfoDate;
        s.InfoAuthor = InfoAuthor; s.InfoDepartment = InfoDepartment; s.InfoExtra = InfoExtra;
    }
}

/// <summary>Stores reusable header templates as JSON, globally per user (%AppData%/StructoFox/HeaderTemplates),
/// plus the user's chosen default template for new PAPs and structograms.</summary>
public static class HeaderTemplateService
{
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StructoFox", "HeaderTemplates");

    static string FileOf(string name) => Path.Combine(Dir, Sanitize(name) + ".json");

    /// <summary>All template names (file stem), sorted.</summary>
    public static List<string> List()
    {
        try
        {
            if (!Directory.Exists(Dir)) return new();
            return Directory.EnumerateFiles(Dir, "*.json")
                .Select(Path.GetFileNameWithoutExtension).Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return new(); }
    }

    public static HeaderData? Load(string name)
    {
        try { var f = FileOf(name); return File.Exists(f) ? JsonSerializer.Deserialize<HeaderData>(File.ReadAllText(f)) : null; }
        catch { return null; }
    }

    public static void Save(string name, HeaderData data)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FileOf(name), JsonSerializer.Serialize(data, Opts));
    }

    public static void Delete(string name)
    {
        try { var f = FileOf(name); if (File.Exists(f)) File.Delete(f); } catch { }
    }

    static string Sanitize(string s)
    {
        var clean = new string((s ?? "").Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '_').ToArray()).Trim();
        return clean.Length == 0 ? "Template" : clean;
    }

    // ── Defaults applied to new diagrams ───────────────────────────────────
    sealed class Defaults { public string Pap { get; set; } = ""; public string Struct { get; set; } = ""; }

    static string DefaultsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StructoFox", "header-defaults.json");

    static Defaults LoadDefaults()
    {
        try { return File.Exists(DefaultsFile) ? JsonSerializer.Deserialize<Defaults>(File.ReadAllText(DefaultsFile)) ?? new() : new(); }
        catch { return new(); }
    }

    static void SaveDefaults(Defaults d)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DefaultsFile)!);
        File.WriteAllText(DefaultsFile, JsonSerializer.Serialize(d, Opts));
    }

    /// <summary>The default template name for new PAPs / structograms (empty = none).</summary>
    public static string DefaultForPap    { get => LoadDefaults().Pap;    set { var d = LoadDefaults(); d.Pap = value;    SaveDefaults(d); } }
    public static string DefaultForStruct { get => LoadDefaults().Struct; set { var d = LoadDefaults(); d.Struct = value; SaveDefaults(d); } }

    /// <summary>Applies the chosen default header to a brand-new diagram's style, if one is configured.</summary>
    public static void ApplyDefault(bool isPap, DiagramStyle target)
    {
        var name = isPap ? DefaultForPap : DefaultForStruct;
        if (!string.IsNullOrWhiteSpace(name) && Load(name) is { } h) h.ApplyTo(target);
    }
}
