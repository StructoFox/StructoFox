namespace StructoFox.Core.Models;

// ─────────────────────────────────────────────────────────────────────────────
// Shared board canvas primitives.
// Extracted from ClaudetRelay's WorldEntityService so the code-board / flowchart
// models stand on their own with no dependency on the host application.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Visual style of a relation line on the board.</summary>
public enum BoardLineStyle
{
    Solid, Dotted, Dashed, DotDash,
    DoubleSolid, DoubleDotted, DoubleDashed, DoubleDotDash
}

/// <summary>A movable waypoint on a relation line (makes the line bend around cards).</summary>
public class BoardWaypoint
{
    public string  Id         { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public double  X          { get; set; }
    public double  Y          { get; set; }
    /// <summary>
    /// When set, this waypoint's position is always read from the master waypoint with this ID
    /// (which lives on another relation).  Used so junction-connected lines move together.
    /// </summary>
    public string? LinkedToId { get; set; } = null;
}

/// <summary>A freely-placeable styled text annotation on the board canvas.</summary>
public class BoardTextBox
{
    public string Id          { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public double X           { get; set; }
    public double Y           { get; set; }
    public double Width       { get; set; } = 200;
    public double Height      { get; set; } = 80;
    public string Text        { get; set; } = "Text";
    public string FontFamily  { get; set; } = "Segoe UI";
    public double FontSize    { get; set; } = 12;
    public bool   Bold        { get; set; } = false;
    public bool   Italic      { get; set; } = false;
    public string TextColor   { get; set; } = "#212121";
    public string BgColor     { get; set; } = "#00000000";
    public string FrameColor  { get; set; } = "#FF808080";
    public double FrameThick  { get; set; } = 0;
    public string FrameStyle  { get; set; } = "None"; // None, Solid, Dashed, Dotted
    public string HAlign      { get; set; } = "Left";  // Left, Center, Right, Justify
    public string VAlign      { get; set; } = "Top";   // Top, Center, Bottom
}

/// <summary>A colored grouping frame that groups and moves items on the board canvas.</summary>
public class BoardFrame
{
    public string Id     { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public double X      { get; set; }
    public double Y      { get; set; }
    public double Width  { get; set; } = 300;
    public double Height { get; set; } = 200;
    public string Color  { get; set; } = "#FF4488AA";
    public string Label  { get; set; } = "";
}
