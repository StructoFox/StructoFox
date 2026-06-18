using Avalonia.Controls;
using Avalonia.Layout;

namespace StructoFox.App;

/// <summary>
/// A one-line text prompt — replaces ClaudetRelay's <c>PromptText(prompt, initial)</c> helper.
/// Asks the user a quick question and hands back what they typed.
/// </summary>
public static class PromptDialog
{
    /// <summary>
    /// Shows a modal text prompt over <paramref name="owner"/> and awaits the answer.
    /// Returns the entered string, or <c>null</c> if the user cancelled — empty is a valid answer, absence isn't.
    /// </summary>
    public static Task<string?> Show(Window owner, string prompt, string initial = "", string title = "")
    {
        var box = new TextBox { Text = initial, MinWidth = 320 };

        var dlg = new Window
        {
            Title                 = title,
            SizeToContent         = SizeToContent.WidthAndHeight,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        Ui.ThemeWindow(dlg);

        var ok     = Ui.Btn("OK");
        var cancel = Ui.Btn("Cancel");
        ok.IsDefault    = true;   // Enter confirms
        cancel.IsCancel = true;   // Esc bails
        ok.Click     += (_, _) => dlg.Close(box.Text ?? "");
        cancel.Click += (_, _) => dlg.Close(null);

        dlg.Content = new StackPanel
        {
            Margin = new(20),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = prompt },
                box,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, ok },
                },
            },
        };

        // Focus the box so the user can just start typing — no extra click required.
        dlg.Opened += (_, _) => box.Focus();

        return dlg.ShowDialog<string?>(owner);
    }
}
