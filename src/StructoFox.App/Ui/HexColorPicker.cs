using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using StructoFox.Core;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// A self-contained colour picker offering three equivalent ways to choose a colour: a one-click
/// palette strip (CI colours), RGB sliders + hex, and CMYK fields (for print/CI accuracy). All views
/// stay in sync. Theme-independent and dependency-free. Exposes <see cref="Color"/> + <see cref="ColorChanged"/>.
/// NOTE: CMYK↔RGB uses the standard (profile-less) conversion — approximate, not ICC-calibrated.
/// </summary>
public class HexColorPicker : StackPanel
{
    readonly Border  _preview = new() { Height = 26, CornerRadius = new(3), BorderBrush = Brushes.Gray, BorderThickness = new(1) };
    readonly Slider  _r = Channel();
    readonly Slider  _g = Channel();
    readonly Slider  _b = Channel();
    readonly TextBox _hex = new() { Width = 100 };
    readonly TextBox _c = Field(), _m = Field(), _y = Field(), _k = Field();
    bool _updating;

    /// <summary>Raised whenever the chosen colour changes (palette, slider, hex or CMYK edit).</summary>
    public event EventHandler? ColorChanged;

    // Builds the preview, optional palette strip, RGB sliders, hex field and CMYK row, all wired in sync.
    public HexColorPicker(bool showPalette = true)
    {
        Spacing = 6;
        Children.Add(_preview);
        if (showPalette) Children.Add(BuildPaletteStrip());
        Children.Add(ChannelRow("R", _r));
        Children.Add(ChannelRow("G", _g));
        Children.Add(ChannelRow("B", _b));
        Children.Add(LabeledRow("Hex", _hex));
        Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children =
            {
                new TextBlock { Text = "CMYK %", VerticalAlignment = VerticalAlignment.Center },
                _c, _m, _y, _k,
            },
        });

        foreach (var s in new[] { _r, _g, _b })
            s.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) Commit(FromSliders()); };
        WireText(_hex, () => { try { return Color.Parse(_hex.Text ?? ""); } catch { return (Color?)null; } });
        foreach (var f in new[] { _c, _m, _y, _k }) WireText(f, () => FromCmyk());

        Color = Colors.Black;
    }

    /// <summary>The selected colour. Setting it updates every view (sliders, hex, CMYK, preview).</summary>
    public Color Color
    {
        get => FromSliders();
        set => ShowColor(value);
    }

    // Pushes a colour into every input/display at once, guarded so the sync doesn't recurse.
    void ShowColor(Color col)
    {
        _updating = true;
        _r.Value = col.R; _g.Value = col.G; _b.Value = col.B;
        _hex.Text = HexOf(col);
        var (c, m, y, k) = RgbToCmyk(col);
        _c.Text = Pct(c); _m.Text = Pct(m); _y.Text = Pct(y); _k.Text = Pct(k);
        _preview.Background = new SolidColorBrush(col);
        _updating = false;
    }

    // Adopts a new colour from one of the inputs: refresh all views and notify listeners.
    void Commit(Color col)
    {
        if (_updating) return;
        ShowColor(col);
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    // Hooks a text field to commit its parsed colour on Enter or focus-loss (ignoring invalid input).
    void WireText(TextBox box, Func<Color?> parse)
    {
        void Try() { if (!_updating && parse() is { } col) Commit(col); }
        box.LostFocus += (_, _) => Try();
        box.KeyDown   += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Try(); };
    }

    // The colour currently described by the RGB sliders (the canonical source).
    Color FromSliders() => Color.FromRgb((byte)_r.Value, (byte)_g.Value, (byte)_b.Value);

    // The colour described by the four CMYK fields, or null if they don't parse.
    Color? FromCmyk()
    {
        if (!(TryPct(_c, out var c) && TryPct(_m, out var m) && TryPct(_y, out var y) && TryPct(_k, out var k)))
            return null;
        return CmykToRgb(c, m, y, k);
    }

    // Builds a one-click palette strip (CI colours) from the active palette; chips set the colour.
    Control BuildPaletteStrip()
    {
        var pal   = PaletteStore.LoadAll().FirstOrDefault() ?? PaletteService.BuiltIn();
        var strip = new WrapPanel { MaxWidth = 250 };
        foreach (var nc in pal.Colors)
        {
            var hex  = nc.Value;
            var chip = new Button { Width = 20, Height = 20, Margin = new(2), Padding = new(0), BorderBrush = Brushes.Gray, BorderThickness = new(1) };
            try { chip.Background = new SolidColorBrush(Color.Parse(hex)); } catch { chip.Background = Brushes.Transparent; }
            ToolTip.SetTip(chip, $"{nc.Name}\n{hex}");
            chip.Click += (_, _) => { try { Commit(Color.Parse(hex)); } catch { } };
            strip.Children.Add(chip);
        }
        return strip;
    }

    // ── conversion + small helpers ───────────────────────────────────────────

    /// <summary>Standard (profile-less) RGB→CMYK: K from the brightest channel, then C/M/Y relative to it.</summary>
    public static (double c, double m, double y, double k) RgbToCmyk(Color col)
    {
        double r = col.R / 255.0, g = col.G / 255.0, b = col.B / 255.0;
        double k = 1 - Math.Max(r, Math.Max(g, b));
        if (k >= 1) return (0, 0, 0, 1);   // pure black — undefined C/M/Y, treat as zero
        return ((1 - r - k) / (1 - k), (1 - g - k) / (1 - k), (1 - b - k) / (1 - k), k);
    }

    // Standard (profile-less) CMYK→RGB; inputs are 0..1.
    static Color CmykToRgb(double c, double m, double y, double k) => Color.FromRgb(
        (byte)Math.Round(255 * (1 - c) * (1 - k)),
        (byte)Math.Round(255 * (1 - m) * (1 - k)),
        (byte)Math.Round(255 * (1 - y) * (1 - k)));

    // A 0–255 channel slider.
    static Slider Channel() => new() { Minimum = 0, Maximum = 255, Width = 150, SmallChange = 1, LargeChange = 16 };

    // A narrow numeric field for a CMYK percentage.
    static TextBox Field() => new() { Width = 46 };

    // A labelled row (fixed-width letter + control).
    static Control ChannelRow(string label, Control control) => LabeledRow(label, control);
    static Control LabeledRow(string label, Control control) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 8,
        Children = { new TextBlock { Text = label, Width = 32, VerticalAlignment = VerticalAlignment.Center }, control },
    };

    // Parses a CMYK percentage field (0..100) into a 0..1 fraction; false if it isn't a number.
    static bool TryPct(TextBox box, out double frac)
    {
        frac = 0;
        if (!double.TryParse(box.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var p)) return false;
        frac = Math.Clamp(p, 0, 100) / 100.0;
        return true;
    }

    // Formats a 0..1 fraction as a whole-percent string.
    static string Pct(double frac) => Math.Round(frac * 100).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Formats a colour as opaque web hex (#RRGGBB).</summary>
    public static string HexOf(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
