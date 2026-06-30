using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StructoFox.Core;

namespace StructoFox.Plugin.AiCodegen;

/// <summary>Small Avalonia helpers shared by the plugin's windows, so they look like the rest of StructoFox
/// (parented to the host window and themed via <see cref="IPluginContext.ApplyTheme"/>).</summary>
internal static class PluginUi
{
    /// <summary>Creates a window parented and themed by the host.</summary>
    public static Window NewWindow(IPluginContext ctx, string title, double w, double h)
    {
        var win = new Window
        {
            Title = title, Width = w, Height = h,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        ctx.ApplyTheme(win);
        return win;
    }

    /// <summary>Shows a window modally over the host owner if available, else just shows it.</summary>
    public static void Open(this Window win, IPluginContext ctx)
    {
        if (ctx.OwnerWindow is Window owner) win.Show(owner);
        else                                win.Show();
    }

    public static TextBlock Label(string text) => new()
    {
        Text = text, FontWeight = FontWeight.SemiBold, Margin = new(0, 10, 0, 3),
    };

    public static TextBlock Dim(string text) => new()
    {
        Text = text, Opacity = 0.7, FontSize = 11, TextWrapping = TextWrapping.Wrap,
    };

    public static Button Btn(string text) => new()
    {
        Content = text, Padding = new(14, 7), Margin = new(0, 0, 8, 0),
    };

    public static StackPanel Row(params Control[] kids)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        foreach (var k in kids) sp.Children.Add(k);
        return sp;
    }
}
