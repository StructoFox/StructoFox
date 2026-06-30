using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using StructoFox.Core;

namespace StructoFox.Plugin.AiCodegen;

/// <summary>A themed error window with a one-line summary and an expandable, copy-to-clipboard details box.
/// Used when the OS secret store can't be reached, so the user sees exactly what failed and what to install.</summary>
internal static class ErrorDialog
{
    public static void Show(IPluginContext ctx, string summary, string details)
    {
        var win = PluginUi.NewWindow(ctx, "Fehler", 560, 420);

        var detailsBox = new TextBox
        {
            Text = details, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas, Menlo, monospace"), FontSize = 12, MinHeight = 180,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(detailsBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(detailsBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        var copy = new Button { Content = "📋  Details kopieren" };
        copy.Click += async (_, _) =>
        {
            var cb = TopLevel.GetTopLevel(win)?.Clipboard;
            if (cb is not null) { await cb.SetTextAsync(summary + "\n\n" + details); copy.Content = "✓ Kopiert"; }
        };

        var close = new Button { Content = "Schließen", IsCancel = true, Margin = new(8, 0, 0, 0) };
        close.Click += (_, _) => win.Close();

        var panel = new StackPanel { Margin = new(20), Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = "⚠  " + summary, FontWeight = FontWeight.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(210, 90, 70)),
        });
        panel.Children.Add(new Expander
        {
            Header = "Technische Details", IsExpanded = true,
            Content = detailsBox, Margin = new(0, 6, 0, 0),
        });
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new(0, 8, 0, 0), Children = { copy, close },
        });

        win.Content = new ScrollViewer { Content = panel };
        win.Open(ctx);
    }
}
