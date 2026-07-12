using System.Text.Json.Serialization;

namespace StructoFox.Core.Models;

/// <summary>Page orientation for a print page.</summary>
public enum PrintOrientation { Portrait, Landscape }

/// <summary>Standard paper sizes (DIN A-series + common international), or <see cref="Custom"/> for an explicit
/// pixel size. Physical sizes are resolved via <see cref="PaperSizes"/>.</summary>
public enum PaperSize { Custom, A6, A5, A4, A3, A2, A1, A0, Letter, Legal, Tabloid, B6, B5, B4, B3, B2, B1, B0 }

/// <summary>Which editor a <see cref="DiagramItem"/> pulls from.</summary>
public enum DiagramKind { Flowchart, Structogram, Board }

/// <summary>Export target for a print document.</summary>
public enum PrintExportFormat { Pdf, Tiff, Png }

/// <summary>TIFF frame compression (PDF uses <see cref="ExportSettings.JpegQuality"/> / <see cref="ExportSettings.Lossless"/>).</summary>
public enum TiffCompression { Lzw, Deflate, Jpeg }

/// <summary>A print/export document: an ordered set of pages plus the last-used export settings. Stored as JSON
/// under a project's <c>print/</c> folder.</summary>
public class PrintDocument
{
    public string          Id    { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string          Name  { get; set; } = "Print";
    public List<PrintPage> Pages { get; set; } = new() { new PrintPage() };
    public ExportSettings  Export { get; set; } = new();

    /// <summary>Layout grid (its own, coarser than a diagram grid). Off by default. Spacing is stored in mm;
    /// <see cref="GridUnit"/> only picks how it's shown/edited (mm or inch).</summary>
    public bool          GridVisible   { get; set; } = false;
    public bool          GridSnap      { get; set; } = true;    // on by default (move + mouse-resize snap to grid)
    public double        GridMm        { get; set; } = 5;          // 5 mm default
    public string        GridUnit      { get; set; } = "mm";       // "mm" or "in"
    public string        GridColor     { get; set; } = "#000000";
    public double        GridOpacity   { get; set; } = 0.15;       // 0..1
    public double        GridThickness { get; set; } = 1;          // px
    public PrintGridStyle GridStyle    { get; set; } = PrintGridStyle.Solid;
}

/// <summary>How the print layout grid is drawn.</summary>
public enum PrintGridStyle
{
    Solid, Dotted, Dashed,
    /// <summary>Only a small cross at each intersection.</summary>
    Crosses,
    /// <summary>Crosses at intersections + dots spaced ALONG the grid lines between them.</summary>
    CrossesDots,
    /// <summary>Crosses at intersections + short dashes spaced along the grid lines between them.</summary>
    CrossesDashes,
}

/// <summary>One page: a paper format + a set of freely placed, independently scaled items.</summary>
public class PrintPage
{
    public PaperSize        Paper         { get; set; } = PaperSize.A4;
    public PrintOrientation Orientation   { get; set; } = PrintOrientation.Portrait;
    /// <summary>Used only when <see cref="Paper"/> is <see cref="PaperSize.Custom"/> (device-independent px @ 96 DPI).</summary>
    public double           CustomWidth   { get; set; } = 794;   // A4 @ 96 DPI
    public double           CustomHeight  { get; set; } = 1123;
    public string           Background    { get; set; } = "#FFFFFF";
    public List<PrintItem>  Items         { get; set; } = new();

    /// <summary>Page margins in millimetres. Content is clipped to the area INSIDE the margins on export; the
    /// margins show as a guide on the canvas. Defaults: top/bottom 1 cm, left 2 cm, right 1 cm.</summary>
    public double MarginTop    { get; set; } = 10;
    public double MarginBottom { get; set; } = 10;
    public double MarginLeft   { get; set; } = 20;
    public double MarginRight  { get; set; } = 10;

    /// <summary>The page size in device-independent pixels (@ 96 DPI), honouring orientation.</summary>
    [JsonIgnore]
    public (double W, double H) SizePx
    {
        get
        {
            var (w, h) = Paper == PaperSize.Custom ? (CustomWidth, CustomHeight) : PaperSizes.Px96(Paper);
            return Orientation == PrintOrientation.Landscape ? (h, w) : (w, h);
        }
    }

    /// <summary>The printable rectangle (inside the margins) in device-independent px @ 96 DPI: (X, Y, W, H).</summary>
    [JsonIgnore]
    public (double X, double Y, double W, double H) PrintablePx
    {
        get
        {
            const double f = 96.0 / 25.4;
            var (w, h) = SizePx;
            double x = MarginLeft * f, y = MarginTop * f;
            return (x, y, Math.Max(1, w - (MarginLeft + MarginRight) * f), Math.Max(1, h - (MarginTop + MarginBottom) * f));
        }
    }
}

/// <summary>Base for anything placed on a page: a position, an independent scale, and stacking order.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DiagramItem),      "diagram")]
[JsonDerivedType(typeof(DiagramDecorItem), "decor")]
[JsonDerivedType(typeof(HeaderItem),       "header")]
[JsonDerivedType(typeof(LabelItem),        "label")]
[JsonDerivedType(typeof(TextItem),         "text")]
[JsonDerivedType(typeof(LegendItem),       "legend")]
public abstract class PrintItem
{
    public string Id     { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public double X      { get; set; }
    public double Y      { get; set; }
    public double Scale  { get; set; } = 1.0;
    public int    ZOrder { get; set; }
}

/// <summary>A live reference to a PAP / structogram / board — re-rendered from current data on "Update", so it
/// stays 100% identical to the editor. <see cref="Key"/> is an entity id, "entityId#methodId", or a board id.</summary>
public sealed class DiagramItem : PrintItem
{
    public DiagramKind Kind { get; set; } = DiagramKind.Flowchart;
    public string      Key  { get; set; } = "";
    public string      Caption { get; set; } = "";   // optional title shown under the diagram
}

/// <summary>One decoration block of a diagram (its title block / info table / legend), placed as its own movable,
/// scalable, individually-deletable item at the position it holds on the source diagram. Re-rendered live from the
/// diagram's style, so it stays identical.</summary>
public sealed class DiagramDecorItem : PrintItem
{
    public DiagramKind Kind { get; set; } = DiagramKind.Flowchart;
    public string      Key  { get; set; } = "";
    public DecorPos    Pos  { get; set; } = DecorPos.TopLeft;
}

/// <summary>A print header / title block placed as its own movable, scalable item — built exactly like a diagram
/// header (title / info table / logo via the same <c>DiagramStyle</c> + saved header templates). Optionally shows
/// the page number inside its info block.</summary>
public sealed class HeaderItem : PrintItem
{
    public string       Title          { get; set; } = "Header";
    public DiagramStyle Style          { get; set; } = new() { ShowTitle = true, ShowInfo = true };
    /// <summary>Which decoration slot of the header this item shows (a header is inserted as one item per slot,
    /// at the slot's position, so it can be decomposed — like a PAP's decoration).</summary>
    public DecorPos     Pos            { get; set; } = DecorPos.TopLeft;
    public bool         ShowPageNumber { get; set; } = false;
    /// <summary>Max render width in device-independent px (0 = unconstrained). Long info fields wrap instead of
    /// overflowing the page. Set on insert to (page width − 3 cm).</summary>
    public double       MaxWidth       { get; set; } = 0;
}

/// <summary>A single-line caption with one uniform style.</summary>
public sealed class LabelItem : PrintItem
{
    public string  Text        { get; set; } = "Label";
    public string  FontFamily  { get; set; } = "";
    public double  FontSize    { get; set; } = 14;
    public string  Color       { get; set; } = "#000000";
    public bool    Bold        { get; set; }
    public bool    Italic      { get; set; }
    public bool    Underline   { get; set; }
    public bool    Strike      { get; set; }
    /// <summary>Fill behind the text; null/empty = transparent.</summary>
    public string? Background   { get; set; }
    /// <summary>Border colour; null/empty = no border (uses <see cref="BorderThickness"/>).</summary>
    public string? BorderColor  { get; set; }
    public double  BorderThickness { get; set; } = 0;
    /// <summary>Inner padding around the text (px).</summary>
    public double  Padding      { get; set; } = 2;
}

/// <summary>A multi-line free-text box. Font family + size are uniform per box; colour, marker (highlight) and
/// styles are mixable INLINE via <see cref="Runs"/>.</summary>
public sealed class TextItem : PrintItem
{
    public string        FontFamily { get; set; } = "";
    public double        FontSize   { get; set; } = 14;
    public string        Align      { get; set; } = "Left";   // Left / Center / Right
    public double        Width      { get; set; } = 240;       // wrap width (device-independent px)
    public TextListStyle List       { get; set; } = TextListStyle.None;
    public string        Color      { get; set; } = "#000000";
    public bool          Bold       { get; set; }
    public bool          Italic     { get; set; }
    public bool          Underline  { get; set; }
    public bool          Strike     { get; set; }
    public string?       Background      { get; set; }
    public string?       BorderColor     { get; set; }
    public double        BorderThickness { get; set; } = 0;
    public double        Padding         { get; set; } = 4;
    public List<TextRun> Runs       { get; set; } = new() { new TextRun { Text = "Text" } };

    /// <summary>The whole text as plain string (v1 edits box-level style; inline runs are preserved on load but
    /// collapsed to one run when edited here).</summary>
    [JsonIgnore]
    public string PlainText
    {
        get => string.Concat(Runs.Select(r => r.Text));
        set => Runs = new() { new TextRun { Text = value } };
    }
}

/// <summary>Optional list decoration a <see cref="TextItem"/> prefixes to each non-empty line.</summary>
public enum TextListStyle { None, Dash, Dot, AlphaLower, Number }

/// <summary>A styled span within a <see cref="TextItem"/>. Newlines in <see cref="Text"/> are honoured.</summary>
public sealed class TextRun
{
    public string  Text      { get; set; } = "";
    public string? Fg        { get; set; }   // null = default foreground
    public string? Marker    { get; set; }   // highlight/background; null = none
    public bool    Bold      { get; set; }
    public bool    Italic    { get; set; }
    public bool    Underline { get; set; }
    public bool    Strike    { get; set; }
}

/// <summary>A standalone legend / caption table (like a PAP's), movable and scalable on its own.</summary>
public sealed class LegendItem : PrintItem
{
    public string          Title { get; set; } = "Legend";
    public List<LegendRow> Rows  { get; set; } = new();
    public string          FontFamily { get; set; } = "";
    public double          FontSize   { get; set; } = 12;
    public string          Color      { get; set; } = "#000000";
    public string          BorderColor{ get; set; } = "#000000";
}

public sealed class LegendRow
{
    public string Cell1 { get; set; } = "";
    public string Cell2 { get; set; } = "";
}

/// <summary>Last-used export choices for the document.</summary>
public class ExportSettings
{
    public PrintExportFormat Format      { get; set; } = PrintExportFormat.Pdf;
    public int               Dpi         { get; set; } = 200;
    /// <summary>PDF: false = JPEG-compress page images at <see cref="JpegQuality"/>; true = lossless (Flate).</summary>
    public bool              Lossless    { get; set; } = false;
    public int               JpegQuality { get; set; } = 85;    // 1..100 (PDF JPEG, TIFF JPEG)
    public TiffCompression   Tiff        { get; set; } = TiffCompression.Lzw;
}

/// <summary>Physical paper sizes and the device-independent pixel sizes (@ 96 DPI) used for on-page layout.</summary>
public static class PaperSizes
{
    // Portrait width/height in millimetres.
    public static (double W, double H) Mm(PaperSize p) => p switch
    {
        PaperSize.A6      => (105, 148),
        PaperSize.A5      => (148, 210),
        PaperSize.A4      => (210, 297),
        PaperSize.A3      => (297, 420),
        PaperSize.A2      => (420, 594),
        PaperSize.A1      => (594, 841),
        PaperSize.A0      => (841, 1189),
        PaperSize.Letter  => (215.9, 279.4),
        PaperSize.Legal   => (215.9, 355.6),
        PaperSize.Tabloid => (279.4, 431.8),
        PaperSize.B6      => (125, 176),
        PaperSize.B5      => (176, 250),
        PaperSize.B4      => (250, 353),
        PaperSize.B3      => (353, 500),
        PaperSize.B2      => (500, 707),
        PaperSize.B1      => (707, 1000),
        PaperSize.B0      => (1000, 1414),
        _                 => (210, 297),   // fallback A4
    };

    /// <summary>Portrait size in device-independent pixels at 96 DPI (1 mm = 96/25.4 px).</summary>
    public static (double W, double H) Px96(PaperSize p)
    {
        var (w, h) = Mm(p);
        const double f = 96.0 / 25.4;
        return (Math.Round(w * f), Math.Round(h * f));
    }
}
