using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace StructoFox.App;

// Which buttons a message dialog offers. Maps to the WPF MessageBoxButton cases we actually used.
public enum DialogButtons { Ok, OkCancel, YesNo, YesNoCancel }

// What the user picked. Closing the window (X) counts as the "soft no" — Cancel, or No if no Cancel exists.
public enum DialogResult { Ok, Cancel, Yes, No }

/// <summary>
/// A tiny modal message box — Avalonia ships without one, so the fox brought its own.
/// Replaces ClaudetRelay's <c>MessageBox.Show(text, title, buttons, image)</c> calls.
/// </summary>
public static class MessageDialog
{
    /// <summary>
    /// Shows a modal message over <paramref name="owner"/> and awaits the user's choice.
    /// Returns the picked <see cref="DialogResult"/>; dismissing the window yields the soft-no.
    /// </summary>
    public static Task<DialogResult> Show(
        Window owner, string message, string title = "", DialogButtons buttons = DialogButtons.Ok)
    {
        var softNo = buttons == DialogButtons.YesNo ? DialogResult.No : DialogResult.Cancel;

        var dlg = new Window
        {
            Title                 = title,
            SizeToContent         = SizeToContent.WidthAndHeight,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            MinWidth              = 320,
            MaxWidth              = 560,
        };
        Ui.ThemeWindow(dlg);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };

        // Wires one labelled button to close the dialog with the given result.
        void Add(string label, DialogResult result, bool isDefault = false)
        {
            var b = Ui.Btn(label);
            b.IsDefault = isDefault;
            b.Click += (_, _) => dlg.Close(result);
            row.Children.Add(b);
        }

        switch (buttons)
        {
            case DialogButtons.OkCancel:
                Add(Loc.S("Common_Cancel"), DialogResult.Cancel);
                Add(Loc.S("Common_OK"), DialogResult.Ok, isDefault: true);
                break;
            case DialogButtons.YesNo:
                Add(Loc.S("Common_No"), DialogResult.No);
                Add(Loc.S("Common_Yes"), DialogResult.Yes, isDefault: true);
                break;
            case DialogButtons.YesNoCancel:
                Add(Loc.S("Common_Cancel"), DialogResult.Cancel);
                Add(Loc.S("Common_No"), DialogResult.No);
                Add(Loc.S("Common_Yes"), DialogResult.Yes, isDefault: true);
                break;
            default:
                Add(Loc.S("Common_OK"), DialogResult.Ok, isDefault: true);
                break;
        }

        dlg.Content = new StackPanel
        {
            Margin = new(20),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                row,
            },
        };

        return dlg.ShowDialog<DialogResult>(owner);
    }
}
