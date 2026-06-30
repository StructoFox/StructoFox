using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// Composes a diagram with its decoration — an optional title, an info "title block", a faint watermark and a
/// logo — into one canvas the way it will print / export. Title, logo and info each sit at a chosen position
/// (Top/Bottom/Left/Right reserve an EMPTY band around the diagram so nothing overlaps the drawing; Center
/// overlays it). Several decorations sharing a position are laid out in order: logo, title, info. The watermark
/// is always a faint centred overlay behind everything. The fox's letterhead, basically.
/// </summary>
public static class DiagramDecor
{
    /// <summary>Wraps <paramref name="diagram"/> with the configured decoration, reserving space for edge
    /// decorations so they never cover the drawing. Returns the composed control to host on the canvas.</summary>
    public static Control Compose(Control diagram, string title, DiagramStyle style, Action? onEditTitle = null)
    {
        // Collect the positioned decorations in their collision order: logo, then title, then info.
        var items = new List<(DecorPos pos, Control ctrl)>();
        if (BuildLogo(style)  is { } logo)  items.Add((style.LogoPosition,  logo));
        if (BuildTitle(title, style, onEditTitle) is { } ttl) items.Add((style.TitlePosition, ttl));
        if (BuildInfo(style)  is { } info)  items.Add((style.InfoPosition,  info));

        // Space-reserving bands around the diagram (Top/Bottom = horizontal rows, Left/Right = vertical stacks).
        var dock = new DockPanel { LastChildFill = true };
        AddBand(dock, Dock.Top,    items, DecorPos.Top);
        AddBand(dock, Dock.Bottom, items, DecorPos.Bottom);
        AddBand(dock, Dock.Left,   items, DecorPos.Left);
        AddBand(dock, Dock.Right,  items, DecorPos.Right);
        dock.Children.Add(diagram);   // fills the centre

        // Overlay layer (faint watermark + any Center-positioned decorations), click-through.
        var overlay = new Panel { IsHitTestVisible = false };
        AddWatermark(overlay, style);
        var centre = items.Where(i => i.pos == DecorPos.Center).Select(i => i.ctrl).ToList();
        if (centre.Count > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            foreach (var c in centre) row.Children.Add(c);
            // The title in the centre may want to be editable, so this overlay piece opts back into hit-testing.
            row.IsHitTestVisible = true;
            overlay.Children.Add(row);
        }

        var outer = new Grid();
        outer.Children.Add(dock);
        outer.Children.Add(overlay);
        return outer;
    }

    static void AddBand(DockPanel dock, Dock side, List<(DecorPos pos, Control ctrl)> items, DecorPos pos)
    {
        var here = items.Where(i => i.pos == pos).Select(i => i.ctrl).ToList();
        if (here.Count == 0) return;
        bool horizontal = pos is DecorPos.Top or DecorPos.Bottom;
        var band = new StackPanel
        {
            Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical,
            Spacing = 12, Margin = new(12, 8, 12, 8),
            HorizontalAlignment = horizontal ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
            VerticalAlignment   = horizontal ? VerticalAlignment.Stretch  : VerticalAlignment.Center,
        };
        foreach (var c in here) band.Children.Add(c);
        DockPanel.SetDock(band, side);
        dock.Children.Add(band);
    }

    // ── Pieces ───────────────────────────────────────────────────────────────

    static Control? BuildTitle(string title, DiagramStyle style, Action? onEditTitle)
    {
        if (!style.ShowTitle || string.IsNullOrWhiteSpace(title)) return null;
        var heading = new TextBlock
        {
            Text = title,
            FontSize = style.TitleFontSize, FontWeight = style.TitleBold ? FontWeight.Bold : FontWeight.Normal,
            Foreground = new SolidColorBrush(ParseOr(string.IsNullOrWhiteSpace(style.TitleColor) ? style.TextColor : style.TitleColor, Colors.Black)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        if (onEditTitle is not null)
        {
            ToolTip.SetTip(heading, Loc.S("Decor_TitleEditTip"));
            heading.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(heading).Properties.IsRightButtonPressed) { onEditTitle(); e.Handled = true; }
            };
        }
        return heading;
    }

    static Control? BuildLogo(DiagramStyle style)
    {
        if (string.IsNullOrWhiteSpace(style.LogoPath) || !File.Exists(style.LogoPath)) return null;
        try
        {
            return new Image { Source = new Bitmap(style.LogoPath), Width = 96, Stretch = Stretch.Uniform,
                IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
        }
        catch { return null; }
    }

    // The optional "title block" / Schriftfeld: a small bordered table of the non-empty info fields.
    static Control? BuildInfo(DiagramStyle style)
    {
        if (!style.ShowInfo) return null;

        var rows = new List<(string label, string val)>();
        void Add(string key, string val) { if (!string.IsNullOrWhiteSpace(val)) rows.Add((Loc.S(key), val)); }
        Add("Decor_InfoName",      style.InfoName);
        Add("Decor_InfoProject",   style.InfoProject);
        Add("Decor_InfoProjectNo", style.InfoProjectNo);
        Add("Decor_InfoVersion",   style.InfoVersion);
        Add("Decor_InfoDate",      style.InfoDate);
        Add("Decor_InfoAuthor",    style.InfoAuthor);
        bool hasExtra = !string.IsNullOrWhiteSpace(style.InfoExtra);
        if (rows.Count == 0 && !hasExtra) return null;

        var text = new SolidColorBrush(ParseOr(style.TextColor, Colors.Black));
        var line = new SolidColorBrush(ParseOr(style.LineColor, Colors.Gray));

        var grid = new Grid { RowSpacing = 2, ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        int r = 0;
        foreach (var (label, val) in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var l = new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, Foreground = text, FontSize = 12 };
            var v = new TextBlock { Text = val, Foreground = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(l, r); Grid.SetColumn(l, 0); grid.Children.Add(l);
            Grid.SetRow(v, r); Grid.SetColumn(v, 1); grid.Children.Add(v);
            r++;
        }
        if (hasExtra)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var extra = new TextBlock { Text = style.InfoExtra, Foreground = text, FontSize = 12,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 360, Margin = new(0, rows.Count > 0 ? 4 : 0, 0, 0) };
            Grid.SetRow(extra, r); Grid.SetColumn(extra, 0); Grid.SetColumnSpan(extra, 2);
            grid.Children.Add(extra);
        }

        return new Border
        {
            BorderBrush = line, BorderThickness = new(1), CornerRadius = new(3),
            Padding = new(10, 8), Child = grid,
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80)),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
    }

    static void AddWatermark(Panel layer, DiagramStyle style)
    {
        if (!string.IsNullOrWhiteSpace(style.WatermarkImage) && File.Exists(style.WatermarkImage))
        {
            try
            {
                layer.Children.Add(new Image
                {
                    Source = new Bitmap(style.WatermarkImage), Width = 440, Stretch = Stretch.Uniform, Opacity = 0.08,
                    IsHitTestVisible = false,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    RenderTransform = new RotateTransform(style.WatermarkAngle), RenderTransformOrigin = RelativePoint.Center,
                });
            }
            catch { /* unreadable image → no watermark */ }
        }
        if (!string.IsNullOrWhiteSpace(style.Watermark))
        {
            layer.Children.Add(new TextBlock
            {
                Text = style.Watermark, FontSize = 80, FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(ParseOr(style.LineColor, Colors.Gray)),
                Opacity = 0.08, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = new RotateTransform(style.WatermarkAngle), RenderTransformOrigin = RelativePoint.Center,
            });
        }
    }

    static Color ParseOr(string hex, Color fallback)
    {
        try { return Color.Parse(hex); } catch { return fallback; }
    }
}
