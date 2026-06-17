using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace StructoFox.App;

/// <summary>
/// A faint blueprint-style grid drawn behind content — the "drafting board" texture of the
/// Dev-Cockpit shell. Theme-agnostic (a low-alpha grey), so it reads on light and dark surfaces alike.
/// </summary>
public class GridBackdrop : Control
{
    const double Step = 24;
    static readonly Pen Line = new(new SolidColorBrush(Color.FromArgb(26, 128, 128, 128)), 1);

    public GridBackdrop() => ClipToBounds = true;

    // Re-draw whenever the control is resized, so the grid always fills the area.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == BoundsProperty) InvalidateVisual();
    }

    // Paints the vertical + horizontal hairlines across the whole control.
    public override void Render(DrawingContext ctx)
    {
        for (double x = 0; x <= Bounds.Width; x += Step) ctx.DrawLine(Line, new(x, 0), new(x, Bounds.Height));
        for (double y = 0; y <= Bounds.Height; y += Step) ctx.DrawLine(Line, new(0, y), new(Bounds.Width, y));
    }
}
