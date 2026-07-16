using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using static StructoFox.App.RichTextEditorDialog;

namespace StructoFox.App;

/// <summary>Neutral model for the light, single-line (one uniform style) text editor.</summary>
public sealed class SimpleTextModel
{
    public string  Text        { get; set; } = "";
    public string  FontFamily  { get; set; } = "";
    public double  FontSize    { get; set; } = 14;
    public string  Color       { get; set; } = "#000000";
    public bool    Bold        { get; set; }
    public bool    Italic      { get; set; }
    public bool    Underline   { get; set; }
    public bool    Strike      { get; set; }
    public string? Background      { get; set; }
    public string? BorderColor     { get; set; }
    public double  BorderThickness { get; set; }
    public string  Align       { get; set; } = "Left";   // Left / Center / Right
}

/// <summary>The lightweight one-style text editor (mirrors the print composer's Label editor): a single line with
/// one font / size / colour / bold-italic-underline-strike / background / border. Shared by the board.</summary>
public static class SimpleTextEditorDialog
{
    public static async Task<bool> Edit(Window owner, SimpleTextModel m, string title)
    {
        var dlg = new Window { Title = title, Width = 500, MinWidth = 440, CanResize = true,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Ui.ThemeWindow(dlg);

        var textBox = new TextBox { Text = m.Text, AcceptsReturn = false, TextWrapping = TextWrapping.NoWrap };
        ThemeInput(textBox);

        var family = new ComboBox { MinWidth = 180 }; ThemeInput(family);
        family.Items.Add(Loc.S("Pc_Default"));
        foreach (var f in FontManager.Current.SystemFonts.Select(f => f.Name).Distinct().OrderBy(n => n)) family.Items.Add(f);
        family.SelectedIndex = string.IsNullOrEmpty(m.FontFamily) ? 0 : Math.Max(0, family.Items.IndexOf(m.FontFamily));
        var size = new TextBox { Text = Num(m.FontSize), Width = 56 }; ThemeInput(size);

        var bold = new ToggleButton { Content = "B", FontWeight = FontWeight.Bold, MinWidth = 34, IsChecked = m.Bold };
        var ital = new ToggleButton { Content = "I", FontStyle = FontStyle.Italic, MinWidth = 34, IsChecked = m.Italic };
        var undl = new ToggleButton { Content = "U", MinWidth = 34, IsChecked = m.Underline };
        var strk = new ToggleButton { Content = "S", MinWidth = 34, IsChecked = m.Strike };
        foreach (var t in new[] { bold, ital, undl, strk }) ThemeInput(t);

        var alignBox = new ComboBox { MinWidth = 110 }; ThemeInput(alignBox);
        foreach (var a in new[] { "Pc_AlignLeft", "Pc_AlignCenter", "Pc_AlignRight" }) alignBox.Items.Add(Loc.S(a));
        alignBox.SelectedIndex = m.Align == "Center" ? 1 : m.Align == "Right" ? 2 : 0;

        var textCol   = new ColorField(Loc.S("Pc_ColorText"));  SetColorField(textCol, m.Color, Colors.Black, false);
        var backCol   = new ColorField(Loc.S("Pc_Background")); SetColorField(backCol, m.Background, Colors.White, true);
        var borderCol = new ColorField(Loc.S("Pc_Border"));     SetColorField(borderCol, m.BorderColor, Colors.Black, true);
        var borderW   = new TextBox { Text = Num(m.BorderThickness), Width = 56 }; ThemeInput(borderW);

        var styleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { bold, ital, undl, strk } };
        var fontRow  = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { family, Lbl(Loc.S("Pc_Size")), Spinner(size, 4, 400, 1) } };
        var alignRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Lbl(Loc.S("Pc_Align")), alignBox } };
        var borderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { borderCol, Lbl(Loc.S("Pc_BorderWidth")), Spinner(borderW, 0, 40, 0.5) } };

        var panel = new StackPanel { Margin = new(16), Spacing = 10, Children = { textBox, fontRow, styleRow, alignRow, textCol, backCol, borderRow } };

        bool ok = false;
        var okBtn = Ui.Btn(Loc.S("Common_Ok")); okBtn.Click += (_, _) => { ok = true; dlg.Close(); };
        var cancel = Ui.Btn(Loc.S("Common_Cancel")); cancel.Click += (_, _) => dlg.Close();
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, okBtn } });
        dlg.Content = panel;
        await dlg.ShowDialog(owner);
        if (!ok) return false;

        m.Text = textBox.Text ?? "";
        m.FontFamily = family.SelectedIndex <= 0 ? "" : (family.SelectedItem as string ?? "");
        m.FontSize = Math.Clamp(ParseNum(size.Text, m.FontSize), 4, 400);
        m.Bold = bold.IsChecked == true; m.Italic = ital.IsChecked == true;
        m.Underline = undl.IsChecked == true; m.Strike = strk.IsChecked == true;
        m.Align = alignBox.SelectedIndex == 1 ? "Center" : alignBox.SelectedIndex == 2 ? "Right" : "Left";
        m.Color = textCol.Inherit ? "#000000" : (HexColorPicker.HexOf(textCol.Color) ?? "#000000");
        m.Background = backCol.Inherit ? null : HexColorPicker.HexOf(backCol.Color);
        m.BorderColor = borderCol.Inherit ? null : HexColorPicker.HexOf(borderCol.Color);
        m.BorderThickness = Math.Clamp(ParseNum(borderW.Text, 0), 0, 40);
        return true;
    }
}
