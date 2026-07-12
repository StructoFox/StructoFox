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

    /// <summary>Where the title heading sits on the plan (a reserved band, except Center which overlays).</summary>
    public DecorPos TitlePosition { get; set; } = DecorPos.TopCenter;

    /// <summary>Title font size in px.</summary>
    public double TitleFontSize { get; set; } = 20;

    /// <summary>Title in bold.</summary>
    public bool TitleBold { get; set; } = true;

    /// <summary>Title colour (web hex); empty = inherit the diagram text colour.</summary>
    public string TitleColor { get; set; } = "";

    /// <summary>A faint diagonal watermark text (e.g. "DRAFT", a company name); empty = none.</summary>
    public string Watermark { get; set; } = "";

    /// <summary>Path to a faint centred watermark image; empty/missing = none. Works alongside or
    /// instead of the text watermark.</summary>
    public string WatermarkImage { get; set; } = "";

    /// <summary>Rotation of the watermark (text + image), in degrees.</summary>
    public double WatermarkAngle { get; set; } = -30;

    /// <summary>Path to a logo image to place on the plan; empty/missing = none.</summary>
    public string LogoPath { get; set; } = "";

    /// <summary>Where the logo sits on the plan (shares the 5-position system with title and info).</summary>
    public DecorPos LogoPosition { get; set; } = DecorPos.TopLeft;

    /// <summary>Width (px) of the logo cell in a merged title block; the logo is scaled to fit inside it. 0 = auto.</summary>
    public double LogoBoxWidth { get; set; } = 90;

    // ── Info field (an optional "title block" / Schriftfeld for presentation) ─────────────────────────────
    // Only non-empty rows are shown. Like the title, it sits in a reserved band (or overlays at Center).
    public bool   ShowInfo        { get; set; } = false;
    public DecorPos InfoPosition  { get; set; } = DecorPos.BottomCenter;
    public string InfoName        { get; set; } = "";   // name of the structogram / PAP / function
    public string InfoProject     { get; set; } = "";
    public string InfoProjectNo   { get; set; } = "";
    public string InfoVersion     { get; set; } = "";
    public string InfoDate        { get; set; } = "";
    public string InfoAuthor      { get; set; } = "";
    public string InfoDepartment  { get; set; } = "";   // class (school) or department (company)
    public string InfoExtra       { get; set; } = "";   // free multiline note (plain text)
    public string InfoPage        { get; set; } = "";   // rendered page number, e.g. "2 / 5" — filled by the composer
    public bool   ShowPageNumber  { get; set; } = false; // print composer auto-fills InfoPage with "current / total"

    // ── Grid (snapping aid) ──────────────────────────────────────────────────

    /// <summary>Show the alignment grid behind the diagram (off by default — snapping is on, the grid is
    /// just an optional visual aid).</summary>
    public bool GridVisible { get; set; } = false;

    /// <summary>Grid line colour (web hex).</summary>
    public string GridColor { get; set; } = "#B0BEC5";

    /// <summary>Grid line opacity, 0..1 (a faint grid is least distracting).</summary>
    public double GridOpacity { get; set; } = 0.35;

    /// <summary>How grid lines are drawn.</summary>
    public GridLineStyle GridStyle { get; set; } = GridLineStyle.Lines;

    // Hands back a fresh copy of the default "drawing-board" look: white paper, dark thin lines.
    public static DiagramStyle Default() => new();
}

/// <summary>How the alignment grid is rendered.</summary>
public enum GridLineStyle { Lines, Dashed, Dots }

/// <summary>Where a decoration (title / logo / info field) sits on the plan: a top or bottom band, aligned
/// left / centre / right. The chosen band reserves an empty strip so decorations never cover the drawing.
/// Several decorations sharing the exact same slot are laid out in order (logo, title, info).</summary>
public enum DecorPos { TopLeft, TopCenter, TopRight, BottomLeft, BottomCenter, BottomRight }

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
