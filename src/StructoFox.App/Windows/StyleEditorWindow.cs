using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StructoFox.Core;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// A dedicated editor for one element's appearance: pick line/fill/text colours and thickness,
/// apply saved preset slots (incl. built-in B/W standards), and save the current look as a new slot.
/// Roomier and clearer than a cascading menu when there are many colours. Returns the edited style.
/// </summary>
public class StyleEditorWindow : Window
{
    List<StylePreset> _presets = new();

    readonly ComboBox    _presetCombo = new() { MinWidth = 220 };
    readonly ColorPicker _linePicker  = new() { Color = Colors.Black };
    readonly ColorPicker _fillPicker  = new() { Color = Colors.White };
    readonly ColorPicker _textPicker  = new() { Color = Colors.Black };
    readonly CheckBox    _lineInherit = new() { Content = "Inherit" };
    readonly CheckBox    _fillInherit = new() { Content = "Inherit" };
    readonly CheckBox    _textInherit = new() { Content = "Inherit" };
    readonly ComboBox    _thickCombo  = new() { MinWidth = 120 };
    readonly Border      _preview     = new() { Height = 60, CornerRadius = new(3) };
    readonly TextBlock   _previewText = new() { Text = "", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    // Opens the editor over an owner, seeded from the current style; returns the edited style or null.
    public static Task<ElementStyle?> Edit(Window owner, ElementStyle current) =>
        new StyleEditorWindow(current).ShowDialog<ElementStyle?>(owner);

    // Builds the window and loads the starting style into the controls.
    StyleEditorWindow(ElementStyle current)
    {
        Title                 = Loc.S("StyleEd_Title");
        Width                 = 420;
        SizeToContent         = SizeToContent.Height;
        CanResize             = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Ui.Theme(this, BackgroundProperty, "ContentBgBrush");

        Content = BuildContent();
        LoadInto(current);
        UpdatePreview();
    }

    // Lays out the preset row, the three colour rows, thickness, a live preview and OK/Cancel.
    Control BuildContent()
    {
        var root = new StackPanel { Margin = new(16), Spacing = 10 };

        // Preset slots: choose one and apply it, or save the current look as a new slot.
        _presets = PresetStore.All();
        foreach (var p in _presets) _presetCombo.Items.Add(p.Name);
        var applyBtn = Ui.Btn(Loc.S("StyleEd_Apply"));
        applyBtn.Click += (_, _) => { if (_presetCombo.SelectedIndex is var i and >= 0 && i < _presets.Count) LoadInto(_presets[i].Style); };
        var saveBtn = Ui.Btn(Loc.S("StyleEd_SaveSlot"));
        saveBtn.Click += async (_, _) => await SaveAsSlot();
        root.Children.Add(new TextBlock { Text = Loc.S("StyleEd_Preset") });
        root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _presetCombo, applyBtn, saveBtn } });

        root.Children.Add(ColorRow(Loc.S("StyleEd_Line"), _linePicker, _lineInherit));
        root.Children.Add(ColorRow(Loc.S("StyleEd_Fill"), _fillPicker, _fillInherit));
        root.Children.Add(ColorRow(Loc.S("StyleEd_Text"), _textPicker, _textInherit));

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

    // Builds one labelled colour row: an inherit checkbox that disables the picker when ticked.
    Control ColorRow(string label, ColorPicker picker, CheckBox inherit)
    {
        inherit.IsCheckedChanged += (_, _) => { picker.IsEnabled = inherit.IsChecked != true; UpdatePreview(); };
        picker.ColorChanged += (_, _) => UpdatePreview();
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Width = 110 },
                inherit,
                picker,
            },
        };
    }

    // Seeds the controls from an element style (used at startup and when applying a preset).
    void LoadInto(ElementStyle s)
    {
        _lineInherit.IsChecked = s.LineColor is null;
        _fillInherit.IsChecked = s.FillColor is null;
        _textInherit.IsChecked = s.TextColor is null;
        if (s.LineColor is { } lc) TrySet(_linePicker, lc);
        if (s.FillColor is { } fc) TrySet(_fillPicker, fc);
        if (s.TextColor is { } tc) TrySet(_textPicker, tc);
        _linePicker.IsEnabled = s.LineColor is not null;
        _fillPicker.IsEnabled = s.FillColor is not null;
        _textPicker.IsEnabled = s.TextColor is not null;
        _thickCombo.SelectedIndex = s.LineThickness is { } t && t >= 1 && t <= 5 ? (int)Math.Round(t) : 0;
        UpdatePreview();
    }

    // Reads the controls back into a fresh ElementStyle (inherit → null fields).
    ElementStyle BuildStyle() => new()
    {
        LineColor     = _lineInherit.IsChecked == true ? null : HexOf(_linePicker.Color),
        FillColor     = _fillInherit.IsChecked == true ? null : HexOf(_fillPicker.Color),
        TextColor     = _textInherit.IsChecked == true ? null : HexOf(_textPicker.Color),
        LineThickness = _thickCombo.SelectedIndex >= 1 ? _thickCombo.SelectedIndex : null,
    };

    // Repaints the preview from the current controls, treating "inherit" as the default look.
    void UpdatePreview()
    {
        var line = _lineInherit.IsChecked == true ? Colors.Black : _linePicker.Color;
        var fill = _fillInherit.IsChecked == true ? Colors.White : _fillPicker.Color;
        var text = _textInherit.IsChecked == true ? Colors.Black : _textPicker.Color;
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

        // Refresh the preset list so the new slot is immediately selectable.
        _presets = PresetStore.All();
        _presetCombo.Items.Clear();
        foreach (var p in _presets) _presetCombo.Items.Add(p.Name);
    }

    // Sets a picker's colour from hex, ignoring an unparseable value.
    static void TrySet(ColorPicker picker, string hex)
    {
        try { picker.Color = Color.Parse(hex); } catch { /* keep current */ }
    }

    // Formats an Avalonia colour as opaque web hex (#RRGGBB).
    static string HexOf(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
