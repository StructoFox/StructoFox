using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>Neutral model the shared rich-text editor edits, so any host (print composer, board, …) can reuse it
/// without depending on its own item type. The host copies its fields in, opens the dialog, and copies them back.</summary>
public sealed class RichTextModel
{
    public List<TextRun> Runs        { get; set; } = new() { new TextRun { Text = "Text" } };
    public string        FontFamily  { get; set; } = "";
    public double        FontSize    { get; set; } = 14;
    public string        Align       { get; set; } = "Left";   // Left / Center / Right
    public double        LineSpacing { get; set; } = 1.0;
    public string        Color       { get; set; } = "#000000";
    public string?       Background      { get; set; }
    public string?       BorderColor     { get; set; }
    public double        BorderThickness { get; set; }
}

/// <summary>
/// The selection-based rich text-box editor: plain typing + a formatting toolbar (Avalonia has no free rich-text
/// control), a live formatted PREVIEW, and whole-box font / line-spacing / colour / border. Formatting is stored per
/// <see cref="TextRun"/> and kept aligned to the characters as the text is edited. Font FAMILY stays per box.
/// Extracted from the print composer so the board reuses the exact same editor.
/// </summary>
public static class RichTextEditorDialog
{
    /// <summary>Opens the editor on <paramref name="m"/>. Returns true (and writes the edits back into <paramref name="m"/>)
    /// on OK, false on cancel. <paramref name="canvasBgHex"/> tints the preview's outer border to match the host canvas.</summary>
    public static async Task<bool> Edit(Window owner, RichTextModel m, string title, string canvasBgHex)
    {
        if (m.Runs.Count == 0) m.Runs.Add(new TextRun { Text = "" });
        var runs = m.Runs.Select(r => r.Clone()).ToList();   // work on a copy; Cancel discards
        string cur = RichText.Plain(runs).Replace("\r\n", "\n").Replace("\r", "\n");

        var dlg = new Window { Title = title, Width = 820, MinWidth = 640, CanResize = true,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Ui.ThemeWindow(dlg);

        var textBox = new TextBox { Text = cur, AcceptsReturn = true, MinHeight = 190, TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        ThemeInput(textBox);

        var family = new ComboBox { MinWidth = 170 }; ThemeInput(family);
        family.Items.Add(Loc.S("Pc_Default"));
        foreach (var f in FontManager.Current.SystemFonts.Select(f => f.Name).Distinct().OrderBy(n => n)) family.Items.Add(f);
        family.SelectedIndex = string.IsNullOrEmpty(m.FontFamily) ? 0 : Math.Max(0, family.Items.IndexOf(m.FontFamily));
        var size   = new TextBox { Text = Num(m.FontSize), Width = 56 }; ThemeInput(size);
        var lineSp = new TextBox { Text = Num(m.LineSpacing), Width = 56 }; ThemeInput(lineSp);
        var alignBox = new ComboBox { MinWidth = 110 }; ThemeInput(alignBox);
        foreach (var a in new[] { "Pc_AlignLeft", "Pc_AlignCenter", "Pc_AlignRight" }) alignBox.Items.Add(Loc.S(a));
        alignBox.SelectedIndex = m.Align == "Center" ? 1 : m.Align == "Right" ? 2 : 0;

        // No whole-box text-colour control: the default stays black and colour is applied PER SELECTION via the "A"
        // toolbar button. Text typed inside a coloured selection inherits its colour, so a box-wide setting is moot.
        var backCol   = new ColorField(Loc.S("Pc_Background")); SetColorField(backCol, m.Background, Colors.White, true);
        var borderCol = new ColorField(Loc.S("Pc_Border"));     SetColorField(borderCol, m.BorderColor, Colors.Black, true);
        var borderW   = new TextBox { Text = Num(m.BorderThickness), Width = 56 }; ThemeInput(borderW);

        // The preview mimics the result: the OUTER border is the canvas colour, the INNER border is the text box
        // itself (its own background + border), so the box's optics show exactly as they will on the canvas.
        var canvasBg = ParseBrush(canvasBgHex, Brushes.White);
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var previewInner = new Border { Child = preview, Padding = new(8) };
        var previewBox = new Border { Child = previewInner, Padding = new(14), MinHeight = 190,
            Background = canvasBg, BorderThickness = new(1),
            VerticalAlignment = VerticalAlignment.Stretch };
        Ui.Theme(previewBox, Border.BorderBrushProperty, "ControlBorderBrush");

        // Clicking a toolbar button or the colour dialog moves focus off the editor and clears the live selection,
        // so remember the last non-empty selection and fall back to it when the editor currently has none.
        (int s, int e) lastSel = (0, 0);
        (int s, int e) Sel()
        {
            int a = textBox.SelectionStart, b = textBox.SelectionEnd;
            var cr = a <= b ? (a, b) : (b, a);
            if (cr.Item2 > cr.Item1) { lastSel = cr; return cr; }
            return lastSel;
        }
        var selSize = new TextBox { Text = "", Width = 46, PlaceholderText = "px" }; ThemeInput(selSize);
        ToolTip.SetTip(selSize, Loc.S("Pc_SelSize"));
        var selSizeBtn = Ui.Btn(Loc.S("Pc_ApplySize")); selSizeBtn.Focusable = false;
        ToolTip.SetTip(selSizeBtn, Loc.S("Pc_SelSize"));

        // Shows the selected text's font size (empty when the selection mixes sizes); not while the field is being typed in.
        void SyncSelSize()
        {
            if (selSize.IsFocused) return;
            int a0 = textBox.SelectionStart, b0 = textBox.SelectionEnd;
            int s = Math.Min(a0, b0), e = Math.Max(a0, b0); if (e <= s) { selSize.Text = ""; return; }
            double box = Math.Clamp(ParseNum(size.Text, m.FontSize), 4, 400);
            double? common = null; bool mixed = false; int pos = 0;
            foreach (var r in runs)
            {
                int a = Math.Max(s, pos), b = Math.Min(e, pos + r.Text.Length);
                if (b > a) { double eff = r.Size is { } sz && sz > 0 ? sz : box; if (common is null) common = eff; else if (Math.Abs(common.Value - eff) > 0.001) mixed = true; }
                pos += r.Text.Length;
            }
            selSize.Text = mixed || common is null ? "" : Num(common.Value);
        }
        void ApplySelSize() { var (s, e) = Sel(); if (e <= s) return; var v = ParseNum(selSize.Text, 0); RichText.Apply(runs, s, e, r => r.Size = v >= 4 ? v : (double?)null); Refresh(); }

        var bold = new ToggleButton { Content = "B", FontWeight = FontWeight.Bold, MinWidth = 32, IsThreeState = true };
        var ital = new ToggleButton { Content = "I", FontStyle = FontStyle.Italic, MinWidth = 32, IsThreeState = true };
        var undl = new ToggleButton { Content = "U", MinWidth = 32, IsThreeState = true };
        var strk = new ToggleButton { Content = "S", MinWidth = 32, IsThreeState = true };
        var sup  = new ToggleButton { Content = "x²", MinWidth = 34, IsThreeState = true };
        var sub  = new ToggleButton { Content = "x₂", MinWidth = 34, IsThreeState = true };
        foreach (var b in new Control[] { bold, ital, undl, strk, sup, sub }) ThemeInput(b);

        bool AnyRun(int s, int e, Func<TextRun, bool> p)
        {
            int pos = 0;
            foreach (var r in runs) { int a = Math.Max(s, pos), b = Math.Min(e, pos + r.Text.Length); if (b > a && p(r)) return true; pos += r.Text.Length; }
            return false;
        }
        // Reflects the LIVE selection in each toggle: highlighted when the selection has the effect, dimmed when only
        // part of it does (mixed), plain when none. We avoid the tri-state indeterminate visual (unreadable in theme).
        void SyncButtons()
        {
            int a0 = textBox.SelectionStart, b0 = textBox.SelectionEnd;
            int s = Math.Min(a0, b0), e = Math.Max(a0, b0);
            void Set(ToggleButton btn, Func<TextRun, bool> p)
            {
                bool all = e > s && RichText.All(runs, s, e, p);
                bool any = e > s && AnyRun(s, e, p);
                btn.IsChecked = all || any;
                btn.Opacity   = any && !all ? 0.55 : 1.0;
            }
            Set(bold, r => r.Bold); Set(ital, r => r.Italic); Set(undl, r => r.Underline);
            Set(strk, r => r.Strike); Set(sup, r => r.Super); Set(sub, r => r.Sub);
        }

        selSizeBtn.Click += (_, _) => ApplySelSize();
        selSize.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { ApplySelSize(); e.Handled = true; } };

        textBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.SelectionStartProperty || e.Property == TextBox.SelectionEndProperty)
            {
                int a = textBox.SelectionStart, b = textBox.SelectionEnd;
                if (a != b) lastSel = a <= b ? (a, b) : (b, a);
                SyncSelSize();
                SyncButtons();
            }
        };

        void Refresh()
        {
            preview.Inlines!.Clear();
            preview.FontFamily = family.SelectedIndex <= 0 ? FontFamily.Default : new FontFamily(family.SelectedItem as string ?? "");
            double bs = Math.Clamp(ParseNum(size.Text, m.FontSize), 4, 400);
            preview.FontSize = bs;
            preview.TextAlignment = alignBox.SelectedIndex == 1 ? TextAlignment.Center : alignBox.SelectedIndex == 2 ? TextAlignment.Right : TextAlignment.Left;
            var defFg = ParseBrush(m.Color, Brushes.Black);   // box default colour (not user-editable; per-run colour wins)
            preview.Foreground = defFg;
            double lsv = Math.Clamp(ParseNum(lineSp.Text, 1), 0.1, 4);
            double maxFs = runs.Aggregate(bs, (mx, r) => r.Size is { } sz && sz > mx ? sz : mx);
            preview.LineHeight = Math.Max(maxFs * 1.3 * lsv, maxFs * 1.18);   // floor: never let lines overlap
            previewInner.Background = backCol.Inherit ? Brushes.Transparent : ParseBrush(HexColorPicker.HexOf(backCol.Color), Brushes.Transparent);
            double bw = Math.Clamp(ParseNum(borderW.Text, 0), 0, 40);
            previewInner.BorderThickness = new(bw);
            previewInner.BorderBrush = bw > 0 && !borderCol.Inherit ? ParseBrush(HexColorPicker.HexOf(borderCol.Color), Brushes.Transparent) : Brushes.Transparent;
            foreach (var inl in RichText.ToInlines(runs, bs, 1.0, defFg)) preview.Inlines.Add(inl);
            SyncSelSize();
            SyncButtons();
        }

        void Toggle(Func<TextRun, bool> get, Action<TextRun, bool> set)
        {
            var (s, e) = Sel(); if (e <= s) return;
            bool allOn = RichText.All(runs, s, e, get);
            RichText.Apply(runs, s, e, r => set(r, !allOn));
            Refresh();
        }

        bold.Click += (_, _) => Toggle(r => r.Bold,      (r, v) => r.Bold = v);
        ital.Click += (_, _) => Toggle(r => r.Italic,    (r, v) => r.Italic = v);
        undl.Click += (_, _) => Toggle(r => r.Underline, (r, v) => r.Underline = v);
        strk.Click += (_, _) => Toggle(r => r.Strike,    (r, v) => r.Strike = v);
        sup.Click  += (_, _) => { var (s, e) = Sel(); if (e <= s) return; bool on = RichText.All(runs, s, e, r => r.Super); RichText.Apply(runs, s, e, r => { r.Super = !on; if (r.Super) r.Sub = false; }); Refresh(); };
        sub.Click  += (_, _) => { var (s, e) = Sel(); if (e <= s) return; bool on = RichText.All(runs, s, e, r => r.Sub);   RichText.Apply(runs, s, e, r => { r.Sub = !on;   if (r.Sub)   r.Super = false; }); Refresh(); };

        var colBtn = Ui.Btn("A"); ToolTip.SetTip(colBtn, Loc.S("Pc_ColorText"));
        colBtn.Click += async (_, _) => { var (s, e) = Sel(); if (e <= s) return; var hex = await ColorPickDialog.Pick(dlg, Loc.S("Pc_ColorText"), "#000000"); if (hex is null) return; RichText.Apply(runs, s, e, r => r.Fg = hex); Refresh(); };
        var mkBtn = Ui.Btn("▨"); ToolTip.SetTip(mkBtn, Loc.S("Pc_Marker"));
        mkBtn.Click += async (_, _) =>
        {
            var (s, e) = Sel(); if (e <= s) return;
            if (RichText.All(runs, s, e, r => r.Marker != null)) { RichText.Apply(runs, s, e, r => r.Marker = null); Refresh(); return; }
            var hex = await ColorPickDialog.Pick(dlg, Loc.S("Pc_Marker"), "#FFF176"); if (hex is null) return;
            RichText.Apply(runs, s, e, r => r.Marker = hex); Refresh();
        };

        var clearBtn = Ui.Btn("⊘"); ToolTip.SetTip(clearBtn, Loc.S("Pc_ClearFormat"));
        clearBtn.Click += (_, _) => { var (s, e) = Sel(); if (e <= s) return; RichText.Apply(runs, s, e, r => { r.Bold = r.Italic = r.Underline = r.Strike = r.Super = r.Sub = false; r.Fg = null; r.Marker = null; r.Size = null; }); Refresh(); };

        var help = new Button { Content = "?", Width = 26, Padding = new(0), Focusable = false }; ThemeInput(help);
        ToolTip.SetTip(help, Loc.S("Pc_FormatHelp"));

        textBox.TextChanged += (_, _) => { var nt = (textBox.Text ?? "").Replace("\r\n", "\n").Replace("\r", "\n"); RichText.Remap(runs, cur, nt); cur = nt; Refresh(); };
        family.SelectionChanged  += (_, _) => Refresh();
        size.TextChanged     += (_, _) => Refresh();
        lineSp.TextChanged   += (_, _) => Refresh();
        borderW.TextChanged  += (_, _) => Refresh();
        alignBox.SelectionChanged += (_, _) => Refresh();
        backCol.Changed   += (_, _) => Refresh();
        borderCol.Changed += (_, _) => Refresh();

        // Effect toggles on the left; the colour + highlight buttons pushed to the RIGHT (they act on the selection).
        var toolbar = new WrapPanel();
        foreach (var c in new Control[] { bold, ital, undl, strk, sup, sub, clearBtn,
                                          new Border { Width = 10 }, colBtn, mkBtn })
        { c.Margin = new(0, 0, 4, 4); c.Focusable = false; toolbar.Children.Add(c); }

        var sizeGroup = new Border { CornerRadius = new(4), Padding = new(6, 2), VerticalAlignment = VerticalAlignment.Center, BorderThickness = new(1),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
                Children = { Lbl(Loc.S("Pc_Size")), Spinner(size, 4, 400, 1), selSize, selSizeBtn } } };
        Ui.Theme(sizeGroup, Border.BorderBrushProperty, "ControlBorderBrush");

        var globalRow = new WrapPanel { Children = {
            family, sizeGroup,
            Lbl(Loc.S("Pc_LineSpacing")), Spinner(lineSp, 0.1, 4, 0.1),
            Lbl(Loc.S("Pc_Align")), alignBox } };
        foreach (var c in globalRow.Children) c.Margin = new(0, 0, 12, 4);

        var borderWCol = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
            Children = { Lbl(Loc.S("Pc_BorderWidth")), Spinner(borderW, 0, 40, 0.5) } };
        var opticsCol = new StackPanel { Spacing = 6, Children = { backCol,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { borderCol, borderWCol } } } };

        var editorHead = new Grid { ColumnDefinitions = new("*,Auto") };
        var editorLbl = Lbl(Loc.S("Pc_Editor")); Grid.SetColumn(editorLbl, 0);
        Grid.SetColumn(help, 1);
        editorHead.Children.Add(editorLbl); editorHead.Children.Add(help);

        var previewCol = new StackPanel { Spacing = 6, Children = { Lbl(Loc.S("Pc_Preview")), previewBox, opticsCol } };
        var editorCol  = new StackPanel { Spacing = 6, Children = { editorHead, textBox, toolbar } };
        var topGrid = new Grid { ColumnDefinitions = new("*,Auto,*"), ColumnSpacing = 12 };
        var colSep = new Border { Width = 1, Background = Brushes.Transparent, Margin = new(0, 4) };
        Grid.SetColumn(previewCol, 0); topGrid.Children.Add(previewCol);
        Grid.SetColumn(colSep, 1);     topGrid.Children.Add(colSep);
        Grid.SetColumn(editorCol, 2);  topGrid.Children.Add(editorCol);

        var rowSep = new Border { Height = 1, Background = Brushes.Gray, Opacity = 0.4 };
        var panel = new StackPanel { Margin = new(16), Spacing = 10, Children = { globalRow, rowSep, topGrid } };
        var okBtn = Ui.Btn(Loc.S("Common_Ok")); bool ok = false; okBtn.Click += (_, _) => { ok = true; dlg.Close(); };
        var cancel = Ui.Btn(Loc.S("Common_Cancel")); cancel.Click += (_, _) => dlg.Close();
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, okBtn } });
        dlg.Content = new ScrollViewer { Content = panel, MaxHeight = 740 };
        Refresh();
        await dlg.ShowDialog(owner);
        if (!ok) return false;

        m.Runs = runs;
        m.FontFamily = family.SelectedIndex <= 0 ? "" : (family.SelectedItem as string ?? "");
        m.FontSize   = Math.Clamp(ParseNum(size.Text, m.FontSize), 4, 400);
        m.LineSpacing = Math.Clamp(ParseNum(lineSp.Text, 1), 0.1, 4);
        m.Align = alignBox.SelectedIndex == 1 ? "Center" : alignBox.SelectedIndex == 2 ? "Right" : "Left";
        // m.Color (box default) is left untouched — colour is a per-selection run property now.
        m.Background = backCol.Inherit ? null : HexColorPicker.HexOf(backCol.Color);
        m.BorderColor = borderCol.Inherit ? null : HexColorPicker.HexOf(borderCol.Color);
        m.BorderThickness = Math.Clamp(ParseNum(borderW.Text, 0), 0, 40);
        return true;
    }

    // ── Small shared UI helpers (mirror the composer's private ones) ───────────────────────────────
    internal static TextBlock Lbl(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };

    internal static void ThemeInput(Control c)
    {
        Ui.Theme(c, TemplatedControl.BackgroundProperty,  "InputBgBrush");
        Ui.Theme(c, TemplatedControl.ForegroundProperty,  "SidebarTextBrush");
        Ui.Theme(c, TemplatedControl.BorderBrushProperty, "ControlBorderBrush");
    }

    internal static IBrush ParseBrush(string? hex, IBrush fallback)
    { try { return string.IsNullOrWhiteSpace(hex) ? fallback : new SolidColorBrush(Color.Parse(hex)); } catch { return fallback; } }

    internal static string Num(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    internal static double ParseNum(string? s, double fallback)
        => double.TryParse((s ?? "").Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    internal static void SetColorField(ColorField f, string? hex, Color fallback, bool allowNone)
    {
        if (string.IsNullOrWhiteSpace(hex)) { if (allowNone) f.Inherit = true; else { f.Inherit = false; f.Color = fallback; } }
        else { f.Inherit = false; try { f.Color = Color.Parse(hex); } catch { f.Color = fallback; } }
    }

    // A numeric field plus tiny ▲/▼ step buttons on one line (own buttons, so no unthemed spinner glyphs).
    internal static Control Spinner(TextBox field, double min, double max, double step)
    {
        void Bump(double d) => field.Text = Num(Math.Clamp(ParseNum(field.Text, min) + d, min, max));
        var up = new Button { Content = "▲", FontSize = 8, Padding = new(5, 0), MinWidth = 0 }; up.Click += (_, _) => Bump(step);
        var dn = new Button { Content = "▼", FontSize = 8, Padding = new(5, 0), MinWidth = 0 }; dn.Click += (_, _) => Bump(-step);
        ThemeInput(up); ThemeInput(dn);
        field.VerticalAlignment = VerticalAlignment.Center;
        var stack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { up, dn } };
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { field, stack } };
    }
}
