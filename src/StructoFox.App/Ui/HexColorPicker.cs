using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using StructoFox.Core;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// A self-contained colour picker. Visual scheme = an HSV saturation/value box + a hue bar (the
/// most practical, precise-everywhere layout), backed by exact RGB sliders, hex, and CMYK fields,
/// plus a one-click palette strip (CI colours). All views stay in sync. Theme-independent, no
/// external dependency. NOTE: CMYK↔RGB is the standard profile-less approximation (not ICC).
/// </summary>
public class HexColorPicker : StackPanel
{
    const double BoxW = 200, BoxH = 130, BarH = 16;

    readonly Border  _preview  = new() { Height = 24, CornerRadius = new(3), BorderBrush = Brushes.Gray, BorderThickness = new(1) };
    readonly Grid    _svBox    = new() { Width = BoxW, Height = BoxH, Background = Brushes.Transparent };
    readonly Border  _hueFill  = new() { IsHitTestVisible = false };          // solid current hue (bottom layer)
    readonly Border  _hueBar   = new() { Width = BoxW, Height = BarH, BorderBrush = Brushes.Gray, BorderThickness = new(1) };
    readonly Slider  _r = Channel();
    readonly Slider  _g = Channel();
    readonly Slider  _b = Channel();
    readonly TextBox _hex = new() { Width = 100 };
    readonly TextBlock _rgbText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Opacity = 0.8 };
    readonly TextBox _c = Field(), _m = Field(), _y = Field(), _k = Field();

    readonly StackPanel _paletteArea = new() { Spacing = 4 };
    double _h, _s = 1, _v;     // current HSV (the SV box / hue bar drive these)
    bool _updating, _svDrag, _hueDrag;

    /// <summary>Raised whenever the chosen colour changes (any input).</summary>
    public event EventHandler? ColorChanged;

    // Builds preview, HSV box + hue bar, optional palette strip, RGB sliders, hex and CMYK — all synced.
    public HexColorPicker(bool showPalette = true)
    {
        Spacing = 6;
        Children.Add(_preview);
        Children.Add(BuildSvBox());
        Children.Add(BuildHueBar());
        if (showPalette) { Children.Add(_paletteArea); RebuildPaletteArea(); }
        Children.Add(ChannelRow("R", _r));
        Children.Add(ChannelRow("G", _g));
        Children.Add(ChannelRow("B", _b));
        Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { new TextBlock { Text = "Hex", Width = 32, VerticalAlignment = VerticalAlignment.Center }, _hex, _rgbText },
        });
        Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { new TextBlock { Text = "CMYK %", VerticalAlignment = VerticalAlignment.Center }, _c, _m, _y, _k },
        });

        foreach (var s in new[] { _r, _g, _b })
            s.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) Commit(FromSliders()); };
        WireText(_hex, () => { try { return Color.Parse(_hex.Text ?? ""); } catch { return (Color?)null; } });
        foreach (var f in new[] { _c, _m, _y, _k }) WireText(f, FromCmyk);

        Color = Colors.Black;
    }

    /// <summary>The selected colour. Setting it updates every view.</summary>
    public Color Color
    {
        get => FromSliders();
        set => ShowColor(value);
    }

    // Pushes a colour into every input/display at once (and recomputes HSV), guarded against recursion.
    void ShowColor(Color col)
    {
        _updating = true;
        _r.Value = col.R; _g.Value = col.G; _b.Value = col.B;
        _hex.Text = HexOf(col);
        _rgbText.Text = $"{col.R}, {col.G}, {col.B}";
        var (cc, mm, yy, kk) = RgbToCmyk(col);
        _c.Text = Pct(cc); _m.Text = Pct(mm); _y.Text = Pct(yy); _k.Text = Pct(kk);
        (_h, _s, _v) = RgbToHsv(col);
        _hueFill.Background = new SolidColorBrush(HsvToRgb(_h, 1, 1));   // SV box base = full-sat hue
        _preview.Background = new SolidColorBrush(col);
        _updating = false;
    }

    // Adopts a colour from one of the inputs: refresh all views and notify listeners.
    void Commit(Color col)
    {
        if (_updating) return;
        ShowColor(col);
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── visual HSV controls ────────────────────────────────────────────────

    // The saturation/value box: solid hue, then a white→transparent (S) and a transparent→black (V) overlay.
    Control BuildSvBox()
    {
        var white = new Border
        {
            IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new(0, 0, RelativeUnit.Relative), EndPoint = new(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) },
            },
        };
        var black = new Border
        {
            IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new(0, 0, RelativeUnit.Relative), EndPoint = new(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0), new GradientStop(Colors.Black, 1) },
            },
        };
        _svBox.Children.Add(_hueFill);
        _svBox.Children.Add(white);
        _svBox.Children.Add(black);

        void Set(Point p)
        {
            _s = Math.Clamp(p.X / BoxW, 0, 1);
            _v = Math.Clamp(1 - p.Y / BoxH, 0, 1);
            Commit(HsvToRgb(_h, _s, _v));
        }
        _svBox.PointerPressed  += (_, e) => { _svDrag = true; e.Pointer.Capture(_svBox); Set(e.GetPosition(_svBox)); };
        _svBox.PointerMoved    += (_, e) => { if (_svDrag) Set(e.GetPosition(_svBox)); };
        _svBox.PointerReleased += (_, e) => { _svDrag = false; e.Pointer.Capture(null); };
        return _svBox;
    }

    // The hue bar: a full rainbow gradient; clicking/dragging picks the hue.
    Control BuildHueBar()
    {
        _hueBar.Background = new LinearGradientBrush
        {
            StartPoint = new(0, 0, RelativeUnit.Relative), EndPoint = new(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new(Color.FromRgb(255, 0, 0), 0.0), new(Color.FromRgb(255, 255, 0), 1.0 / 6),
                new(Color.FromRgb(0, 255, 0), 2.0 / 6), new(Color.FromRgb(0, 255, 255), 3.0 / 6),
                new(Color.FromRgb(0, 0, 255), 4.0 / 6), new(Color.FromRgb(255, 0, 255), 5.0 / 6),
                new(Color.FromRgb(255, 0, 0), 1.0),
            },
        };
        void Set(Point p) { _h = Math.Clamp(p.X / BoxW, 0, 1) * 360; Commit(HsvToRgb(_h, _s, _v)); }
        _hueBar.PointerPressed  += (_, e) => { _hueDrag = true; e.Pointer.Capture(_hueBar); Set(e.GetPosition(_hueBar)); };
        _hueBar.PointerMoved    += (_, e) => { if (_hueDrag) Set(e.GetPosition(_hueBar)); };
        _hueBar.PointerReleased += (_, e) => { _hueDrag = false; e.Pointer.Capture(null); };
        return _hueBar;
    }

    // Hooks a text field to commit its parsed colour on Enter or focus-loss (ignoring invalid input).
    void WireText(TextBox box, Func<Color?> parse)
    {
        void Try() { if (!_updating && parse() is { } col) Commit(col); }
        box.LostFocus += (_, _) => Try();
        box.KeyDown   += (_, e) => { if (e.Key == Key.Enter) Try(); };
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

    // (Re)builds the palette area: a header with the active palette's name + a chooser button,
    // then a one-click strip of that palette's colours (CI colours). Chips set the colour.
    void RebuildPaletteArea()
    {
        _paletteArea.Children.Clear();
        var pal = PaletteStore.Active();

        var chooser = new Button { Content = "▾", Padding = new(6, 0), MinHeight = 22 };
        ToolTip.SetTip(chooser, Loc.S("Palette_Choose"));
        chooser.Click += (_, _) =>
        {
            var cm = new ContextMenu();
            foreach (var p in PaletteStore.LoadAll())
            {
                var name = p.Name;
                var mi = new MenuItem { Header = name };
                mi.Click += (_, _) => { PaletteStore.ActiveName = name; RebuildPaletteArea(); };
                cm.Items.Add(mi);
            }
            cm.Open(chooser);
        };
        _paletteArea.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children =
            {
                new TextBlock { Text = pal.Name, FontSize = 11, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center },
                chooser,
            },
        });

        var strip = new WrapPanel { MaxWidth = BoxW + 16 };
        foreach (var nc in pal.Colors)
        {
            var hex  = nc.Value;
            var chip = new Button { Width = 20, Height = 20, Margin = new(2), Padding = new(0), BorderBrush = Brushes.Gray, BorderThickness = new(1) };
            try { chip.Background = new SolidColorBrush(Color.Parse(hex)); } catch { chip.Background = Brushes.Transparent; }
            ToolTip.SetTip(chip, $"{nc.Name}\n{hex}");
            chip.Click += (_, _) => { try { Commit(Color.Parse(hex)); } catch { } };
            strip.Children.Add(chip);
        }
        _paletteArea.Children.Add(strip);
    }

    // ── colour-space conversions ─────────────────────────────────────────────

    /// <summary>Standard (profile-less) RGB→CMYK: K from the brightest channel, then C/M/Y relative to it.</summary>
    public static (double c, double m, double y, double k) RgbToCmyk(Color col)
    {
        double r = col.R / 255.0, g = col.G / 255.0, b = col.B / 255.0;
        double k = 1 - Math.Max(r, Math.Max(g, b));
        if (k >= 1) return (0, 0, 0, 1);
        return ((1 - r - k) / (1 - k), (1 - g - k) / (1 - k), (1 - b - k) / (1 - k), k);
    }

    // Standard (profile-less) CMYK→RGB; inputs are 0..1.
    static Color CmykToRgb(double c, double m, double y, double k) => Color.FromRgb(
        (byte)Math.Round(255 * (1 - c) * (1 - k)),
        (byte)Math.Round(255 * (1 - m) * (1 - k)),
        (byte)Math.Round(255 * (1 - y) * (1 - k)));

    // RGB→HSV (h in degrees, s/v in 0..1).
    static (double h, double s, double v) RgbToHsv(Color col)
    {
        double r = col.R / 255.0, g = col.G / 255.0, b = col.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d != 0)
        {
            if (max == r)      h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * (((b - r) / d) + 2);
            else               h = 60 * (((r - g) / d) + 4);
        }
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    // HSV→RGB (h in degrees, s/v in 0..1).
    static Color HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
        double r = 0, g = 0, b = 0;
        switch ((int)(h / 60))
        {
            case 0: r = c; g = x; break;
            case 1: r = x; g = c; break;
            case 2: g = c; b = x; break;
            case 3: g = x; b = c; break;
            case 4: r = x; b = c; break;
            default: r = c; b = x; break;
        }
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    // ── small helpers ────────────────────────────────────────────────────────

    static Slider Channel() => new() { Minimum = 0, Maximum = 255, Width = 150, SmallChange = 1, LargeChange = 16 };
    static TextBox Field() => new() { Width = 46 };

    static Control ChannelRow(string label, Control control) => LabeledRow(label, control);
    static Control LabeledRow(string label, Control control) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 8,
        Children = { new TextBlock { Text = label, Width = 32, VerticalAlignment = VerticalAlignment.Center }, control },
    };

    static bool TryPct(TextBox box, out double frac)
    {
        frac = 0;
        if (!double.TryParse(box.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var p)) return false;
        frac = Math.Clamp(p, 0, 100) / 100.0;
        return true;
    }

    static string Pct(double frac) => Math.Round(frac * 100).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Formats a colour as opaque web hex (#RRGGBB).</summary>
    public static string HexOf(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
