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

        // Space-reserving header/footer bands; each has three slots (left / centre / right).
        var dock = new DockPanel { LastChildFill = true };
        if (BuildBand(items, top: true)  is { } topBand)  { DockPanel.SetDock(topBand, Dock.Top);    dock.Children.Add(topBand); }
        if (BuildBand(items, top: false) is { } botBand)  { DockPanel.SetDock(botBand, Dock.Bottom); dock.Children.Add(botBand); }
        dock.Children.Add(diagram);   // fills the centre

        // Overlay layer: the faint, centred watermark behind everything; click-through.
        var overlay = new Panel { IsHitTestVisible = false };
        AddWatermark(overlay, style);

        var outer = new Grid
        {
            // The whole composed area carries the diagram's background, so the reserved decoration bands read
            // as part of the canvas (the canvas simply grows to make room) rather than a frame around it.
            Background = new SolidColorBrush(ParseOr(style.BackgroundColor, Colors.White)),
        };
        outer.Children.Add(dock);
        outer.Children.Add(overlay);
        return outer;
    }

    static bool IsTop(DecorPos p)  => p is DecorPos.TopLeft or DecorPos.TopCenter or DecorPos.TopRight;
    static HorizontalAlignment HAlign(DecorPos p) => p switch
    {
        DecorPos.TopLeft  or DecorPos.BottomLeft  => HorizontalAlignment.Left,
        DecorPos.TopRight or DecorPos.BottomRight => HorizontalAlignment.Right,
        _                                         => HorizontalAlignment.Center,
    };

    // Builds one header/footer band: a 3-column grid (left / centre / right). Items in the same slot stack
    // horizontally in collision order. Returns null if the band has no items.
    static Control? BuildBand(List<(DecorPos pos, Control ctrl)> items, bool top)
    {
        var here = items.Where(i => IsTop(i.pos) == top).ToList();
        if (here.Count == 0) return null;

        var grid = new Grid { Margin = new(12, 8, 12, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        foreach (var (h, col) in new[] { (HorizontalAlignment.Left, 0), (HorizontalAlignment.Center, 1), (HorizontalAlignment.Right, 2) })
        {
            var slot = here.Where(i => HAlign(i.pos) == h).Select(i => i.ctrl).ToList();
            if (slot.Count == 0) continue;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = h,
                VerticalAlignment = VerticalAlignment.Center };
            foreach (var c in slot) row.Children.Add(c);
            row.SetValue(Grid.ColumnProperty, col);
            grid.Children.Add(row);
        }
        return grid;
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

    // The optional "title block" / Schriftfeld: a line-separated table. Name fills the left; the right side has
    // ProjectNo + Project on top and Version + Date + Author below; an optional free note spans the bottom.
    // Each cell shows its label small+bold on top, the value larger underneath.
    static Control? BuildInfo(DiagramStyle style)
    {
        if (!style.ShowInfo) return null;

        var text = new SolidColorBrush(ParseOr(style.TextColor, Colors.Black));
        var line = new SolidColorBrush(ParseOr(style.LineColor, Colors.Gray));

        // One cell: label (bold, size 7) on a top line, value (size 14) on the line below.
        Control Cell(string labelKey, string value, Thickness sep) => new Border
        {
            BorderBrush = line, BorderThickness = sep, Padding = new(6, 3, 6, 4),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = Loc.S(labelKey), FontWeight = FontWeight.Bold, FontSize = 7, Foreground = text },
                    new TextBlock { Text = value, FontSize = 14, Foreground = text, TextWrapping = TextWrapping.Wrap,
                        MinHeight = 18 },
                },
            },
        };

        Grid Cols(string defs, params Control[] cells)
        {
            var g = new Grid();
            foreach (var d in defs.Split(',')) g.ColumnDefinitions.Add(new ColumnDefinition(
                d == "auto" ? GridLength.Auto : new GridLength(double.Parse(d), GridUnitType.Star)));
            for (int i = 0; i < cells.Length; i++) { cells[i].SetValue(Grid.ColumnProperty, i); g.Children.Add(cells[i]); }
            return g;
        }

        // Right block: two stacked rows, separated by a horizontal line.
        var rightTop = Cols("1,1",
            Cell("Decor_InfoProjectNo", style.InfoProjectNo, new(0, 0, 1, 0)),
            Cell("Decor_InfoProject",   style.InfoProject,   new(0)));
        var rightBottom = Cols("1,1,1",
            Cell("Decor_InfoVersion", style.InfoVersion, new(0, 0, 1, 0)),
            Cell("Decor_InfoDate",    style.InfoDate,    new(0, 0, 1, 0)),
            Cell("Decor_InfoAuthor",  style.InfoAuthor,  new(0)));
        var rightTopWrap = new Border { BorderBrush = line, BorderThickness = new(0, 0, 0, 1), Child = rightTop };
        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        right.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        rightTopWrap.SetValue(Grid.RowProperty, 0); right.Children.Add(rightTopWrap);
        rightBottom.SetValue(Grid.RowProperty, 1); right.Children.Add(rightBottom);

        // Name fills the left, vertically spanning both right rows; a vertical line divides it from the right.
        var name = new Border { BorderBrush = line, BorderThickness = new(0, 0, 1, 0), MinWidth = 170,
            Child = Cell("Decor_InfoName", style.InfoName, new(0)) };
        var main = Cols("auto,1", name, right);

        // Stack the main block over an optional full-width note row, all inside the outer box.
        var stack = new StackPanel();
        stack.Children.Add(main);
        if (!string.IsNullOrWhiteSpace(style.InfoExtra))
            stack.Children.Add(new Border { BorderBrush = line, BorderThickness = new(0, 1, 0, 0),
                Child = Cell("Decor_InfoExtra", style.InfoExtra, new(0)) });

        return new Border
        {
            BorderBrush = line, BorderThickness = new(1), Child = stack,
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
