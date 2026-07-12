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
/// A self-contained colour picker. Visual scheme = an HSV saturation/value box + a hue bar
/// (click/drag), backed by a tidy numeric grid: per-channel RGB as decimal (0–255) AND hex (00–FF),
/// plus CMYK percentages — and a one-click palette strip (CI colours) with a palette chooser.
/// All views stay in sync. Theme-independent, no external dependency.
/// NOTE: CMYK↔RGB is the standard profile-less approximation (not ICC).
/// </summary>
public class HexColorPicker : StackPanel
{
    const double BoxW = 200, BoxH = 130, BarH = 16;

    readonly Border _preview = new() { Height = 24, CornerRadius = new(3), BorderBrush = Brushes.Gray, BorderThickness = new(1) };
    readonly Grid   _svBox   = new() { Width = BoxW, Height = BoxH, Background = Brushes.Transparent };
    readonly Border _hueFill = new() { IsHitTestVisible = false };
    readonly Border _hueBar  = new() { Width = BoxW, Height = BarH, BorderBrush = Brushes.Gray, BorderThickness = new(1) };

    // Per-channel numeric inputs: decimal (0–255) and hex (00–FF). Alpha (A) drives transparency.
    readonly TextBox _rDec = Dec(), _gDec = Dec(), _bDec = Dec(), _aDec = Dec();
    readonly TextBox _rHex = Hex2(), _gHex = Hex2(), _bHex = Hex2(), _aHex = Hex2();
    readonly TextBox _c = Pctf(), _m = Pctf(), _y = Pctf(), _k = Pctf();
    byte _alpha = 255;

    readonly StackPanel _paletteArea = new() { Spacing = 4 };
    Color  _color = Colors.Black;
    double _h, _s = 1, _v;
    bool _updating, _svDrag, _hueDrag;

    /// <summary>Raised whenever the chosen colour changes (any input).</summary>
    public event EventHandler? ColorChanged;

    // Builds the picker. Default = vertical stack. When a leadingColumn is given, uses a wide layout:
    // a 3-column top row [leading | HSV box+hue | preview], a divider, then the RGB and CMYK rows.
    public HexColorPicker(bool showPalette = true, Control? leadingColumn = null)
    {
        Spacing = 6;

        if (leadingColumn is not null)
        {
            _preview.Width = 90; _preview.Height = double.NaN; _preview.VerticalAlignment = VerticalAlignment.Stretch;
            var visual = new StackPanel { Spacing = 6, Children = { BuildSvBox(), BuildHueBar() } };
            Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 12,
                Children = { leadingColumn, visual, _preview },
            });
            if (showPalette) { Children.Add(_paletteArea); RebuildPaletteArea(); }
            Children.Add(Sep());
            Children.Add(NumericArea());
        }
        else
        {
            Children.Add(_preview);
            Children.Add(BuildSvBox());
            Children.Add(BuildHueBar());
            if (showPalette) { Children.Add(_paletteArea); RebuildPaletteArea(); }
            Children.Add(NumericArea());
        }

        WireText(_rDec, FromDec); WireText(_gDec, FromDec); WireText(_bDec, FromDec); WireText(_aDec, FromDec);
        WireText(_rHex, FromHex); WireText(_gHex, FromHex); WireText(_bHex, FromHex); WireText(_aHex, FromHex);
        foreach (var f in new[] { _c, _m, _y, _k }) WireText(f, FromCmyk);

        Color = Colors.Black;
    }

    /// <summary>The selected colour. Setting it updates every view.</summary>
    public Color Color
    {
        get => _color;
        set => ShowColor(value);
    }

    // Pushes a colour into every input/display at once (RGB dec+hex, CMYK, HSV, preview), guarded.
    void ShowColor(Color col)
    {
        _updating = true;
        _color = col;
        _alpha = col.A;
        _rDec.Text = col.R.ToString(); _gDec.Text = col.G.ToString(); _bDec.Text = col.B.ToString(); _aDec.Text = col.A.ToString();
        _rHex.Text = col.R.ToString("X2"); _gHex.Text = col.G.ToString("X2"); _bHex.Text = col.B.ToString("X2"); _aHex.Text = col.A.ToString("X2");
        var (cc, mm, yy, kk) = RgbToCmyk(col);
        _c.Text = Pct(cc); _m.Text = Pct(mm); _y.Text = Pct(yy); _k.Text = Pct(kk);
        (_h, _s, _v) = RgbToHsv(col);
        _hueFill.Background = new SolidColorBrush(HsvToRgb(_h, 1, 1));
        _preview.Background = new SolidColorBrush(col);
        _updating = false;
    }

    // Adopts a colour from one of the inputs: refresh all views and notify listeners.
    void Commit(Color col)
    {
        if (_updating) return;
        ShowColor(Color.FromArgb(_alpha, col.R, col.G, col.B));   // keep the chosen alpha through hue/SV/palette picks
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── visual HSV controls ────────────────────────────────────────────────

    // The saturation/value box: solid hue, then white→transparent (S) and transparent→black (V) overlays.
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

    // (Re)builds the palette area: active palette name + ▾ chooser, then a one-click colour strip.
    void RebuildPaletteArea()
    {
        _paletteArea.Children.Clear();
        var pal = PaletteStore.Active();

        var chooser = new Button { Content = "▾", Padding = new(6, 0), MinHeight = 22 };
        ToolTip.SetTip(chooser, Loc.S("Palette_Choose"));
        Ui.Theme(chooser, TemplatedControl.BackgroundProperty, "ControlBgBrush");
        Ui.Theme(chooser, TemplatedControl.ForegroundProperty, "SidebarTextBrush");
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

        // Bind the name label to the theme's text colour so it stays readable on dark backgrounds.
        var nameLabel = new TextBlock { Text = pal.Name, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Ui.Theme(nameLabel, TextBlock.ForegroundProperty, "ContentTextBrush");

        _paletteArea.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { nameLabel, chooser },
        });

        var strip = new WrapPanel { MaxWidth = BoxW + 16 };
        foreach (var nc in pal.Colors)
        {
            var hex = nc.Value;
            strip.Children.Add(Ui.ColorChip(hex, $"{nc.Name}\n{hex}", () => { try { Commit(Color.Parse(hex)); } catch { } }, size: 22));
        }
        // Cap the height and scroll, so a large palette doesn't push the numeric fields off-window.
        _paletteArea.Children.Add(new ScrollViewer
        {
            MaxHeight = 96,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            Content = strip,
        });
    }

    // Hooks a text field to commit its parsed colour on Enter or focus-loss (ignoring invalid input).
    void WireText(TextBox box, Func<Color?> parse)
    {
        void Try() { if (!_updating && parse() is { } col) Commit(col); }
        box.LostFocus += (_, _) => Try();
        box.KeyDown   += (_, e) => { if (e.Key == Key.Enter) Try(); };
    }

    // The colour described by the decimal (0–255) channel fields incl. alpha, or null if any don't parse.
    Color? FromDec()
    {
        if (TryByteDec(_rDec, out var r) && TryByteDec(_gDec, out var g) && TryByteDec(_bDec, out var b) && TryByteDec(_aDec, out var a))
        { _alpha = a; return Color.FromArgb(a, r, g, b); }
        return null;
    }

    // The colour described by the hex (00–FF) channel fields incl. alpha, or null if any don't parse.
    Color? FromHex()
    {
        if (TryByteHex(_rHex, out var r) && TryByteHex(_gHex, out var g) && TryByteHex(_bHex, out var b) && TryByteHex(_aHex, out var a))
        { _alpha = a; return Color.FromArgb(a, r, g, b); }
        return null;
    }

    // The colour described by the four CMYK fields, or null if they don't parse.
    Color? FromCmyk()
    {
        if (TryPct(_c, out var c) && TryPct(_m, out var m) && TryPct(_y, out var y) && TryPct(_k, out var k))
            return CmykToRgb(c, m, y, k);
        return null;
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

    static Color CmykToRgb(double c, double m, double y, double k) => Color.FromRgb(
        (byte)Math.Round(255 * (1 - c) * (1 - k)),
        (byte)Math.Round(255 * (1 - m) * (1 - k)),
        (byte)Math.Round(255 * (1 - y) * (1 - k)));

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

    static TextBox Dec()  => new() { Width = 46 };
    static TextBox Hex2() => new() { Width = 40 };
    static TextBox Pctf() => new() { Width = 56 };

    // The numeric area: RGB stacked vertically (each dec+hex) on the left, CMYK as a 2×2 grid on
    // the right — R/G/B | C M / Y K — separated by a thin divider.
    Control NumericArea()
    {
        var rgb = new StackPanel
        {
            Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
            Children = { ChannelGroup("R", _rDec, _rHex), ChannelGroup("G", _gDec, _gHex), ChannelGroup("B", _bDec, _bHex), ChannelGroup("A", _aDec, _aHex) },
        };

        var cmyk = new Grid { ColumnSpacing = 10, RowSpacing = 4, VerticalAlignment = VerticalAlignment.Center };
        cmyk.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        cmyk.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        cmyk.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        cmyk.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        void Place(Control c, int row, int col) { Grid.SetRow(c, row); Grid.SetColumn(c, col); cmyk.Children.Add(c); }
        Place(PctGroup("C", _c), 0, 0); Place(PctGroup("M", _m), 0, 1);
        Place(PctGroup("Y", _y), 1, 0); Place(PctGroup("K", _k), 1, 1);

        var sep = new Border { Width = 1, Background = Brushes.Gray, Opacity = 0.4, Margin = new(10, 0, 10, 0) };
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { rgb, sep, cmyk } };
    }

    // A thin horizontal divider between the picker's top area and the numeric rows.
    static Control Sep() => new Border { Height = 1, Background = Brushes.Gray, Opacity = 0.4, Margin = new(0, 4, 0, 4) };

    // "R: [dec] [hex]" group. The label has a fixed width so dec/hex fields line up across rows.
    static Control ChannelGroup(string label, TextBox dec, TextBox hex) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 4,
        Children = { Lbl(label), dec, hex },
    };

    // "C: [%] %" group. The label has a fixed width so the % fields line up across the 2×2 grid.
    static Control PctGroup(string label, TextBox pct)
    {
        var pc = new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center };
        Ui.Theme(pc, TextBlock.ForegroundProperty, "ContentTextBrush");
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { Lbl(label), pct, pc } };
    }

    // A fixed-width "<X>:" label, so every field that follows snaps to the same vertical line. Theme-coloured so
    // it stays readable on any background (never white-on-white).
    static TextBlock Lbl(string label)
    {
        var tb = new TextBlock { Text = label + ":", Width = 18, VerticalAlignment = VerticalAlignment.Center };
        Ui.Theme(tb, TextBlock.ForegroundProperty, "ContentTextBrush");
        return tb;
    }

    // Parses a 0–255 decimal field (clamped) into a byte; false if it isn't a number.
    static bool TryByteDec(TextBox box, out byte value)
    {
        value = 0;
        if (!int.TryParse(box.Text, out var n)) return false;
        value = (byte)Math.Clamp(n, 0, 255);
        return true;
    }

    // Parses a 00–FF hex field into a byte; false if it isn't valid hex.
    static bool TryByteHex(TextBox box, out byte value)
    {
        value = 0;
        try { value = Convert.ToByte((box.Text ?? "").Trim().TrimStart('#'), 16); return true; }
        catch { return false; }
    }

    // Parses a CMYK percentage field (0..100, one decimal allowed) into a 0..1 fraction.
    static bool TryPct(TextBox box, out double frac)
    {
        frac = 0;
        if (!double.TryParse(box.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var p)) return false;
        frac = Math.Clamp(p, 0, 100) / 100.0;
        return true;
    }

    // One-decimal percent label for a 0..1 fraction.
    static string Pct(double frac) => (frac * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Formats a colour as opaque web hex (#RRGGBB).</summary>
    public static string HexOf(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Formats a colour as web hex, including the alpha channel (#AARRGGBB) when not fully
    /// opaque — so transparency survives a round-trip through Color.Parse.</summary>
    public static string HexOfRgba(Color c) => c.A == 255 ? HexOf(c) : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
}
