using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace StructoFox.App;

/// <summary>
/// A one-button info message with a "Don't show again" checkbox. Once the user ticks it, the message
/// (identified by a stable key) is suppressed for good via <see cref="SuppressStore"/>.
/// </summary>
public static class InfoDialog
{
    /// <summary>Shows the info unless its key was suppressed earlier. Returns when dismissed.</summary>
    public static Task Show(Window owner, string key, string message, string title = "")
    {
        if (SuppressStore.IsSuppressed(key)) return Task.CompletedTask;

        var dlg = new Window
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, MinWidth = 340, MaxWidth = 560,
        };
        Ui.ThemeWindow(dlg);

        var dontShow = new CheckBox { Content = Loc.S("Common_DontShowAgain") };
        Ui.Theme(dontShow, CheckBox.ForegroundProperty, "ContentTextBrush");

        var ok = Ui.Btn(Loc.S("Common_OK")); ok.IsDefault = true; ok.IsCancel = true;
        ok.HorizontalAlignment = HorizontalAlignment.Right;
        ok.Click += (_, _) => { if (dontShow.IsChecked == true) SuppressStore.Suppress(key); dlg.Close(); };

        var msg = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        Ui.Theme(msg, TextBlock.ForegroundProperty, "ContentTextBrush");

        dlg.Content = new StackPanel { Margin = new(20), Spacing = 14, Children = { msg, dontShow, ok } };
        return dlg.ShowDialog(owner);
    }
}
