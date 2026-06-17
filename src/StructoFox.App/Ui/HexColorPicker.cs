using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using StructoFox.Core;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// A small, self-contained colour picker: a live preview plus R/G/B sliders and a hex field, all
/// kept in sync. Theme-independent and dependency-free — built because the external ColorPicker
/// control didn't render reliably. Exposes <see cref="Color"/> and raises <see cref="ColorChanged"/>.
/// </summary>
public class HexColorPicker : StackPanel
{
    readonly Border  _preview = new() { Height = 26, CornerRadius = new(3), BorderBrush = Brushes.Gray, BorderThickness = new(1) };
    readonly Slider  _r = Channel();
    readonly Slider  _g = Channel();
    readonly Slider  _b = Channel();
    readonly TextBox _hex = new() { Width = 100 };
    bool _updating;

    /// <summary>Raised whenever the chosen colour changes (slider drag or hex edit).</summary>
    public event EventHandler? ColorChanged;

    // Assembles the preview, an optional palette strip, the channel rows and the hex field.
    // Pass showPalette: false where a palette strip would be redundant (e.g. the palette editor).
    public HexColorPicker(bool showPalette = true)
    {
        Spacing = 6;
        Children.Add(_preview);
        if (showPalette) Children.Add(BuildPaletteStrip());
        Children.Add(ChannelRow("R", _r));
        Children.Add(ChannelRow("G", _g));
        Children.Add(ChannelRow("B", _b));
        Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { new TextBlock { Text = "Hex", Width = 18, VerticalAlignment = VerticalAlignment.Center }, _hex },
        });

        foreach (var s in new[] { _r, _g, _b })
            s.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) OnSliders(); };
        _hex.LostFocus += (_, _) => OnHex();
        _hex.KeyDown   += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) OnHex(); };

        Color = Colors.Black;
    }

    /// <summary>The currently selected colour. Setting it moves the sliders and hex field to match.</summary>
    public Color Color
    {
        get => Color.FromRgb((byte)_r.Value, (byte)_g.Value, (byte)_b.Value);
        set
        {
            _updating = true;
            _r.Value = value.R; _g.Value = value.G; _b.Value = value.B;
            _hex.Text = HexOf(value);
            Repaint(value);
            _updating = false;
        }
    }

    // Reacts to a slider move: refresh the hex text + preview and notify listeners.
    void OnSliders()
    {
        if (_updating) return;
        var c = Color;
        _updating = true; _hex.Text = HexOf(c); _updating = false;
        Repaint(c);
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    // Reacts to a hex edit: parse it, move the sliders to match, and notify listeners.
    void OnHex()
    {
        if (_updating) return;
        try
        {
            var c = Color.Parse(_hex.Text ?? "");
            _updating = true; _r.Value = c.R; _g.Value = c.G; _b.Value = c.B; _updating = false;
            Repaint(c);
            ColorChanged?.Invoke(this, EventArgs.Empty);
        }
        catch { /* ignore an incomplete/invalid hex while typing */ }
    }

    // Builds a one-click palette strip (CI colours) from the active palette; chips set the colour.
    Control BuildPaletteStrip()
    {
        var pal   = PaletteStore.LoadAll().FirstOrDefault() ?? PaletteService.BuiltIn();
        var strip = new WrapPanel { MaxWidth = 230 };
        foreach (var nc in pal.Colors)
        {
            var hex  = nc.Value;
            var chip = new Button { Width = 20, Height = 20, Margin = new(2), Padding = new(0), BorderBrush = Brushes.Gray, BorderThickness = new(1) };
            try { chip.Background = new SolidColorBrush(Color.Parse(hex)); } catch { chip.Background = Brushes.Transparent; }
            ToolTip.SetTip(chip, $"{nc.Name}\n{hex}");
            chip.Click += (_, _) => { try { Color = Color.Parse(hex); ColorChanged?.Invoke(this, EventArgs.Empty); } catch { } };
            strip.Children.Add(chip);
        }
        return strip;
    }

    // Paints the preview swatch with the given colour.
    void Repaint(Color c) => _preview.Background = new SolidColorBrush(c);

    // Builds one 0–255 channel slider.
    static Slider Channel() => new() { Minimum = 0, Maximum = 255, Width = 200, SmallChange = 1, LargeChange = 16 };

    // Lays out a labelled channel row (letter + slider).
    static Control ChannelRow(string label, Slider slider) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 8,
        Children = { new TextBlock { Text = label, Width = 18, VerticalAlignment = VerticalAlignment.Center }, slider },
    };

    // Formats a colour as opaque web hex (#RRGGBB).
    static string HexOf(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
