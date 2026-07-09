using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace StructoFox.App;

/// <summary>
/// Minimal modal that asks which code syntax a freshly-created (embedded) project should use for the node-editor
/// autocomplete. Returns the chosen <c>ExportLanguage</c> name, or null if cancelled.
/// </summary>
public class SyntaxDialog : Window
{
    readonly ComboBox _lang = new() { MinWidth = 200 };

    /// <summary>Shows the picker over an owner; returns the selected ExportLanguage name, or null on cancel.</summary>
    public static Task<string?> Pick(Window owner) => new SyntaxDialog().ShowDialog<string?>(owner);

    SyntaxDialog()
    {
        Title                 = Loc.S("Syntax_Title");
        Width                 = 380;
        SizeToContent         = SizeToContent.Height;
        CanResize             = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Ui.ThemeWindow(this);

        foreach (var (_, label) in FlowChartWindow.AuthorLanguages) _lang.Items.Add(label);
        _lang.SelectedIndex = 0;

        var ok = Ui.Btn(Loc.S("Common_OK")); ok.IsDefault = true;
        ok.Click += (_, _) => Close(FlowChartWindow.AuthorLanguages[System.Math.Max(0, _lang.SelectedIndex)].Lang.ToString());
        var cancel = Ui.Btn(Loc.S("Common_Cancel")); cancel.IsCancel = true; cancel.Click += (_, _) => Close(null);

        var intro = new TextBlock { Text = Loc.S("Syntax_Intro"), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        Ui.Theme(intro, TextBlock.ForegroundProperty, "ContentTextBrush");

        Content = new StackPanel
        {
            Margin = new(18), Spacing = 12,
            Children =
            {
                intro,
                _lang,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } },
            },
        };
    }
}
