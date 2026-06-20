using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace StructoFox.App;

/// <summary>
/// A tiny single-choice list picker: shows labelled items, returns the picked item's id (or null on
/// cancel). Used for "assign this board to which function/method?" and similar one-of-many choices.
/// </summary>
public static class PickListDialog
{
    public static Task<string?> Show(Window owner, string title, List<(string Id, string Label)> items)
    {
        var dlg = new Window
        {
            Title = title, Width = 380, Height = 460,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        Ui.ThemeWindow(dlg);

        var list = new ListBox { SelectionMode = SelectionMode.Single };
        Ui.Theme(list, TemplatedControl.BackgroundProperty,  "ControlBgBrush");
        Ui.Theme(list, TemplatedControl.ForegroundProperty,  "SidebarTextBrush");
        Ui.Theme(list, TemplatedControl.BorderBrushProperty, "ControlBorderBrush");
        foreach (var (id, label) in items) list.Items.Add(new ListBoxItem { Content = label, Tag = id });

        string? result = null;
        void Commit() { if (list.SelectedItem is ListBoxItem { Tag: string id }) { result = id; dlg.Close(); } }

        var ok = Ui.Btn(Loc.S("Common_OK")); ok.IsDefault = true; ok.Click += (_, _) => Commit();
        var cancel = Ui.Btn(Loc.S("Common_Cancel")); cancel.IsCancel = true; cancel.Click += (_, _) => dlg.Close();
        list.DoubleTapped += (_, _) => Commit();

        var grid = new Grid { Margin = new(12), RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(list, 0); grid.Children.Add(list);
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new(0, 8, 0, 0), Children = { cancel, ok } };
        Grid.SetRow(btnRow, 1); grid.Children.Add(btnRow);
        dlg.Content = grid;

        return dlg.ShowDialog<string?>(owner).ContinueWith(_ => result, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
