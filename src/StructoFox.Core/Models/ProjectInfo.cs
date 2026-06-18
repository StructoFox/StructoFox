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
}
