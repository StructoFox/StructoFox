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

    // ── Decoration (branding for the plan / its export) ──────────────────────

    /// <summary>Render the diagram's title as a heading on the plan.</summary>
    public bool ShowTitle { get; set; } = false;

    /// <summary>A faint diagonal watermark text (e.g. "DRAFT", a company name); empty = none.</summary>
    public string Watermark { get; set; } = "";

    /// <summary>Path to a logo image to place in a corner; empty/missing = none.</summary>
    public string LogoPath { get; set; } = "";

    /// <summary>Which corner the logo sits in.</summary>
    public DecorCorner LogoCorner { get; set; } = DecorCorner.TopRight;

    // Hands back a fresh copy of the default "drawing-board" look: white paper, dark thin lines.
    public static DiagramStyle Default() => new();
}

/// <summary>The corner a diagram logo is anchored to.</summary>
public enum DecorCorner { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>
/// Optional per-element appearance overrides (one block / node). Every field is nullable and
/// falls back to the diagram's <see cref="DiagramStyle"/> when unset, so a diagram with no overrides
/// is a clean, standard-compliant (DIN 66261 / ISO 5807) drawing. Emphasis here is redundant by
/// design — purely cosmetic, never bedeutungstragend — so a stripped diagram still reads correctly.
/// </summary>
public class ElementStyle
{
    /// <summary>Override for this element's line/border colour (web hex), or null to inherit.</summary>
    public string? LineColor { get; set; }

    /// <summary>Override for this element's line/border thickness in px, or null to inherit.</summary>
    public double? LineThickness { get; set; }

    /// <summary>Override for this element's text colour (web hex), or null to inherit.</summary>
    public string? TextColor { get; set; }

    /// <summary>Override for this element's background fill (web hex), or null for no fill.</summary>
    public string? FillColor { get; set; }
}
