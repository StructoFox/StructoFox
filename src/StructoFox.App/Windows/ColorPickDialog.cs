using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace StructoFox.App;

/// <summary>
/// A minimal modal colour picker: shows a picker seeded from a starting colour and returns the
/// chosen value as web hex (#RRGGBB), or null if cancelled. Reusable wherever one colour is needed.
/// </summary>
public class ColorPickDialog : Window
{
    readonly HexColorPicker _picker = new();

    // Opens the picker over an owner and awaits the chosen hex (or null on cancel).
    public static Task<string?> Pick(Window owner, string title, string? initialHex)
        => new ColorPickDialog(title, initialHex).ShowDialog<string?>(owner);

    // Builds the little dialog, seeding the picker from the initial colour when given.
    ColorPickDialog(string title, string? initialHex)
    {
        Title                 = title;
        SizeToContent         = SizeToContent.WidthAndHeight;
        CanResize             = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Ui.ThemeWindow(this);

        if (initialHex is not null)
            try { _picker.Color = Color.Parse(initialHex); } catch { /* keep default */ }

        var ok = Ui.Btn(Loc.S("Common_OK")); ok.IsDefault = true;
        ok.Click += (_, _) => Close($"#{_picker.Color.R:X2}{_picker.Color.G:X2}{_picker.Color.B:X2}");
        var cancel = Ui.Btn(Loc.S("Common_Cancel")); cancel.IsCancel = true;
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new(16), Spacing = 12,
            Children =
            {
                _picker,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { cancel, ok },
                },
            },
        };
    }
}
