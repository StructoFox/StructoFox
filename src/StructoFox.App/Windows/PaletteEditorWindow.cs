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
    readonly TextBox     _nameBox      = new() { PlaceholderText = Loc.S("Pal_ColorName"), MinWidth = 160 };
    HexColorPicker _picker = null!;   // assigned in BuildContent (needs the leading column)

    // Loads the saved palettes (seeding if needed) and builds the editor around the first one.
    public PaletteEditorWindow()
    {
        Title                 = Loc.S("Pal_Title");
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
        top.Children.Add(new TextBlock { Text = Loc.S("Pal_Label"), VerticalAlignment = VerticalAlignment.Center });
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
        var newBtn  = Ui.Btn(Loc.S("Pal_New"), Loc.S("Pal_NewTip"));  newBtn.Click  += async (_, _) => await NewPalette();
        var saveBtn = Ui.Btn(Loc.S("Pal_Save"), Loc.S("Pal_SaveTip")); saveBtn.Click += async (_, _) => await SaveCurrent();
        top.Children.Add(newBtn);
        top.Children.Add(saveBtn);
        root.Children.Add(top);

        // ── Bottom: colour editor — wide 3-column picker (name+buttons | picker | preview) ──
        var addBtn = Ui.Btn(Loc.S("Pal_AddUpdate"), Loc.S("Pal_AddUpdateTip"));
        addBtn.Click += (_, _) => AddOrUpdate();
        var delBtn = Ui.Btn(Loc.S("Pal_Remove"), Loc.S("Pal_RemoveTip"));
        delBtn.Click += (_, _) => RemoveSelected();

        var lead = new StackPanel
        {
            Width = 170, Spacing = 8,
            Children = { new TextBlock { Text = Loc.S("Pal_Name") }, _nameBox, addBtn, delBtn },
        };
        _picker = new HexColorPicker(showPalette: false, leadingColumn: lead) { Color = Colors.SteelBlue };
        _picker.Margin = new(0, 12, 0, 0);
        DockPanel.SetDock(_picker, Dock.Bottom);
        root.Children.Add(_picker);

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
            _swatches.Children.Add(Ui.ColorChip(
                nc.Value, $"{nc.Name}\n{nc.Value}", () => SelectColor(nc),
                selected: ReferenceEquals(nc, _selected), size: 30));
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
        var name = string.IsNullOrWhiteSpace(_nameBox.Text) ? Loc.S("Pal_DefaultColor") : _nameBox.Text.Trim();
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
        var name = await PromptDialog.Show(this, Loc.S("Pal_NewPrompt"), Loc.S("Pal_NewDefault"), Loc.S("Pal_NewTitle"));
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
        await MessageDialog.Show(this, string.Format(Loc.S("Pal_Saved"), _current.Name, path), Loc.S("Pal_SavedTitle"));
    }

    // ── Colour helpers ─────────────────────────────────────────────────────────

    // Formats an Avalonia colour as opaque web hex (#RRGGBB) for storage.
    static string HexOf(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
