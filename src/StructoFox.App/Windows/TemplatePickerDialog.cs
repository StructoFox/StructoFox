using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using StructoFox.Core;

namespace StructoFox.App;

/// <summary>Lists the saved header templates so the user can pick one to apply (or delete one). Returns the
/// chosen template name on apply, or null on cancel.</summary>
public class TemplatePickerDialog : Window
{
    readonly ListBox _list = new() { MinHeight = 180 };

    public static new Task<string?> Show(Window owner) => new TemplatePickerDialog().ShowDialog<string?>(owner);

    TemplatePickerDialog()
    {
        Title = Loc.S("Decor_TemplatePick");
        Width = 380; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Ui.ThemeWindow(this);

        Reload();
        Ui.Theme(_list, TemplatedControl.BackgroundProperty, "InputBgBrush");
        Ui.Theme(_list, TemplatedControl.ForegroundProperty, "SidebarTextBrush");

        var apply = Ui.Btn(Loc.S("Decor_ApplyTemplate")); apply.IsDefault = true;
        apply.Click += (_, _) => { if (_list.SelectedItem is string n) Close(n); };
        var del = Ui.Btn(Loc.S("Decor_DeleteTemplate"));
        del.Click += (_, _) => { if (_list.SelectedItem is string n) { HeaderTemplateService.Delete(n); Reload(); } };
        var cancel = Ui.Btn(Loc.S("Common_Cancel")); cancel.IsCancel = true; cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new(18), Spacing = 10,
            Children =
            {
                _list,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { del, cancel, apply } },
            },
        };
    }

    void Reload()
    {
        var names = HeaderTemplateService.List();
        _list.ItemsSource = names;
        if (names.Count > 0) _list.SelectedIndex = 0;
    }
}
