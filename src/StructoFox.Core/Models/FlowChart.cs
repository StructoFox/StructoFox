
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
    Junction,     // merge/collector point (Sammelpunkt) — a small filled dot several lines join at
    MultiDecision, // diamond — a multi-way branch (switch/case): many labelled outgoing tines, drawn as a comb
    Annotation    // comment/Bemerkung (DIN 66001) — an open bracket linked to an element by a dashed line; documentary only
}

/// <summary>Which comb(s) a <see cref="FlowNodeKind.MultiDecision"/> hangs its labelled tines on.</summary>
public enum CombDirection
{
    Bottom,   // a horizontal spine below the diamond; tines drop down
    Right,    // a vertical spine right of the diamond; tines run right
    Both      // both combs; each tine joins the one its target faces
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

    /// <summary>Which page of the flowchart this node lives on (0 = first page). Pages are a display grouping
    /// within the SAME file; off-page connectors pair across pages by their label (like on-page connectors),
    /// so the converter/codegen still see one continuous graph.</summary>
    public int          Page   { get; set; } = 0;

    /// <summary>Off-page connectors: the auto-created "entry" on the target page (mirrors the exit's label,
    /// read-only, undeletable, movable). The user-made "exit" has this false.</summary>
    public bool         OffPageEntry { get; set; } = false;

    /// <summary>Links an off-page exit to its entry (same value on both), so a label change on the exit can
    /// update the entry and a double-click can jump to the paired page.</summary>
    public string       OffPagePair  { get; set; } = "";

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

    /// <summary>For a <see cref="FlowNodeKind.Annotation"/> (Bemerkung): mirror the bracket and its connection
    /// point to the RIGHT side (default false = bracket spine + leader on the left).</summary>
    public bool Mirrored { get; set; }

    // ── Multi-Verzweigung (switch/case) only — ignored by every other kind ──
    /// <summary>For a <see cref="FlowNodeKind.MultiDecision"/>: which comb(s) its tines hang on.</summary>
    public CombDirection CombDir { get; set; } = CombDirection.Bottom;
    /// <summary>For a <see cref="FlowNodeKind.MultiDecision"/>: grid steps between adjacent tines.
    /// 0 = automatic (a target symbol's width+1 grid for a bottom comb, height+1 for a right comb), so
    /// neighbouring case bodies don't collide.</summary>
    public int           TineSpacing { get; set; } = 0;

    /// <summary>For a <see cref="FlowNodeKind.MultiDecision"/>: grid steps from the diamond edge to the
    /// comb spine — drag the spine handle to push the comb nearer/further from the condition.</summary>
    public int           CombGap { get; set; } = 1;

    /// <summary>For a <see cref="FlowNodeKind.MultiDecision"/>: grid steps the comb is shifted ALONG its
    /// spine (left/right for a bottom comb, up/down for a right comb) — so the tines can sit asymmetrically
    /// to one side of the stem for a more compact plan. Drag the spine bar sideways to set it.</summary>
    public int           CombShift { get; set; } = 0;

    /// <summary>For a Both-mode L comb: grid steps the whole tine group / bar is shifted left/right,
    /// independently of the stem. Drag the L's bottom bar sideways to set it.</summary>
    public int           CombBarShift { get; set; } = 0;

    /// <summary>For a Multi-Verzweigung comb: grid steps the STEM meets the bar away from the diamond
    /// vertex's straight-down/across point — drag the stem along the bar to set it (a Z-stem). The meeting
    /// stays fixed relative to the diamond, so it rides along when the bar shifts.</summary>
    public int           CombStemPos { get; set; } = 0;

    /// <summary>For a Multi-Verzweigung comb: which diamond vertex the stem leaves from — -1 = auto (the
    /// comb's natural side), else 0=Top, 1=Bottom, 2=Left, 3=Right. Drag the stem's diamond end to set it.</summary>
    public int           CombStemVertex { get; set; } = -1;

    /// <summary>For a Multi-Verzweigung comb: hand-routed bends of the stem (vertex → …waypoints… → bar). The
    /// vertex end and these bends are fixed; only the final straight into the bar flexes when the bar moves.</summary>
    public List<BoardWaypoint> CombStemWaypoints { get; set; } = [];
}

/// <summary>A directed arrow between two flow nodes.</summary>
public class FlowConnection
{
    public string              Id        { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string              FromId    { get; set; } = "";
    public string              ToId      { get; set; } = "";
    /// <summary>If set, this connection's TARGET end isn't a node but a "tap" onto another connection
    /// (the id) — a T-piece on a line. The meeting point is the projection of the anchor (ToTapX/Y) onto
    /// that line, so when the target moves sideways the tap keeps its other coordinate (the stub just
    /// grows/shrinks) instead of being dragged along.</summary>
    public string              ToTapConn { get; set; } = "";
    public double              ToTapX    { get; set; } = 0;
    public double              ToTapY    { get; set; } = 0;
    /// <summary>Optional label, e.g. "yes" / "no" on a decision branch.</summary>
    public string              Label     { get; set; } = "";
    /// <summary>Where the label sits along the line, as a fraction (0..1) of the polyline length. A
    /// negative value means "auto": at the first segment for a decision branch, else the longest segment.</summary>
    public double              LabelPos  { get; set; } = -1;
    /// <summary>Signed perpendicular offset of the label from the line (px): +below/right, -above/left.
    /// 0 = the default side (above a horizontal run, right of a vertical one).</summary>
    public double              LabelOff  { get; set; } = 0;
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

    /// <summary>For a Multi-Verzweigung comb tine: grid steps this tooth's slot is nudged ALONG the bar from
    /// its evenly-spaced default position — drag the tooth to set it (the bar grows/shrinks to follow).</summary>
    public int                 TineOffset { get; set; } = 0;

    /// <summary>For a wired comb tooth: a user-chosen approach anchor near the target. The entry is the
    /// projection of this onto the target's nearest edge, so the arrow can sit on any side (always
    /// perpendicular, from outside). When <see cref="TineTargetSet"/> is false the entry is automatic.</summary>
    public bool                TineTargetSet { get; set; } = false;
    public double              TineTargetX   { get; set; } = 0;
    public double              TineTargetY   { get; set; } = 0;
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
