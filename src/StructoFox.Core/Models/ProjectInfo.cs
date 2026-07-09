namespace StructoFox.Core.Models;

/// <summary>
/// Metadata stored in a project's marker file (<c>project.structofox</c>). Its presence is what
/// makes a folder a StructoFox project; the contents let the app remember per-project preferences.
/// </summary>
public class ProjectInfo
{
    public string   Name             { get; set; } = "Project";
    public DateTime Created          { get; set; } = DateTime.UtcNow;
    public string   Description      { get; set; } = "";

    /// <summary>Preferred OXSUIT theme name to apply when this project opens (null = leave as-is).</summary>
    public string?  PreferredTheme   { get; set; }

    /// <summary>Preferred colour-palette name for this project (null = leave as-is).</summary>
    public string?  PreferredPalette { get; set; }

    /// <summary>Last language this project generated code in (an <c>ExportLanguage</c> name), so the generate
    /// dialog can preselect it next time. Null = none yet.</summary>
    public string?  LastExportLanguage { get; set; }

    /// <summary>Target platform hint for native code generation (Portable / Windows / Linux / macOS), used to
    /// steer the AI's API choices for C/C++. Null = Portable.</summary>
    public string?  TargetPlatform { get; set; }

    /// <summary>Emit one file per type (multi-file project) instead of a single source file, for the languages
    /// that support it (C#, C, C++). Default false.</summary>
    public bool     MultiFile { get; set; }

    /// <summary>The project's authoring language (an <c>ExportLanguage</c> name), used to render node-editor
    /// autocomplete suggestions (method signatures) in that syntax. Null = default (C#).</summary>
    public string?  Language { get; set; }
}
