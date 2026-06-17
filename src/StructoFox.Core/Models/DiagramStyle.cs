namespace StructoFox.Core.Models;

/// <summary>
/// The user-controlled look of a diagram *surface* (structogram / flowchart) — deliberately
/// independent of the application's OXSUIT theme. A diagram is a document, so its appearance must
/// be stable and export-safe: the SAME style drives the on-screen renderer and any printed/exported
/// output (HTML5/SVG, PDF, TIFF). Colours are plain web hex (#RRGGBB) so the platform-neutral Core
/// stays free of any UI-framework brush types.
/// </summary>
public class DiagramStyle
{
    /// <summary>Canvas background colour behind the diagram.</summary>
    public string BackgroundColor { get; set; } = "#FFFFFF";

    /// <summary>Colour of all structural lines / borders / connectors.</summary>
    public string LineColor { get; set; } = "#1C1C1C";

    /// <summary>Thickness of structural lines, in px.</summary>
    public double LineThickness { get; set; } = 1.0;

    /// <summary>Colour of block / node text.</summary>
    public string TextColor { get; set; } = "#1C1C1C";

    /// <summary>Font family for diagram text.</summary>
    public string FontFamily { get; set; } = "Segoe UI";

    /// <summary>Base font size for diagram text, in px.</summary>
    public double FontSize { get; set; } = 12;

    // Hands back a fresh copy of the default "drawing-board" look: white paper, dark thin lines.
    public static DiagramStyle Default() => new();
}
