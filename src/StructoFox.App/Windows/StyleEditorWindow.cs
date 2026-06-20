using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// A compact editor for one element's appearance: collapsible line/fill/text colour fields, a
/// thickness picker, preset slots (incl. built-in B/W standards) and a live preview. Returns the
/// edited style, or null on cancel.
/// </summary>
public class StyleEditorWindow : Window
{
    List<StylePreset> _presets = new();

    readonly ComboBox   _presetCombo = new() { MinWidth = 200 };
    readonly ColorField _lineField   = new(Loc.S("StyleEd_Line"));
    readonly ColorField _fillField   = new(Loc.S("StyleEd_Fill"));
    readonly ColorField _textField   = new(Loc.S("StyleEd_Text"));
    readonly ComboBox   _thickCombo  = new() { MinWidth = 120 };
    readonly Border     _preview     = new() { Height = 56, CornerRadius = new(3) };
    readonly TextBlock  _previewText = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    // Opens the editor over an owner, seeded from the current style; returns the edited style or null.
    public static Task<ElementStyle?> Edit(Window owner, ElementStyle current) =>
        new StyleEditorWindow(current).ShowDialog<ElementStyle?>(owner);

    // Builds the window and loads the starting style into the controls.
    StyleEditorWindow(ElementStyle current)
    {
        Title                 = Loc.S("StyleEd_Title");
        Width                 = 440;
        SizeToContent         = SizeToContent.Height;
        CanResize             = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Ui.ThemeWindow(this);

        Content = BuildContent();
        LoadInto(current);
        UpdatePreview();
    }

    // Lays out the preset chooser, the three collapsible colour fields, thickness, preview and OK/Cancel.
    Control BuildContent()
    {
        var root = new StackPanel { Margin = new(16), Spacing = 8 };

        // Preset slots: choose + apply on one line, save on the next (keeps it from overflowing).
        _presets = PresetStore.All();
        foreach (var p in _presets) _presetCombo.Items.Add(p.Name);
        var applyBtn = Ui.Btn(Loc.S("StyleEd_Apply"));
        applyBtn.Click += (_, _) => { if (_presetCombo.SelectedIndex is var i and >= 0 && i < _presets.Count) LoadInto(_presets[i].Style); };
        var saveBtn = Ui.Btn(Loc.S("StyleEd_SaveSlot"));
        saveBtn.Click += async (_, _) => await SaveAsSlot();
        root.Children.Add(new TextBlock { Text = Loc.S("StyleEd_Preset") });
        root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _presetCombo, applyBtn } });
        root.Children.Add(saveBtn);

        // Collapsible colour fields.
        foreach (var f in new[] { _lineField, _fillField, _textField })
        {
            f.Changed += (_, _) => UpdatePreview();
            root.Children.Add(f);
        }

        // Thickness: inherit, or 1..5 px.
        _thickCombo.Items.Add(Loc.S("Style_Inherit"));
        for (int w = 1; w <= 5; w++) _thickCombo.Items.Add($"{w} px");
        _thickCombo.SelectionChanged += (_, _) => UpdatePreview();
        root.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { new TextBlock { Text = Loc.S("StyleEd_Thickness"), VerticalAlignment = VerticalAlignment.Center, Width = 110 }, _thickCombo },
        });

        // Live preview.
        root.Children.Add(new TextBlock { Text = Loc.S("StyleEd_Preview"), Margin = new(0, 6, 0, 0) });
        _preview.Child = _previewText;
        root.Children.Add(_preview);

        // OK / Cancel.
        var ok = Ui.Btn(Loc.S("Common_OK")); ok.IsDefault = true; ok.Click += (_, _) => Close(BuildStyle());
        var cancel = Ui.Btn(Loc.S("Common_Cancel")); cancel.IsCancel = true; cancel.Click += (_, _) => Close(null);
        root.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
            Margin = new(0, 8, 0, 0), Children = { cancel, ok },
        });
        return root;
    }

    // Seeds the controls from an element style (used at startup and when applying a preset).
    void LoadInto(ElementStyle s)
    {
        SetField(_lineField, s.LineColor);
        SetField(_fillField, s.FillColor);
        SetField(_textField, s.TextColor);
        _thickCombo.SelectedIndex = s.LineThickness is { } t && t >= 1 && t <= 5 ? (int)Math.Round(t) : 0;
        UpdatePreview();
    }

    // Applies one optional hex value to a field: inherit when null, else set the colour.
    static void SetField(ColorField field, string? hex)
    {
        if (hex is null) { field.Inherit = true; return; }
        field.Inherit = false;
        try { field.Color = Color.Parse(hex); } catch { /* keep current */ }
    }

    // Reads the controls back into a fresh ElementStyle (inherit → null fields).
    ElementStyle BuildStyle() => new()
    {
        // RGBA so transparency (e.g. a see-through note's fill/frame/text) is preserved.
        LineColor     = _lineField.Inherit ? null : HexColorPicker.HexOfRgba(_lineField.Color),
        FillColor     = _fillField.Inherit ? null : HexColorPicker.HexOfRgba(_fillField.Color),
        TextColor     = _textField.Inherit ? null : HexColorPicker.HexOfRgba(_textField.Color),
        LineThickness = _thickCombo.SelectedIndex >= 1 ? _thickCombo.SelectedIndex : null,
    };

    // Repaints the preview from the current fields, treating "inherit" as the default look.
    void UpdatePreview()
    {
        var line = _lineField.Inherit ? Colors.Black : _lineField.Color;
        var fill = _fillField.Inherit ? Colors.White : _fillField.Color;
        var text = _textField.Inherit ? Colors.Black : _textField.Color;
        var th   = _thickCombo.SelectedIndex >= 1 ? _thickCombo.SelectedIndex : 1;

        _preview.Background      = new SolidColorBrush(fill);
        _preview.BorderBrush     = new SolidColorBrush(line);
        _preview.BorderThickness = new(th);
        _previewText.Text        = Loc.S("StyleEd_Sample");
        _previewText.Foreground  = new SolidColorBrush(text);
    }

    // Prompts for a name and saves the current look as a reusable preset slot.
    async Task SaveAsSlot()
    {
        var name = await PromptDialog.Show(this, Loc.S("StyleEd_SlotName"), "My style", Loc.S("StyleEd_SaveSlot"));
        if (string.IsNullOrWhiteSpace(name)) return;
        PresetStore.Save(new StylePreset(name.Trim(), BuildStyle()));

        _presets = PresetStore.All();
        _presetCombo.Items.Clear();
        foreach (var p in _presets) _presetCombo.Items.Add(p.Name);
    }
}
