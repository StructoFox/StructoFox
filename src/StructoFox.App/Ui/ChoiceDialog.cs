using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace StructoFox.App;

/// <summary>
/// A small message dialog that offers several labelled ACTIONS as full-width buttons (plus Cancel), returning the
/// chosen action's id or null. Clearer than a single-choice list when there are a few distinct actions to pick from
/// (e.g. "rename links / leave / delete").
/// </summary>
public static class ChoiceDialog
{
    public static Task<string?> Show(Window owner, string title, string body, List<(string Id, string Label)> options)
    {
        var dlg = new Window
        {
            Title = title, Width = 440, SizeToContent = SizeToContent.Height, MaxHeight = 560,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        Ui.ThemeWindow(dlg);

        string? result = null;
        var panel = new StackPanel { Margin = new(16), Spacing = 10 };

        var msg = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap };
        Ui.Theme(msg, TextBlock.ForegroundProperty, "SidebarTextBrush");
        panel.Children.Add(new ScrollViewer { Content = msg, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        foreach (var (id, label) in options)
        {
            var b = Ui.Btn(label);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            b.Click += (_, _) => { result = id; dlg.Close(); };
            panel.Children.Add(b);
        }

        var cancel = Ui.Btn(Loc.S("Common_Cancel"));
        cancel.IsCancel = true;
        cancel.HorizontalAlignment = HorizontalAlignment.Right;
        cancel.Margin = new(0, 4, 0, 0);
        cancel.Click += (_, _) => dlg.Close();
        panel.Children.Add(cancel);

        dlg.Content = panel;
        return dlg.ShowDialog<object?>(owner).ContinueWith(_ => result, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
