using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using StructoFox.Core;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// Curate colour palettes: pick a palette, add/edit/remove named colours via a colour picker,
/// create new palettes and save them as portable files. Shared by every diagram editor.
/// </summary>
public class PaletteEditorWindow : Window
{
    List<ColorPalette> _palettes;
    ColorPalette       _current;
    NamedColor?        _selected;

    readonly ComboBox    _paletteCombo = new() { MinWidth = 220 };
    readonly WrapPanel   _swatches     = new();
    readonly TextBox     _nameBox      = new() { PlaceholderText = "Colour name", MinWidth = 160 };
    readonly HexColorPicker _picker    = new(showPalette: false) { Color = Colors.SteelBlue };

    // Loads the saved palettes (seeding if needed) and builds the editor around the first one.
    public PaletteEditorWindow()
    {
        Title                 = "🎨 Palette editor";
        Width                 = 560;
        Height                = 680;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Ui.ThemeWindow(this);

        _palettes = PaletteStore.LoadAll();
        _current  = _palettes.FirstOrDefault() ?? PaletteService.BuiltIn();

        Content = BuildContent();
        RefreshPaletteCombo();
        RebuildSwatches();
    }

    // Lays out the three bands: palette chooser (top), swatch grid (middle), colour editor (bottom).
    Control BuildContent()
    {
        var root = new DockPanel { Margin = new(14) };

        // ── Top: palette chooser + new/save ───────────────────────────────────
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(new TextBlock { Text = "Palette:", VerticalAlignment = VerticalAlignment.Center });
        _paletteCombo.SelectionChanged += (_, _) =>
        {
            if (_paletteCombo.SelectedItem is ComboItem ci)
            {
                _current  = _palettes.FirstOrDefault(p => p.Name == ci.Id) ?? _current;
                _selected = null;
                RebuildSwatches();
            }
        };
        top.Children.Add(_paletteCombo);
        var newBtn  = Ui.Btn("New…", "Create a new palette");  newBtn.Click  += async (_, _) => await NewPalette();
        var saveBtn = Ui.Btn("💾 Save", "Save this palette to a file"); saveBtn.Click += async (_, _) => await SaveCurrent();
        top.Children.Add(newBtn);
        top.Children.Add(saveBtn);
        root.Children.Add(top);

        // ── Bottom: colour editor ──────────────────────────────────────────────
        var editor = new StackPanel { Spacing = 8, Margin = new(0, 12, 0, 0) };
        DockPanel.SetDock(editor, Dock.Bottom);
        var fields = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        fields.Children.Add(new TextBlock { Text = "Name:", VerticalAlignment = VerticalAlignment.Center });
        fields.Children.Add(_nameBox);
        editor.Children.Add(fields);
        editor.Children.Add(_picker);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var addBtn = Ui.Btn("＋ Add / Update", "Add a new colour, or update the selected one");
        addBtn.Click += (_, _) => AddOrUpdate();
        var delBtn = Ui.Btn("✕ Remove", "Remove the selected colour");
        delBtn.Click += (_, _) => RemoveSelected();
        actions.Children.Add(addBtn);
        actions.Children.Add(delBtn);
        editor.Children.Add(actions);
        root.Children.Add(editor);

        // ── Middle: swatches (fills remaining space) ───────────────────────────
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _swatches,
            Margin  = new(0, 12, 0, 0),
        };
        root.Children.Add(scroll);
        return root;
    }

    // Repopulates the palette dropdown from the loaded set and selects the current one.
    void RefreshPaletteCombo()
    {
        _paletteCombo.Items.Clear();
        foreach (var p in _palettes) _paletteCombo.Items.Add(new ComboItem(p.Name, p.Name));
        _paletteCombo.SelectedItem = _paletteCombo.Items
            .OfType<ComboItem>().FirstOrDefault(c => c.Id == _current.Name);
    }

    // Redraws the swatch grid as small colour chips; details live in a hover tooltip, so even
    // palettes with hundreds of colours stay compact. Clicking a chip loads it into the editor.
    void RebuildSwatches()
    {
        _swatches.Children.Clear();
        foreach (var c in _current.Colors)
        {
            var nc = c;
            var chip = new Button
            {
                Width = 30, Height = 30, Margin = new(3), Padding = new(0),
                Background = SolidOrFallback(nc.Value),
                BorderBrush = Brushes.Gray,
                BorderThickness = new(ReferenceEquals(nc, _selected) ? 3 : 1),  // thicker = selected
            };
            ToolTip.SetTip(chip, $"{nc.Name}\n{nc.Value}");
            chip.Click += (_, _) => SelectColor(nc);
            _swatches.Children.Add(chip);
        }
    }

    // Loads a clicked chip into the name box + picker, and redraws so its selection ring shows.
    void SelectColor(NamedColor nc)
    {
        _selected      = nc;
        _nameBox.Text  = nc.Name;
        try { _picker.Color = Color.Parse(nc.Value); } catch { /* keep current pick */ }
        RebuildSwatches();
    }

    // Adds a new colour, or updates the selected one, from the name box + picker, then redraws.
    void AddOrUpdate()
    {
        var name = string.IsNullOrWhiteSpace(_nameBox.Text) ? "Colour" : _nameBox.Text.Trim();
        var hex  = HexOf(_picker.Color);

        if (_selected is not null && _current.Colors.Contains(_selected))
        {
            _selected.Name = name; _selected.Value = hex;
        }
        else
        {
            var nc = new NamedColor(name, hex);
            _current.Colors.Add(nc);
            _selected = nc;
        }
        RebuildSwatches();
    }

    // Removes the selected colour from the current palette (if any) and redraws.
    void RemoveSelected()
    {
        if (_selected is not null) _current.Colors.Remove(_selected);
        _selected = null;
        RebuildSwatches();
    }

    // Prompts for a name, creates an empty palette, makes it current and saves it.
    async Task NewPalette()
    {
        var name = await PromptDialog.Show(this, "New palette name:", "My Palette", "New palette");
        if (string.IsNullOrWhiteSpace(name)) return;

        _current = new ColorPalette { Name = name.Trim() };
        _palettes.Add(_current);
        _selected = null;
        PaletteStore.Save(_current);
        RefreshPaletteCombo();
        RebuildSwatches();
    }

    // Writes the current palette to disk and confirms where it landed.
    async Task SaveCurrent()
    {
        var path = PaletteStore.Save(_current);
        await MessageDialog.Show(this, $"Saved palette \"{_current.Name}\" to:\n{path}", "Palette saved");
    }

    // ── Colour helpers ─────────────────────────────────────────────────────────

    // Formats an Avalonia colour as opaque web hex (#RRGGBB) for storage.
    static string HexOf(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // A solid brush for a swatch background, falling back to transparent on a bad hex value.
    static IBrush SolidOrFallback(string hex)
    {
        try { return new SolidColorBrush(Color.Parse(hex)); } catch { return Brushes.Transparent; }
    }
}
