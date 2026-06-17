using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StructoFox.Core;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// A collapsible colour row for the style editor. Collapsed, it shows just a swatch, the colour's
/// palette name (or "Custom") and its RGB/CMYK values in small print; expanded, it reveals the full
/// picker plus an "inherit" toggle. Keeps the style editor compact even with several colours.
/// </summary>
public class ColorField : StackPanel
{
    readonly Expander       _exp     = new() { Margin = new(0, 2, 0, 2) };
    readonly HexColorPicker _picker  = new();
    readonly CheckBox       _inherit = new() { Content = Loc.S("Style_Inherit") };
    readonly Border         _swatch  = new() { Width = 18, Height = 18, CornerRadius = new(3), BorderBrush = Brushes.Gray, BorderThickness = new(1), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock      _name    = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    readonly TextBlock      _vals    = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 10, Opacity = 0.7 };

    /// <summary>Raised when the colour or its inherit state changes.</summary>
    public event EventHandler? Changed;

    // Builds the expander: a one-line summary header over the inherit toggle + picker.
    public ColorField(string label)
    {
        _exp.Header = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                new TextBlock { Text = label, Width = 80, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold },
                _swatch, _name, _vals,
            },
        };
        _exp.Content = new StackPanel { Spacing = 6, Children = { _inherit, _picker } };
        Children.Add(_exp);

        _inherit.IsCheckedChanged += (_, _) => { _picker.IsEnabled = !Inherit; UpdateSummary(); Changed?.Invoke(this, EventArgs.Empty); };
        _picker.ColorChanged      += (_, _) => { UpdateSummary(); Changed?.Invoke(this, EventArgs.Empty); };
        UpdateSummary();
    }

    /// <summary>Whether this property inherits (true = no override).</summary>
    public bool Inherit
    {
        get => _inherit.IsChecked == true;
        set { _inherit.IsChecked = value; _picker.IsEnabled = !value; UpdateSummary(); }
    }

    /// <summary>The picked colour (meaningful only when not inheriting).</summary>
    public Color Color
    {
        get => _picker.Color;
        set { _picker.Color = value; UpdateSummary(); }
    }

    // Refreshes the collapsed-state summary: swatch, palette name (or Custom), and RGB/CMYK values.
    void UpdateSummary()
    {
        if (Inherit)
        {
            _swatch.Background = Brushes.Transparent;
            _name.Text = Loc.S("Style_Inherit");
            _vals.Text = "";
            return;
        }
        var c = Color;
        _swatch.Background = new SolidColorBrush(c);
        _name.Text = PaletteName(c) ?? "Custom";
        var (cy, m, ye, k) = HexColorPicker.RgbToCmyk(c);
        _vals.Text = $"rgb {c.R},{c.G},{c.B}  ·  cmyk {P(cy)},{P(m)},{P(ye)},{P(k)}";
    }

    // Whole-percent helper for CMYK display.
    static string P(double f) => Math.Round(f * 100).ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Looks up a colour's name in the active palette, or null if it isn't a known palette colour.
    static string? PaletteName(Color c)
    {
        var hex = HexColorPicker.HexOf(c);
        return PaletteStore.Active().Colors.FirstOrDefault(n => string.Equals(n.Value, hex, StringComparison.OrdinalIgnoreCase))?.Name;
    }
}
