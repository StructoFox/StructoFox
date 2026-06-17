using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace StructoFox.App;

/// <summary>
/// A faint honeycomb (flat-top hexagon) texture behind content — a futuristic, "techy" backdrop.
/// Its line colour follows the theme (bind <see cref="LineBrush"/> to an OXSUIT accent), but is forced
/// to a low alpha so it's present without being prominent. Re-renders on resize and on theme swaps.
/// </summary>
public class HoneycombBackdrop : Control
{
    const double R = 10;        // hexagon radius (centre → vertex) — small cells
    const byte LineAlpha = 24;  // accent line transparency (regardless of source brush)
    const byte ShadowAlpha = 32; // dark "shadow" line, offset 1px, for a subtle 3D/embossed look

    /// <summary>The (opaque) source colour for the comb lines — bind to a theme accent brush.</summary>
    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<HoneycombBackdrop, IBrush?>(nameof(LineBrush));

    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    static HoneycombBackdrop() => AffectsRender<HoneycombBackdrop>(LineBrushProperty);

    public HoneycombBackdrop() => ClipToBounds = true;

    // Re-draw on resize so the comb always fills the area.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == BoundsProperty) InvalidateVisual();
    }

    // Tiles small flat-top hexagons: a 1px dark shadow line, then the theme-accent line on top —
    // the slight offset reads as a subtle 3D emboss. Both kept very transparent.
    public override void Render(DrawingContext ctx)
    {
        var src    = (LineBrush as ISolidColorBrush)?.Color ?? Color.FromRgb(128, 128, 128);
        var main   = new Pen(new SolidColorBrush(Color.FromArgb(LineAlpha, src.R, src.G, src.B)), 1);
        var shadow = new Pen(new SolidColorBrush(Color.FromArgb(ShadowAlpha, 0, 0, 0)), 1);

        double stepX = 1.5 * R, stepY = Math.Sqrt(3) * R;
        int col = 0;
        for (double cx = 0; cx <= Bounds.Width + R; cx += stepX, col++)
        {
            double offsetY = (col % 2 == 0) ? 0 : stepY / 2;
            for (double cy = offsetY; cy <= Bounds.Height + R; cy += stepY)
            {
                ctx.DrawGeometry(null, shadow, Hexagon(cx + 1, cy + 1));
                ctx.DrawGeometry(null, main, Hexagon(cx, cy));
            }
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
