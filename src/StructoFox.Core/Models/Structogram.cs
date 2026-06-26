namespace StructoFox.Core.Models;

/// <summary>Nassi-Shneiderman block kinds (DIN 66261).</summary>
public enum NsBlockKind
{
    Statement,  // simple action box
    If,         // binary branch (then / else)
    While,      // pre-test loop (kopfgesteuert)
    DoWhile,    // post-test loop (fußgesteuert)
    Case,       // multi-way selection
    Subroutine  // call to a sub-program drawn elsewhere (double-bar box, links to its own diagram)
}

/// <summary>One arm of a Case block (a label + its body sequence).</summary>
public class NsArm
{
    public string        Label { get; set; } = "case";
    public List<NsBlock> Body  { get; set; } = [];
}

/// <summary>
/// A structogram block. Container kinds nest further sequences:
///   While / DoWhile → <see cref="Body"/>
///   If             → <see cref="Body"/> (then) + <see cref="Else"/>
///   Case           → <see cref="Arms"/>
/// </summary>
public class NsBlock
{
    public string        Id    { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public NsBlockKind   Kind  { get; set; } = NsBlockKind.Statement;
    /// <summary>Statement text, loop/if condition, or case expression.</summary>
    public string        Text  { get; set; } = "";
    /// <summary>An attached comment (carried over from a PAP "Bemerkung" that was linked to this block's
    /// source element). Shown as a small annotation on the block; empty = none.</summary>
    public string        Note  { get; set; } = "";
    /// <summary>
    /// True when this block marks a region the flowchart→structogram converter
    /// could not structure. Rendered with a distinct warning style, not deleted.
    /// </summary>
    public bool          Flagged { get; set; } = false;
    public List<NsBlock> Body  { get; set; } = [];
    public List<NsBlock> Else  { get; set; } = [];
    public List<NsArm>   Arms  { get; set; } = [];

    /// <summary>For an If block: the labels of the then / else branches (e.g. "ja" / "nein"), shown in the
    /// two header corners. Empty falls back to the default true/false captions.</summary>
    public string        TrueLabel  { get; set; } = "";
    public string        FalseLabel { get; set; } = "";

    /// <summary>For a Subroutine block: the id of the Function entity it calls (lives in the Functions
    /// library; its diagram is keyed by this id). Empty until linked/created via "show chart".</summary>
    public string        RefId { get; set; } = "";

    /// <summary>Legacy ad-hoc sub-diagram key (pre-RefId). Kept for older files; no longer written.</summary>
    public string        LinkKey { get; set; } = "";

    /// <summary>Optional cosmetic appearance overrides for this block (null = inherit the diagram style).
    /// Purely presentational — ignored by code generation.</summary>
    public ElementStyle? Style { get; set; }
}

public class StructogramData
{
    public string        Title { get; set; } = "";
    public List<NsBlock> Root  { get; set; } = [];

    /// <summary>The diagram's surface appearance, persisted with it (user-controlled, theme-independent).</summary>
    public DiagramStyle  Style { get; set; } = new();
}
