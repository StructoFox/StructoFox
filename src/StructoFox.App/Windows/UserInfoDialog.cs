using Avalonia.Controls;
using Avalonia.Layout;

namespace StructoFox.App;

/// <summary>The user's own details (name + department), suggested into a new diagram's header. Stored globally
/// in <see cref="AppSettings"/>.</summary>
public class UserInfoDialog : Window
{
    readonly TextBox _name = new() { MinWidth = 280 };
    readonly TextBox _dept = new() { MinWidth = 280 };

    public static new Task Show(Window owner) => new UserInfoDialog().ShowDialog(owner);

    UserInfoDialog()
    {
        Title = Loc.S("UserInfo_Title");
        Width = 420; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Ui.ThemeWindow(this);

        _name.Text = AppSettings.UserName;
        _dept.Text = AppSettings.UserDepartment;
        foreach (var b in new[] { _name, _dept })
        {
            Ui.Theme(b, TextBox.BackgroundProperty,  "InputBgBrush");
            Ui.Theme(b, TextBox.ForegroundProperty,  "SidebarTextBrush");
            Ui.Theme(b, TextBox.BorderBrushProperty, "ControlBorderBrush");
        }

        var ok = Ui.Btn(Loc.S("Common_OK")); ok.IsDefault = true;
        ok.Click += (_, _) =>
        {
            AppSettings.UserName       = (_name.Text ?? "").Trim();
            AppSettings.UserDepartment = (_dept.Text ?? "").Trim();
            Close();
        };
        var cancel = Ui.Btn(Loc.S("Common_Cancel")); cancel.IsCancel = true; cancel.Click += (_, _) => Close();

        TextBlock Lbl(string t) { var b = new TextBlock { Text = t }; Ui.Theme(b, TextBlock.ForegroundProperty, "ContentTextBrush"); return b; }

        Content = new StackPanel
        {
            Margin = new(18), Spacing = 10,
            Children =
            {
                Lbl(Loc.S("UserInfo_Name")), _name,
                Lbl(Loc.S("UserInfo_Dept")), _dept,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Margin = new(0, 6, 0, 0), Children = { cancel, ok } },
            },
        };
    }
}
