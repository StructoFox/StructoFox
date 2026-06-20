using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using OXSUIT.Loaders.Avalonia;
using StructoFox.Core;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// Shared entry point for the 🔁 buttons. Lets the user pick which diagram to open for a
/// given function/method (Programmablaufplan or Struktogramm), so every call site behaves alike.
/// Avalonia port of ClaudetRelay's launcher.
/// </summary>
public static class DiagramLauncher
{
    /// <summary>
    /// Shows the chooser modally over <paramref name="owner"/> and opens the picked diagram for
    /// <paramref name="key"/>. Each option hints whether a diagram already exists on disk.
    /// </summary>
    public static Task ChooseAndOpen(Window owner, string projFolder, string key, string title, string? themePath)
    {
        var dlg = new Window
        {
            Title                 = Loc.S("Diag_Title"),
            Width                 = 360,
            Height                = 230,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        // Merge the theme so DynamicResource brushes resolve, then tint the window surface.
        if (!string.IsNullOrWhiteSpace(themePath))
            try { dlg.Resources.MergedDictionaries.Add(OxsuitLoader.Load(themePath)); } catch { /* unthemed is fine */ }
        Ui.ThemeWindow(dlg);

        var stack = new StackPanel { Margin = new(16), Spacing = 6 };
        dlg.Content = stack;

        var hdr = new TextBlock
        {
            Text         = string.Format(Loc.S("Diag_SketchOf"), title),
            FontSize     = 12,
            TextWrapping  = TextWrapping.Wrap,
            Margin       = new(0, 0, 0, 12),
        };
        Ui.Theme(hdr, TextBlock.ForegroundProperty, "SidebarTextBrush");
        stack.Children.Add(hdr);

        // Programmablaufplan option — label flags whether a flowchart already exists.
        var papBtn = ChoiceBtn(FlowChartService.Exists(projFolder, key) ? Loc.S("Diag_PapExists") : Loc.S("Diag_Pap"));
        papBtn.Click += (_, _) =>
        {
            dlg.Close();
            new FlowChartWindow(projFolder, key, title, themePath).Show();
        };
        stack.Children.Add(papBtn);

        // Struktogramm option — same existence hint, plus a tooltip naming the DIN standard.
        var nsBtn = ChoiceBtn(StructogramService.Exists(projFolder, key) ? Loc.S("Diag_NsExists") : Loc.S("Diag_Ns"));
        ToolTip.SetTip(nsBtn, Loc.S("Diag_NsTip"));
        nsBtn.Click += (_, _) =>
        {
            dlg.Close();
            new StructogramWindow(projFolder, key, title, themePath).Show();
        };
        stack.Children.Add(nsBtn);

        // Board option — opens THE board assigned to this function/method (one board kind, in the
        // registry/gallery), creating it on first use. No separate ad-hoc per-key board.
        var hasBoard = CodeBoardRegistryService.Load(projFolder).Any(b => b.TargetKey == key);
        var boardBtn = ChoiceBtn(hasBoard ? Loc.S("Diag_BoardExists") : Loc.S("Diag_Board"));
        ToolTip.SetTip(boardBtn, Loc.S("Diag_BoardTip"));
        boardBtn.Click += (_, _) =>
        {
            dlg.Close();
            var boards = CodeBoardRegistryService.Load(projFolder);
            var board  = boards.FirstOrDefault(b => b.TargetKey == key);
            if (board is null)
            {
                board = new CodeBoard { Name = title, TargetKey = key };
                boards.Add(board);
                CodeBoardRegistryService.Save(projFolder, boards);
            }
            new CodeBoardWindow(projFolder, board, themePath).Show();
        };
        stack.Children.Add(boardBtn);

        return dlg.ShowDialog(owner);
    }

    // Builds one left-aligned, full-width choice button tinted from the active OXSUIT theme.
    static Button ChoiceBtn(string label)
    {
        var b = Ui.Btn(label);
        b.HorizontalAlignment        = HorizontalAlignment.Stretch;
        b.HorizontalContentAlignment = HorizontalAlignment.Left;
        Ui.Theme(b, TemplatedControl.BackgroundProperty, "ControlBgBrush");
        Ui.Theme(b, TemplatedControl.ForegroundProperty, "SidebarTextBrush");
        return b;
    }

}
