using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;

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
    }

    /// <summary>Binds a property to an OXSUIT theme brush via DynamicResource — present-theme or default,
    /// never crashing. Shared so windows and controls all tint from the same well.</summary>
    public static void Theme(Control c, AvaloniaProperty prop, string key) =>
        c.Bind(prop, new DynamicResourceExtension(key));
}
