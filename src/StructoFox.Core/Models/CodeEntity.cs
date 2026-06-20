using System.Text.Json.Serialization;

namespace StructoFox.Core.Models;

public enum PortDirection { Input, Output }

public enum PassingConvention { Direct, Reference, Pointer }

/// <summary>Horizontal = inputs on left, outputs on right. Vertical = inputs on top, outputs on bottom.</summary>
public enum PortOrientation { Horizontal, Vertical }

public enum CodeVisibility { Public, Private, Protected, Internal }

public class CodePort
{
    public string           Id         { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string           Name       { get; set; } = "param";
    public string           DataType   { get; set; } = "void";
    public PortDirection    Direction  { get; set; } = PortDirection.Input;
    public PassingConvention Convention { get; set; } = PassingConvention.Direct;
}

/// <summary>A class/struct field (member variable).</summary>
public class CodeField
{
    public string         Name       { get; set; } = "field";
    public string         DataType   { get; set; } = "int";
    public CodeVisibility Visibility { get; set; } = CodeVisibility.Private;
    public bool           IsStatic   { get; set; } = false;
    public string         DefaultValue { get; set; } = "";
}

/// <summary>A single parameter of a method.</summary>
public class CodeParam
{
    public string            Name       { get; set; } = "arg";
    public string            DataType   { get; set; } = "int";
    public PassingConvention Convention { get; set; } = PassingConvention.Direct;
}

public enum MethodKind { Normal, Constructor, Destructor }

/// <summary>A class/struct/interface method (member function).</summary>
public class CodeMethod
{
    public string          Id         { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public MethodKind      Kind       { get; set; } = MethodKind.Normal;
    public string          Name       { get; set; } = "Method";
    public string          ReturnType { get; set; } = "void";
    public CodeVisibility  Visibility { get; set; } = CodeVisibility.Public;
    public bool            IsStatic   { get; set; } = false;
    public List<CodeParam> Parameters { get; set; } = [];
}

public enum CodeEntityType { Namespace, Class, Struct, Interface, Enum, Function, Object }

public class CodeEntity
{
    public string         Id          { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string         Name        { get; set; } = "Entity";
    public CodeEntityType EntityType  { get; set; } = CodeEntityType.Class;
    public string         Description { get; set; } = "";

    /// <summary>Flow connectors for the Ablaufplan (mainly used by Function entities).</summary>
    public List<CodePort> Ports       { get; set; } = [];

    // ── Structural members (Class / Struct / Interface) ──────────────────
    public List<CodeField>  Fields    { get; set; } = [];
    public List<CodeMethod> Methods   { get; set; } = [];

    /// <summary>Entity ID of the base class this Class/Struct inherits from (empty = none).</summary>
    public string         BaseClassId   { get; set; } = "";
    /// <summary>Entity IDs of interfaces this Class/Struct implements.</summary>
    public List<string>   ImplementsIds { get; set; } = [];

    /// <summary>Enum member names (only for Enum entities).</summary>
    public List<string>   EnumValues    { get; set; } = [];

    /// <summary>For an Object entity: ID of the Class it is an instance of.</summary>
    public string         InstanceOfId  { get; set; } = "";

    /// <summary>Optional containing namespace name.</summary>
    public string         Namespace     { get; set; } = "";

    /// <summary>True for the project's single entry-point function (main). Surfaced in its own cockpit
    /// tab so it doesn't drown among the other functions, and wrapped per language on export.</summary>
    public bool           IsEntryPoint  { get; set; } = false;
}

public class CodeBoard
{
    public string   Id        { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string   Name      { get; set; } = "Board";
    public string   Symbol    { get; set; } = "💻";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>If set, this board authors a function/method body: the diagram key it generates into
    /// (entityId, or "entityId#methodId" for a method). Empty = a plain visualisation board.</summary>
    public string   TargetKey { get; set; } = "";
}

/// <summary>Per-card-per-board position and orientation (orientation is board-local).</summary>
public class CodeCardPosition
{
    public double          X               { get; set; } = 60;
    public double          Y               { get; set; } = 60;
    public double          CardWidth       { get; set; } = 0;
    public double          CardHeight      { get; set; } = 0;
    public int             ZOrder          { get; set; } = 0;
    public PortOrientation PortOrientation { get; set; } = PortOrientation.Horizontal;
}

/// <summary>A port-to-port connection on a code board.</summary>
public class CodeRelation
{
    public string              Id         { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string              FromId     { get; set; } = "";
    public string              FromPortId { get; set; } = "";
    public string              ToId       { get; set; } = "";
    public string              ToPortId   { get; set; } = "";
    public string              Caption    { get; set; } = "";
    public BoardLineStyle      LineStyle  { get; set; } = BoardLineStyle.Solid;
    public string              LineColor  { get; set; } = "#2196F3";
    public double              Thickness  { get; set; } = 1.5;
    public bool                HasArrow   { get; set; } = true;
    public List<BoardWaypoint> Waypoints  { get; set; } = [];
}

public class CodeBoardData
{
    public Dictionary<string, CodeCardPosition> Positions  { get; set; } = new();
    public List<CodeRelation>                   Relations  { get; set; } = [];
    public List<BoardTextBox>                   TextBoxes  { get; set; } = [];
    public List<BoardFrame>                     Frames     { get; set; } = [];
    public double                               GridSize   { get; set; } = 10;
    public bool                                 SnapToGrid { get; set; } = false;
    public bool                                 GridVisible { get; set; } = false;
    public string                               GridColor   { get; set; } = "#B0BEC5";
    public double                               GridOpacity { get; set; } = 0.35;
    public GridLineStyle                        GridStyle   { get; set; } = GridLineStyle.Lines;
}
