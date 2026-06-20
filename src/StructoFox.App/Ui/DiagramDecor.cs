using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// Builds the decoration layer for a diagram plan — an optional title heading, a faint diagonal
/// watermark, and a corner logo — driven by the diagram's <see cref="DiagramStyle"/>. It sits as a
/// non-interactive overlay over the canvas, so it reads as part of the document (and will travel into
/// future exports). The fox's letterhead, basically.
/// </summary>
public static class DiagramDecor
{
    /// <summary>Assembles the overlay (transparent, click-through). Empty when nothing is configured.
    /// If <paramref name="onEditTitle"/> is given, the title heading is made clickable (right-click to
    /// edit its properties).</summary>
    public static Control Build(string title, DiagramStyle style, Action? onEditTitle = null)
    {
        // The layer itself has no background, so empty areas already pass clicks through to the canvas;
        // the decorative children are explicitly click-through, only the title can opt in to right-click.
        var layer = new Panel();

        // Image watermark — large, faint, rotated, centred behind everything.
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

        // Text watermark — large, faint, rotated, centred behind everything.
        if (!string.IsNullOrWhiteSpace(style.Watermark))
        {
            layer.Children.Add(new TextBlock
            {
                Text = style.Watermark,
                FontSize = 80, FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(ParseOr(style.LineColor, Colors.Gray)),
                Opacity = 0.08, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = new RotateTransform(style.WatermarkAngle), RenderTransformOrigin = RelativePoint.Center,
            });
        }

        // Title heading — positioned per the style; optionally right-clickable to edit.
        if (style.ShowTitle && !string.IsNullOrWhiteSpace(title))
        {
            var (h, v, top) = style.TitlePosition switch
            {
                TitlePos.TopLeft      => (HorizontalAlignment.Left,   VerticalAlignment.Top,    true),
                TitlePos.TopRight     => (HorizontalAlignment.Right,  VerticalAlignment.Top,    true),
                TitlePos.BottomLeft   => (HorizontalAlignment.Left,   VerticalAlignment.Bottom, false),
                TitlePos.BottomCenter => (HorizontalAlignment.Center, VerticalAlignment.Bottom, false),
                TitlePos.BottomRight  => (HorizontalAlignment.Right,  VerticalAlignment.Bottom, false),
                _                     => (HorizontalAlignment.Center, VerticalAlignment.Top,    true),
            };
            var heading = new TextBlock
            {
                Text = title,
                FontSize = style.TitleFontSize, FontWeight = style.TitleBold ? FontWeight.Bold : FontWeight.Normal,
                Foreground = new SolidColorBrush(ParseOr(string.IsNullOrWhiteSpace(style.TitleColor) ? style.TextColor : style.TitleColor, Colors.Black)),
                HorizontalAlignment = h, VerticalAlignment = v,
                Margin = top ? new(14, 10, 14, 0) : new(14, 0, 14, 10),
                IsHitTestVisible = onEditTitle is not null,
            };
            if (onEditTitle is not null)
            {
                ToolTip.SetTip(heading, Loc.S("Decor_TitleEditTip"));
                heading.PointerPressed += (_, e) =>
                {
                    if (e.GetCurrentPoint(heading).Properties.IsRightButtonPressed) { onEditTitle(); e.Handled = true; }
                };
            }
            layer.Children.Add(heading);
        }

        // Corner logo.
        if (!string.IsNullOrWhiteSpace(style.LogoPath) && File.Exists(style.LogoPath))
        {
            try
            {
                var img = new Image { Source = new Bitmap(style.LogoPath), Width = 96, Stretch = Stretch.Uniform, Margin = new(14), IsHitTestVisible = false };
                (img.HorizontalAlignment, img.VerticalAlignment) = style.LogoCorner switch
                {
                    DecorCorner.TopLeft     => (HorizontalAlignment.Left,  VerticalAlignment.Top),
                    DecorCorner.TopRight    => (HorizontalAlignment.Right, VerticalAlignment.Top),
                    DecorCorner.BottomLeft  => (HorizontalAlignment.Left,  VerticalAlignment.Bottom),
                    _                       => (HorizontalAlignment.Right, VerticalAlignment.Bottom),
                };
                layer.Children.Add(img);
            }
            catch { /* an unreadable image just shows no logo */ }
        }

        return layer;
    }

    static Color ParseOr(string hex, Color fallback)
    {
        try { return Color.Parse(hex); } catch { return fallback; }
    }
}
