using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace StructoFox.App;

/// <summary>
/// A faint honeycomb (flat-top hexagon) texture behind content — a more futuristic, "techy" backdrop
/// than a plain grid. Theme-agnostic low-alpha grey, kept subtle so it never fights the diagrams on top.
/// </summary>
public class HoneycombBackdrop : Control
{
    const double R = 20;   // hexagon radius (centre → vertex)
    static readonly Pen Line = new(new SolidColorBrush(Color.FromArgb(22, 128, 128, 128)), 1);

    public HoneycombBackdrop() => ClipToBounds = true;

    // Re-draw on resize so the comb always fills the area.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == BoundsProperty) InvalidateVisual();
    }

    // Tiles flat-top hexagons across the control; odd columns are offset by half a row.
    public override void Render(DrawingContext ctx)
    {
        double stepX = 1.5 * R, stepY = Math.Sqrt(3) * R;
        int col = 0;
        for (double cx = 0; cx <= Bounds.Width + R; cx += stepX, col++)
        {
            double offsetY = (col % 2 == 0) ? 0 : stepY / 2;
            for (double cy = offsetY; cy <= Bounds.Height + R; cy += stepY)
                ctx.DrawGeometry(null, Line, Hexagon(cx, cy));
        }
    }

    // Builds one flat-top hexagon outline centred at (cx, cy).
    static Geometry Hexagon(double cx, double cy)
    {
        var fig = new PathFigure { IsClosed = true, StartPoint = Vertex(cx, cy, 0) };
        for (int i = 1; i < 6; i++) fig.Segments!.Add(new LineSegment { Point = Vertex(cx, cy, i) });
        var geo = new PathGeometry();
        geo.Figures!.Add(fig);
        return geo;
    }

    // The i-th vertex (0..5) of a hexagon at 60° steps.
    static Point Vertex(double cx, double cy, int i)
    {
        double a = Math.PI / 180 * (60 * i);
        return new(cx + R * Math.Cos(a), cy + R * Math.Sin(a));
    }
}
