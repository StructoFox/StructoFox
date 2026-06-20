
namespace StructoFox.Core.Models;

/// <summary>Classic flowchart node shapes.</summary>
public enum FlowNodeKind
{
    Start,        // rounded oval — entry
    End,          // rounded oval — exit
    Process,      // rectangle — an action / statement
    Decision,     // diamond — a branch (yes/no)
    InputOutput,  // parallelogram — read/print/IO
    Subroutine,   // double-bordered rectangle — calls another function
    Comment,      // free note
    Connector,    // on-page connector — a small labelled circle that continues the flow elsewhere
    Junction      // merge/collector point (Sammelpunkt) — a small filled dot several lines join at
}

/// <summary>Cosmetic DIN 66001 symbol variants for an I/O node. Purely a drawing choice — the node's
/// semantic <see cref="FlowNodeKind"/> stays InputOutput, so code generation is unaffected (a punched
/// card is still just "input/output" to the generated code).</summary>
public enum FlowSymbol
{
    Auto,          // use the node kind's default shape
    Document,      // printout / report (wavy bottom)
    Display,       // screen output
    ManualInput,   // keyboard / manual entry (slanted top)
    PunchedCard,   // punched card (clipped corner)
    MagneticTape,  // tape reel (circle + foot)
    MagneticDisk,  // disk / database (cylinder)
    StoredData,    // stored data (curved sides)
    OffPageConnector // off-page connector (home-plate pentagon) — continues the flow on another page
}

public class FlowNode
{
    public string       Id     { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public FlowNodeKind Kind   { get; set; } = FlowNodeKind.Process;

    /// <summary>Optional DIN symbol variant (cosmetic; default = the kind's standard shape).</summary>
    public FlowSymbol   Symbol { get; set; } = FlowSymbol.Auto;

    /// <summary>For a Subroutine node: the id of the Function entity it calls (in the Functions library;
    /// its diagram is keyed by this id). Empty until linked/created via "show chart".</summary>
    public string       RefId  { get; set; } = "";
    public string       Text   { get; set; } = "";
    public double       X      { get; set; } = 60;
    public double       Y      { get; set; } = 60;
    public double       Width  { get; set; } = 140;
    public double       Height { get; set; } = 56;

    // ── Optional text formatting (null/false = node default). Multiline text is allowed. ──
    public string? FontFamily    { get; set; }
    public double? FontSize      { get; set; }
    public bool    Bold          { get; set; }
    public bool    Italic        { get; set; }
    public bool    Underline     { get; set; }
    public bool    Strikethrough { get; set; }

    /// <summary>Optional per-node colour overrides (line/fill/text). Null = keep the kind's standard
    /// colours, which stay the default for every newly placed node. Purely presentational.</summary>
    public ElementStyle? Style { get; set; }
}

/// <summary>A directed arrow between two flow nodes.</summary>
public class FlowConnection
{
    public string              Id        { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string              FromId    { get; set; } = "";
    public string              ToId      { get; set; } = "";
    /// <summary>Optional label, e.g. "yes" / "no" on a decision branch.</summary>
    public string              Label     { get; set; } = "";
    public BoardLineStyle      LineStyle { get; set; } = BoardLineStyle.Solid;
    public string              LineColor { get; set; } = "#888888";
    public double              Thickness { get; set; } = 1.6;
    public List<BoardWaypoint> Waypoints { get; set; } = [];

    /// <summary>DIN data-transmission path (communication link): drawn with a zig-zag marker to set it
    /// apart from an ordinary control-flow line.</summary>
    public bool                Transmission { get; set; } = false;
}

public class FlowChartData
{
    /// <summary>Display title (e.g. the function/method name this flow sketches).</summary>
    public string               Title       { get; set; } = "";
    public List<FlowNode>       Nodes       { get; set; } = [];
    public List<FlowConnection> Connections { get; set; } = [];
    public double               GridSize    { get; set; } = 10;
    public bool                 SnapToGrid  { get; set; } = false;

    /// <summary>Connector style: false = DIN-style orthogonal flow lines (default), true = direct
    /// diagonal centre-to-centre arrows (the non-normative convenience option).</summary>
    public bool                 DiagonalLines { get; set; } = false;

    /// <summary>The diagram's surface appearance, persisted with it (user-controlled, theme-independent).</summary>
    public DiagramStyle         Style       { get; set; } = new();
}
