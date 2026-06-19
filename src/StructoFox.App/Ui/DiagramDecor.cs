using Avalonia.Controls;
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
    /// <summary>Assembles the overlay (transparent, click-through). Empty when nothing is configured.</summary>
    public static Control Build(string title, DiagramStyle style)
    {
        var layer = new Panel { IsHitTestVisible = false };

        // Watermark — large, faint, rotated, centred behind everything.
        if (!string.IsNullOrWhiteSpace(style.Watermark))
        {
            layer.Children.Add(new TextBlock
            {
                Text = style.Watermark,
                FontSize = 80, FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(ParseOr(style.LineColor, Colors.Gray)),
                Opacity = 0.08,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = new RotateTransform(-30),
            });
        }

        // Title heading — top-centre.
        if (style.ShowTitle && !string.IsNullOrWhiteSpace(title))
        {
            layer.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 20, FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(ParseOr(style.TextColor, Colors.Black)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new(0, 10, 0, 0),
            });
        }

        // Corner logo.
        if (!string.IsNullOrWhiteSpace(style.LogoPath) && File.Exists(style.LogoPath))
        {
            try
            {
                var img = new Image { Source = new Bitmap(style.LogoPath), Width = 96, Stretch = Stretch.Uniform, Margin = new(14) };
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
