
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
    OffPageConnector, // off-page connector (home-plate pentagon) — continues the flow on another page

    // ── Program-flowchart symbols (DIN 66001 / ISO 5807 core) ──
    Preparation,   // hexagon — setup / loop or switch initialization
    Delay,         // D-shape — a wait / timeout
    ManualOperation, // trapezoid (wider top) — a step done by hand
    LoopLimit,     // rectangle with cut top corners — loop start/limit
    Parallel,      // two horizontal bars — parallel-mode (fork/join)

    // ── Extended ISO data/system symbols + modern (gated by the "extended ISO" option) ──
    Sort,          // diamond split by a line — order data
    Extract,       // upward triangle — pull a subset out
    Merge,         // downward triangle — combine streams
    Collate,       // two triangles (hourglass) — merge + extract
    CloudStorage   // cloud — cloud-based storage/service (modern, not classic ISO 5807)
}

/// <summary>Whether a <see cref="FlowSymbol"/> is part of the classic DIN/ISO program-flowchart core, or
/// an extended ISO data/system (or modern) symbol that the per-chart "extended ISO" option can switch off.</summary>
public static class FlowSymbols
{
    public static bool IsExtended(FlowSymbol s) =>
        s is FlowSymbol.Sort or FlowSymbol.Extract or FlowSymbol.Merge or FlowSymbol.Collate or FlowSymbol.CloudStorage;
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
    /// <summary>Where the label sits along the line, as a fraction (0..1) of the polyline length. A
    /// negative value means "auto": at the first segment for a decision branch, else the longest segment.</summary>
    public double              LabelPos  { get; set; } = -1;
    public BoardLineStyle      LineStyle { get; set; } = BoardLineStyle.Solid;
    public string              LineColor { get; set; } = "#888888";
    public double              Thickness { get; set; } = 1.6;
    public List<BoardWaypoint> Waypoints { get; set; } = [];

    /// <summary>DIN data-transmission path (communication link): drawn with a zig-zag marker to set it
    /// apart from an ordinary control-flow line.</summary>
    public bool                Transmission { get; set; } = false;

    /// <summary>Arrowhead override: null = automatic (an arrow, except into a junction); true = force an
    /// arrowhead (e.g. a line pointing onto another line); false = plain line, no arrowhead.</summary>
    public bool?               Arrow { get; set; } = null;
}

public class FlowChartData
{
    /// <summary>Display title (e.g. the function/method name this flow sketches).</summary>
    public string               Title       { get; set; } = "";
    public List<FlowNode>       Nodes       { get; set; } = [];
    public List<FlowConnection> Connections { get; set; } = [];
    public double               GridSize    { get; set; } = 10;
    public bool                 SnapToGrid  { get; set; } = true;   // most users keep snapping on

    /// <summary>Connector style: false = DIN-style orthogonal flow lines (default), true = direct
    /// diagonal centre-to-centre arrows (the non-normative convenience option).</summary>
    public bool                 DiagonalLines { get; set; } = false;

    /// <summary>Allow extended ISO data/system (+ modern) symbols. On by default; off restricts the menus to
    /// the DIN/ISO program-flowchart core and flags any already-placed extended symbol as non-conforming.</summary>
    public bool                 ExtendedIso  { get; set; } = true;

    /// <summary>The diagram's surface appearance, persisted with it (user-controlled, theme-independent).</summary>
    public DiagramStyle         Style       { get; set; } = new();
}
