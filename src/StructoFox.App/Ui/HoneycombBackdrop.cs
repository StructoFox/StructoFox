using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace StructoFox.App;

/// <summary>
/// A faint honeycomb (flat-top hexagon) texture behind content — a futuristic, "techy" backdrop.
/// Its line colour follows the theme (bind <see cref="LineBrush"/> to an OXSUIT accent), but is forced
/// to a low alpha so it's present without being prominent.
/// <para>The pattern is baked once into a tiled <see cref="DrawingBrush"/> (one repeating cell) and
/// painted with a single fill, so cost is independent of the area — a full-screen window no longer
/// redraws tens of thousands of hexagons on every layout pass.</para>
/// </summary>
public class HoneycombBackdrop : Control
{
    const double R = 10;          // hexagon radius (centre → vertex) — small cells
    const byte LineAlpha = 2;     // accent line transparency (regardless of source brush)
    const byte ShadowAlpha = 3;   // dark line, offset down-right — reads on light themes
    const byte HighlightAlpha = 4; // light line, offset up-left — reads on dark themes

    /// <summary>The (opaque) source colour for the comb lines — bind to a theme accent brush.</summary>
    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<HoneycombBackdrop, IBrush?>(nameof(LineBrush));

    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    DrawingBrush? _brush;   // cached tile, rebuilt only when the line colour changes
    Color _brushColor;

    static HoneycombBackdrop() => AffectsRender<HoneycombBackdrop>(LineBrushProperty);

    public HoneycombBackdrop() => ClipToBounds = true;

    // Fill the whole area with the tiled comb — one cheap draw regardless of size.
    public override void Render(DrawingContext ctx)
    {
        var src = (LineBrush as ISolidColorBrush)?.Color ?? Color.FromRgb(128, 128, 128);
        if (_brush is null || _brushColor != src) { _brush = BuildBrush(src); _brushColor = src; }
        ctx.FillRectangle(_brush, new Rect(Bounds.Size));
    }

    // Builds a seamless one-cell tile of the honeycomb (a 1px dark shadow + a light highlight, slightly
    // offset for a subtle emboss, then the accent line on top), as a tiling DrawingBrush.
    static DrawingBrush BuildBrush(Color src)
    {
        var main      = new Pen(new SolidColorBrush(Color.FromArgb(LineAlpha, src.R, src.G, src.B)), 1);
        var shadow    = new Pen(new SolidColorBrush(Color.FromArgb(ShadowAlpha, 0, 0, 0)), 1);
        var highlight = new Pen(new SolidColorBrush(Color.FromArgb(HighlightAlpha, 255, 255, 255)), 1);

        double tileW = 3 * R, tileH = Math.Sqrt(3) * R;
        // Hex centres covering one period (plus neighbours so the outlines meet seamlessly at the seams).
        var centres = new (double cx, double cy)[]
        {
            (0, 0), (0, tileH), (tileW, 0), (tileW, tileH),
            (1.5 * R, tileH / 2), (1.5 * R, -tileH / 2), (1.5 * R, 3 * tileH / 2),
        };

        GeometryGroup Group(double ox, double oy)
        {
            var g = new GeometryGroup();
            foreach (var (cx, cy) in centres) g.Children.Add(Hexagon(cx + ox, cy + oy));
            return g;
        }

        var dg = new DrawingGroup();
        dg.Children.Add(new GeometryDrawing { Pen = shadow,    Geometry = Group(1, 1) });
        dg.Children.Add(new GeometryDrawing { Pen = highlight, Geometry = Group(-1, -1) });
        dg.Children.Add(new GeometryDrawing { Pen = main,      Geometry = Group(0, 0) });

        return new DrawingBrush(dg)
        {
            TileMode        = TileMode.Tile,
            Stretch         = Stretch.None,
            SourceRect      = new RelativeRect(0, 0, tileW, tileH, RelativeUnit.Absolute),
            DestinationRect = new RelativeRect(0, 0, tileW, tileH, RelativeUnit.Absolute),
        };
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
