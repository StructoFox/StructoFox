using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using StructoFox.Core;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// Edits a flowchart node's text and its formatting: multiline content, font family + size, and
/// bold / italic / underline / strikethrough. Applies to the node on OK; returns true if confirmed.
/// </summary>
public class NodeTextDialog : Window
{
    readonly TextBox       _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 90, MinWidth = 320 };
    readonly ComboBox      _font = new() { MinWidth = 200 };
    readonly TextBox       _size = new() { Width = 56 };
    readonly ToggleButton  _bold      = Toggle("B");
    readonly ToggleButton  _italic    = Toggle("I");
    readonly ToggleButton  _underline = Toggle("U");
    readonly ToggleButton  _strike    = Toggle("S");
    readonly FlowNode      _node;

    // Opens the editor over an owner for a node; returns true if the user confirmed (node mutated). When a project
    // context is supplied, the text box gets AI-free autocomplete (project entities + earlier-in-flow variables).
    public static Task<bool> Edit(Window owner, FlowNode node,
        string? projFolder = null, IReadOnlyDictionary<string, string>? locals = null, ExportLanguage lang = ExportLanguage.CSharp)
        => new NodeTextDialog(node, projFolder, locals ?? new Dictionary<string, string>(), lang).ShowDialog<bool>(owner);

    // Builds the dialog and seeds the controls from the node's current text + formatting.
    NodeTextDialog(FlowNode node, string? projFolder, IReadOnlyDictionary<string, string> locals, ExportLanguage lang)
    {
        _node                 = node;
        if (projFolder is not null)
            CompletionBox.Attach(_text, s => CodeCompletionService.Suggest(projFolder, lang, locals, s));
        Title                 = Loc.S("NodeTxt_Title");
        SizeToContent         = SizeToContent.Height;
        Width                 = 420;
        CanResize             = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Ui.ThemeWindow(this);

        _text.Text = node.Text;
        PopulateFonts(node.FontFamily);
        _size.Text = (node.FontSize ?? 11).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _bold.IsChecked = node.Bold; _italic.IsChecked = node.Italic;
        _underline.IsChecked = node.Underline; _strike.IsChecked = node.Strikethrough;
        _italic.FontStyle = FontStyle.Italic;

        var ok = Ui.Btn(Loc.S("Common_OK")); ok.IsDefault = true; ok.Click += (_, _) => Apply();
        var cancel = Ui.Btn(Loc.S("Common_Cancel")); cancel.IsCancel = true; cancel.Click += (_, _) => Close(false);

        Content = new StackPanel
        {
            Margin = new(16), Spacing = 10,
            Children =
            {
                new TextBlock { Text = Loc.S("NodeTxt_Title") },
                _text,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = Loc.S("NodeTxt_Font"), VerticalAlignment = VerticalAlignment.Center }, _font,
                        new TextBlock { Text = Loc.S("NodeTxt_Size"), VerticalAlignment = VerticalAlignment.Center }, _size,
                    },
                },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _bold, _italic, _underline, _strike } },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { cancel, ok },
                },
            },
        };
    }

    // Fills the font combo with installed families and selects the node's current one (or default).
    void PopulateFonts(string? current)
    {
        _font.Items.Add("(default)");
        foreach (var fam in FontManager.Current.SystemFonts.Select(f => f.Name).Distinct().OrderBy(n => n))
            _font.Items.Add(fam);
        _font.SelectedItem = current is not null && _font.Items.Contains(current) ? current : _font.Items[0];
    }

    // Writes the edited text + formatting back onto the node and closes with success.
    void Apply()
    {
        _node.Text          = _text.Text ?? "";
        _node.FontFamily    = _font.SelectedIndex > 0 ? _font.SelectedItem as string : null;
        _node.FontSize      = double.TryParse(_size.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0 ? s : null;
        _node.Bold          = _bold.IsChecked == true;
        _node.Italic        = _italic.IsChecked == true;
        _node.Underline     = _underline.IsChecked == true;
        _node.Strikethrough = _strike.IsChecked == true;
        Close(true);
    }

    // A small square style toggle button for a text attribute (themed so its glyph stays readable).
    static ToggleButton Toggle(string glyph)
    {
        var b = new ToggleButton { Content = glyph, Width = 34, FontWeight = FontWeight.Bold };
        Ui.Theme(b, TemplatedControl.BackgroundProperty, "ControlBgBrush");
        Ui.Theme(b, TemplatedControl.ForegroundProperty, "SidebarTextBrush");
        return b;
    }
}
