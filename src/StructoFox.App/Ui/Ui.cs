using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace StructoFox.App;

/// <summary>A combo entry that shows <see cref="Name"/> but quietly carries an opaque <see cref="Id"/>.
/// The label the user reads and the value the code keeps, travelling together.</summary>
public sealed record ComboItem(string Name, string Id)
{
    // The drop-down shows this; Avalonia renders plain items via their ToString().
    public override string ToString() => Name;
}

/// <summary>
/// Small factory of consistently-styled controls. The shared toolbox every window dips into,
/// so buttons and friends look the same whether they live on a board, a flowchart, or a dialog.
/// </summary>
public static class Ui
{
    /// <summary>
    /// Builds a standard push button with uniform padding and an optional tooltip.
    /// One button to rule them all — or at least to look the same everywhere.
    /// </summary>
    public static Button Btn(string text, string? tooltip = null)
    {
        var b = new Button
        {
            Content = text,
            Padding = new(12, 6),
            CornerRadius = new(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        if (!string.IsNullOrEmpty(tooltip)) ToolTip.SetTip(b, tooltip);
        // Tie buttons to the OXSUIT theme so their label stays readable on any themed surface
        // (Fluent's own button foreground doesn't follow our inherited window text colour).
        Theme(b, TemplatedControl.BackgroundProperty, "ControlBgBrush");
        Theme(b, TemplatedControl.ForegroundProperty, "SidebarTextBrush");
        return b;
    }

    /// <summary>
    /// Builds a themed combo box wired to the OXSUIT brushes. Pass a width to fix it,
    /// or leave it to stretch. Fill it with <see cref="ComboItem"/>s and read SelectedItem back.
    /// </summary>
    public static ComboBox Combo(double width = 0)
    {
        var c = new ComboBox { MinHeight = 32, CornerRadius = new(4), FontSize = 13 };
        if (width > 0) c.Width = width;
        else c.HorizontalAlignment = HorizontalAlignment.Stretch;

        // Pull colours from the active OXSUIT theme; a theme swap restyles the combo live.
        Theme(c, TemplatedControl.BackgroundProperty,  "ControlBgBrush");
        Theme(c, TemplatedControl.ForegroundProperty,  "SidebarTextBrush");
        Theme(c, TemplatedControl.BorderBrushProperty, "ControlBorderBrush");
        return c;
    }

    /// <summary>Themes a whole window: surface background AND an inherited text colour, so chrome text
    /// always stays readable on the OXSUIT background (TextElement.Foreground cascades to children).</summary>
    public static void ThemeWindow(Window w)
    {
        Theme(w, TemplatedControl.BackgroundProperty, "ContentBgBrush");
        Theme(w, TextElement.ForegroundProperty,      "ContentTextBrush");
        if (AppIcon() is { } icon) w.Icon = icon;   // taskbar + title-bar icon, every window
    }

    // The embedded app icon, loaded once and shared across all windows.
    static WindowIcon? _appIcon;
    static WindowIcon? AppIcon()
    {
        if (_appIcon is not null) return _appIcon;
        try { _appIcon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://StructoFox.App/Assets/appicon.png"))); }
        catch { /* no icon is fine */ }
        return _appIcon;
    }

    /// <summary>Binds a property to an OXSUIT theme brush via DynamicResource — present-theme or default,
    /// never crashing. Shared so windows and controls all tint from the same well.</summary>
    public static void Theme(Control c, AvaloniaProperty prop, string key) =>
        c.Bind(prop, new DynamicResourceExtension(key));

    /// <summary>
    /// A clickable colour chip (Border, not Button — so its hover never hides the colour). On hover
    /// it lifts with a chip-coloured corona; <paramref name="selected"/> gives it a thicker ring.
    /// </summary>
    public static Control ColorChip(string hex, string tooltip, Action onClick, bool selected = false, double size = 24)
    {
        var b = new Border
        {
            Width = size, Height = size, Margin = new(3), CornerRadius = new(3),
            BorderBrush = selected ? Brushes.Black : Brushes.Gray,
            BorderThickness = new(selected ? 3 : 1),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        try { b.Background = new SolidColorBrush(Color.Parse(hex)); } catch { b.Background = Brushes.Transparent; }
        ToolTip.SetTip(b, tooltip);

        b.PointerEntered += (_, _) => { try { b.BoxShadow = BoxShadows.Parse($"0 1 7 2 {hex}"); } catch { } b.ZIndex = 1; };
        b.PointerExited  += (_, _) => { b.BoxShadow = default; b.ZIndex = 0; };
        b.PointerPressed += (_, _) => onClick();
        return b;
    }
}
