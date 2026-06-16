using Avalonia.Controls;
using Avalonia.Layout;

namespace StructoFox.App;

/// <summary>
/// Small factory of consistently-styled controls. The shared toolbox every window dips into,
/// so buttons and friends look the same whether they live on a board, a flowchart, or a dialog.
/// </summary>
public static class Ui
{
    /// <summary>
    /// Builds a standard push button with uniform padding and an optional tooltip.
    /// One button to rule them all — or at least to look the same everywhere.
    /// </summary>
    public static Button Btn(string text, string? tooltip = null)
    {
        var b = new Button
        {
            Content = text,
            Padding = new(12, 6),
            CornerRadius = new(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        if (!string.IsNullOrEmpty(tooltip)) ToolTip.SetTip(b, tooltip);
        return b;
    }
}
