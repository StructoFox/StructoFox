
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
    Comment       // free note
}

public class FlowNode
{
    public string       Id     { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public FlowNodeKind Kind   { get; set; } = FlowNodeKind.Process;
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
}

public class FlowChartData
{
    /// <summary>Display title (e.g. the function/method name this flow sketches).</summary>
    public string               Title       { get; set; } = "";
    public List<FlowNode>       Nodes       { get; set; } = [];
    public List<FlowConnection> Connections { get; set; } = [];
    public double               GridSize    { get; set; } = 10;
    public bool                 SnapToGrid  { get; set; } = false;

    /// <summary>The diagram's surface appearance, persisted with it (user-controlled, theme-independent).</summary>
    public DiagramStyle         Style       { get; set; } = new();
}
