using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// Composes a diagram with its decoration — title, an info "title block", a faint watermark and a logo — into
/// one canvas the way it will print / export. Each decoration sits in a top or bottom band, aligned left/centre/
/// right (six slots), and the band reserves an empty strip so nothing covers the drawing. Several decorations
/// in the same slot lay out in order: logo, title, info. When a logo or title shares a slot with the info field,
/// they're drawn as extra framed cells of the same title block — engineering-office style.
/// </summary>
public static class DiagramDecor
{
    enum Kind { Logo, Title, Info }

    public static Control Compose(Control diagram, string title, DiagramStyle style, Action? onEditTitle = null)
    {
        var items = new List<(DecorPos pos, Kind kind)>();
        if (HasLogo(style))               items.Add((style.LogoPosition,  Kind.Logo));
        if (style.ShowTitle && !string.IsNullOrWhiteSpace(title)) items.Add((style.TitlePosition, Kind.Title));
        if (HasInfo(style))               items.Add((style.InfoPosition,  Kind.Info));

        var dock = new DockPanel { LastChildFill = true };
        if (BuildBand(items, true,  title, style, onEditTitle)  is { } topBand) { DockPanel.SetDock(topBand, Dock.Top);    dock.Children.Add(topBand); }
        if (BuildBand(items, false, title, style, onEditTitle)  is { } botBand) { DockPanel.SetDock(botBand, Dock.Bottom); dock.Children.Add(botBand); }
        dock.Children.Add(diagram);

        var overlay = new Panel { IsHitTestVisible = false };
        AddWatermark(overlay, style);

        var outer = new Grid { Background = new SolidColorBrush(ParseOr(style.BackgroundColor, Colors.White)) };
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

    // One header/footer band: a 3-column grid (left/centre/right). Returns null if empty.
    static Control? BuildBand(List<(DecorPos pos, Kind kind)> items, bool top, string title, DiagramStyle style, Action? onEdit)
    {
        var here = items.Where(i => IsTop(i.pos) == top).ToList();
        if (here.Count == 0) return null;

        // Auto | Star | Auto: the left and right slots take only the width they need (so a right-aligned block
        // doesn't force the band — and the canvas — three times wider); the centre star absorbs the slack and
        // pins the right slot to the canvas edge. The band only grows the canvas when content is genuinely wider.
        var grid = new Grid { Margin = new(12, 8, 12, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        foreach (var (h, col) in new[] { (HorizontalAlignment.Left, 0), (HorizontalAlignment.Center, 1), (HorizontalAlignment.Right, 2) })
        {
            var slot = here.Where(i => HAlign(i.pos) == h).Select(i => i.kind).ToList();
            if (slot.Count == 0) continue;
            var ctrl = BuildSlot(slot, title, style, onEdit);
            if (ctrl is null) continue;
            ctrl.HorizontalAlignment = h;
            ctrl.SetValue(Grid.ColumnProperty, col);
            grid.Children.Add(ctrl);
        }
        return grid;
    }

    // Builds the controls for one slot (in collision order logo→title→info). If the info field shares the slot
    // with a logo/title, they become extra framed cells of the same title block; otherwise a simple spaced row.
    static Control? BuildSlot(List<Kind> kinds, string title, DiagramStyle style, Action? onEdit)
    {
        var text = new SolidColorBrush(ParseOr(style.TextColor, Colors.Black));
        var line = new SolidColorBrush(ParseOr(style.LineColor, Colors.Gray));

        if (kinds.Contains(Kind.Info) && kinds.Count > 1)
        {
            // One continuous title block: logo / title as bordered cells, then the info table inner.
            var info = InfoInner(style, text, line);
            var rowp = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var k in kinds)
            {
                if (k == Kind.Logo && LogoImage(style) is { } lg)
                {
                    lg.VerticalAlignment = VerticalAlignment.Center;
                    lg.Height = LogoHeight;   // start scaled (never native) so it can't inflate the row height
                    // Then track the info table's actual content height so the logo lines up with the rows.
                    info.PropertyChanged += (_, e) =>
                    { if (e.Property == Visual.BoundsProperty && info.Bounds.Height > 0) lg.Height = info.Bounds.Height; };
                    rowp.Children.Add(new Border { BorderBrush = line, BorderThickness = new(0, 0, 1, 0),
                        Padding = new(6, 0), Child = lg, VerticalAlignment = VerticalAlignment.Stretch });
                }
                else if (k == Kind.Title && BuildTitle(title, style, onEdit) is { } tt)
                    rowp.Children.Add(new Border { BorderBrush = line, BorderThickness = new(0, 0, 1, 0),
                        Padding = new(10, 6), Child = tt, VerticalAlignment = VerticalAlignment.Stretch });
                else if (k == Kind.Info)
                    rowp.Children.Add(info);
            }
            return new Border { BorderBrush = line, BorderThickness = new(1), Child = rowp,
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80)) };
        }

        // No merge: a simple spaced row of whatever is here (each piece keeps its own look).
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        foreach (var k in kinds)
        {
            Control? c = k switch
            {
                Kind.Logo  => BuildLogo(style),
                Kind.Title => BuildTitle(title, style, onEdit),
                Kind.Info  => InfoBox(style, text, line),
                _          => null,
            };
            if (c is not null) row.Children.Add(c);
        }
        return row.Children.Count == 0 ? null : row;
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

    static bool HasLogo(DiagramStyle s) => !string.IsNullOrWhiteSpace(s.LogoPath) && File.Exists(s.LogoPath);

    /// <summary>Standard standalone logo height (also the size logos are generally scaled to).</summary>
    const double LogoHeight = 64;

    static Image? LogoImage(DiagramStyle style)
    {
        if (!HasLogo(style)) return null;
        try { return new Image { Source = new Bitmap(style.LogoPath), Stretch = Stretch.Uniform, IsHitTestVisible = false }; }
        catch { return null; }
    }

    static Control? BuildLogo(DiagramStyle style)
    {
        var img = LogoImage(style);
        if (img is null) return null;
        img.Height = LogoHeight;                 // scaled to the standard height; width follows the aspect ratio
        img.VerticalAlignment = VerticalAlignment.Center;
        return img;
    }

    static bool HasInfo(DiagramStyle s) => s.ShowInfo && (
        !string.IsNullOrWhiteSpace(s.InfoName) || !string.IsNullOrWhiteSpace(s.InfoProject) ||
        !string.IsNullOrWhiteSpace(s.InfoProjectNo) || !string.IsNullOrWhiteSpace(s.InfoVersion) ||
        !string.IsNullOrWhiteSpace(s.InfoDate) || !string.IsNullOrWhiteSpace(s.InfoAuthor) ||
        !string.IsNullOrWhiteSpace(s.InfoExtra));

    // The info field wrapped in its own outer frame (used when it stands alone in a slot).
    static Control InfoBox(DiagramStyle style, IBrush text, IBrush line) => new Border
    {
        BorderBrush = line, BorderThickness = new(1), Child = InfoInner(style, text, line),
        Background = new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80)),
        VerticalAlignment = VerticalAlignment.Center,
    };

    // The info field's INNER content (no outer frame), so it can be embedded into a merged title block.
    static Control InfoInner(DiagramStyle style, IBrush text, IBrush line)
    {
        var rightTop = Cols(line, "1,1",
            Cell("Decor_InfoProjectNo", style.InfoProjectNo, new(0, 0, 1, 0), text, line),
            Cell("Decor_InfoProject",   style.InfoProject,   new(0), text, line));
        var rightBottom = Cols(line, "1,1,1",
            Cell("Decor_InfoVersion", style.InfoVersion, new(0, 0, 1, 0), text, line),
            Cell("Decor_InfoDate",    style.InfoDate,    new(0, 0, 1, 0), text, line),
            Cell("Decor_InfoAuthor",  style.InfoAuthor,  new(0), text, line));
        var rightTopWrap = new Border { BorderBrush = line, BorderThickness = new(0, 0, 0, 1), Child = rightTop };

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        right.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        rightTopWrap.SetValue(Grid.RowProperty, 0); right.Children.Add(rightTopWrap);
        rightBottom.SetValue(Grid.RowProperty, 1); right.Children.Add(rightBottom);

        var name = new Border { BorderBrush = line, BorderThickness = new(0, 0, 1, 0), MinWidth = 170,
            Child = Cell("Decor_InfoName", style.InfoName, new(0), text, line) };
        var main = Cols(line, "auto,1", name, right);

        // Top-aligned so its Bounds height equals its content height (it must not stretch to the row, otherwise
        // a merged logo bound to that height would feed back and grow).
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        stack.Children.Add(main);
        if (!string.IsNullOrWhiteSpace(style.InfoExtra))
            stack.Children.Add(new Border { BorderBrush = line, BorderThickness = new(0, 1, 0, 0),
                Child = Cell("Decor_InfoExtra", style.InfoExtra, new(0), text, line) });
        return stack;
    }

    // One title-block cell: label (bold, size 7) above, value (size 14) below; sep = which edges draw a line.
    static Control Cell(string labelKey, string value, Thickness sep, IBrush text, IBrush line) => new Border
    {
        BorderBrush = line, BorderThickness = sep, Padding = new(6, 3, 6, 4),
        VerticalAlignment = VerticalAlignment.Stretch,
        Child = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = Loc.S(labelKey), FontWeight = FontWeight.Bold, FontSize = 7, Foreground = text },
                new TextBlock { Text = value, FontSize = 14, Foreground = text, TextWrapping = TextWrapping.Wrap, MinHeight = 18 },
            },
        },
    };

    static Grid Cols(IBrush line, string defs, params Control[] cells)
    {
        var g = new Grid();
        foreach (var d in defs.Split(','))
            g.ColumnDefinitions.Add(new ColumnDefinition(d == "auto" ? GridLength.Auto : new GridLength(double.Parse(d), GridUnitType.Star)));
        for (int i = 0; i < cells.Length; i++) { cells[i].SetValue(Grid.ColumnProperty, i); g.Children.Add(cells[i]); }
        return g;
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
