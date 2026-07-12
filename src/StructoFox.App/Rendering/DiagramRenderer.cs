using Avalonia.Controls;
using Avalonia.Media.Imaging;
using StructoFox.Core;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// Renders a project's PAPs / structograms / boards to bitmaps by reusing the EXACT editor rendering (so the
/// output is 100% identical to the on-screen diagram), sized to the diagram's content. Used by the print composer
/// for display (re-rendered on resize/update) and export (rendered at the target DPI scale).
/// </summary>
public static class DiagramRenderer
{
    /// <summary>A diagram in the project that can be placed on a page: its kind, its persistence key, and a
    /// human name.</summary>
    public readonly record struct Entry(DiagramKind Kind, string Key, string Name);

    /// <summary>Renders JUST the diagram (no decoration), transparent + tightly cropped, at <paramref name="scale"/>
    /// (1.0 = 96 DPI). Null if unsupported/empty. Must be called on the UI thread.</summary>
    public static RenderTargetBitmap? Render(string projFolder, DiagramKind kind, string key, double scale)
    {
        try
        {
            return kind switch
            {
                DiagramKind.Flowchart when FlowChartService.Exists(projFolder, key)
                    => new FlowChartWindow(projFolder, key, "", null).RenderDiagramOnly(0, scale),
                DiagramKind.Structogram when StructogramService.Exists(projFolder, key)
                    => new StructogramWindow(projFolder, key, "", null).RenderStructogramOnly(scale),
                // Board renderer is added next (same reuse pattern).
                _ => null,
            };
        }
        catch { return null; }
    }

    /// <summary>A diagram body as a LIVE control (crisp text at any scale), for kinds whose rendering is a pure
    /// control tree — currently structograms. Flowcharts stay bitmap-based (Canvas + custom draw). Null otherwise.</summary>
    public static Control? BuildControl(string projFolder, DiagramKind kind, string key)
    {
        try
        {
            if (kind == DiagramKind.Structogram && StructogramService.Exists(projFolder, key))
                return new StructogramWindow(projFolder, key, "", null).BuildStructogramBody();
            return null;
        }
        catch { return null; }
    }

    /// <summary>The decoration positions (title block / info / legend) present on the diagram, so each can be placed
    /// as its own movable item. Loads the diagram data directly — no editor window needed.</summary>
    public static List<DecorPos> DecorPositions(string projFolder, DiagramKind kind, string key)
    {
        try
        {
            if (kind == DiagramKind.Flowchart && FlowChartService.Exists(projFolder, key))
            {
                var d = FlowChartService.Load(projFolder, key);
                return DiagramDecor.EnumeratePositions(d.Title, d.Style);
            }
            if (kind == DiagramKind.Structogram && StructogramService.Exists(projFolder, key))
            {
                var d = StructogramService.Load(projFolder, key);
                return DiagramDecor.EnumeratePositions(d.Title, d.Style);
            }
            return new();
        }
        catch { return new(); }
    }

    /// <summary>The decoration block at <paramref name="pos"/> as a LIVE control (the editor's own decoration
    /// visual). Placed as a control — not a bitmap — so its text stays crisp at any zoom/scale. Null if empty.</summary>
    public static Control? DecorControl(string projFolder, DiagramKind kind, string key, DecorPos pos)
    {
        try
        {
            if (kind == DiagramKind.Flowchart && FlowChartService.Exists(projFolder, key))
            {
                var d = FlowChartService.Load(projFolder, key);
                return DiagramDecor.EnumeratePieces(d.Title, d.Style).FirstOrDefault(p => p.Pos == pos).Ctrl;
            }
            if (kind == DiagramKind.Structogram && StructogramService.Exists(projFolder, key))
            {
                var d = StructogramService.Load(projFolder, key);
                return DiagramDecor.EnumeratePieces(d.Title, d.Style).FirstOrDefault(p => p.Pos == pos).Ctrl;
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>Every diagram in the project that can be placed — currently the flowcharts of functions and
    /// methods. (Structograms/boards are added alongside their renderers.)</summary>
    public static List<Entry> ListAvailable(string projFolder)
    {
        var list = new List<Entry>();

        // Free functions: key = entity id. Both a flowchart AND a structogram may exist for the same function.
        foreach (var fn in CodeEntityService.LoadAll(projFolder, "Function"))
        {
            if (FlowChartService.Exists(projFolder, fn.Id))
                list.Add(new Entry(DiagramKind.Flowchart, fn.Id, fn.Name));
            if (StructogramService.Exists(projFolder, fn.Id))
                list.Add(new Entry(DiagramKind.Structogram, fn.Id, fn.Name));
        }

        // Methods of classes/structs/interfaces: key = "{entityId}#{methodId}".
        foreach (var type in new[] { "Class", "Struct", "Interface" })
            foreach (var e in CodeEntityService.LoadAll(projFolder, type))
                foreach (var m in e.Methods)
                {
                    var key = $"{e.Id}#{m.Id}";
                    if (FlowChartService.Exists(projFolder, key))
                        list.Add(new Entry(DiagramKind.Flowchart, key, $"{e.Name}.{m.Name}"));
                    if (StructogramService.Exists(projFolder, key))
                        list.Add(new Entry(DiagramKind.Structogram, key, $"{e.Name}.{m.Name}"));
                }

        // Sketchbook sketches (standalone diagrams keyed by their sketch id, not code entities).
        try
        {
            if (string.Equals(System.IO.Path.GetFullPath(projFolder),
                              System.IO.Path.GetFullPath(SketchbookService.Root), StringComparison.OrdinalIgnoreCase))
                // A sketch may hold BOTH a flowchart and a structogram under the same id (converted between them),
                // so list whichever files exist regardless of the sketch's original Type.
                foreach (var sk in SketchbookService.Load())
                {
                    if (FlowChartService.Exists(projFolder, sk.Id))
                        list.Add(new Entry(DiagramKind.Flowchart, sk.Id, sk.Name));
                    if (StructogramService.Exists(projFolder, sk.Id))
                        list.Add(new Entry(DiagramKind.Structogram, sk.Id, sk.Name));
                }
        }
        catch { }

        return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
