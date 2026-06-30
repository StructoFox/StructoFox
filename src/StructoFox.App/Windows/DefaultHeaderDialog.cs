using Avalonia.Controls;
using Avalonia.Layout;
using StructoFox.Core;

namespace StructoFox.App;

/// <summary>Lets the user choose which saved header template is applied automatically to new flowcharts and
/// new structograms (or none). Stored globally via <see cref="HeaderTemplateService"/>.</summary>
public class DefaultHeaderDialog : Window
{
    readonly ComboBox _pap    = Ui.Combo(220);
    readonly ComboBox _struct = Ui.Combo(220);

    public static new Task Show(Window owner) => new DefaultHeaderDialog().ShowDialog(owner);

    DefaultHeaderDialog()
    {
        Title = Loc.S("DefHdr_Title");
        Width = 420; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Ui.ThemeWindow(this);

        var none = Loc.S("DefHdr_None");
        var names = HeaderTemplateService.List();
        void Fill(ComboBox c, string current)
        {
            c.Items.Add(none);
            foreach (var n in names) c.Items.Add(n);
            c.SelectedItem = !string.IsNullOrEmpty(current) && names.Contains(current) ? current : none;
        }
        Fill(_pap, HeaderTemplateService.DefaultForPap);
        Fill(_struct, HeaderTemplateService.DefaultForStruct);

        var ok = Ui.Btn(Loc.S("Common_OK")); ok.IsDefault = true;
        ok.Click += (_, _) =>
        {
            HeaderTemplateService.DefaultForPap    = _pap.SelectedItem as string == none ? "" : _pap.SelectedItem as string ?? "";
            HeaderTemplateService.DefaultForStruct = _struct.SelectedItem as string == none ? "" : _struct.SelectedItem as string ?? "";
            Close();
        };
        var cancel = Ui.Btn(Loc.S("Common_Cancel")); cancel.IsCancel = true; cancel.Click += (_, _) => Close();

        TextBlock Lbl(string t) { var b = new TextBlock { Text = t }; Ui.Theme(b, TextBlock.ForegroundProperty, "ContentTextBrush"); return b; }

        Content = new StackPanel
        {
            Margin = new(18), Spacing = 10,
            Children =
            {
                Lbl(Loc.S("DefHdr_Pap")), _pap,
                Lbl(Loc.S("DefHdr_Struct")), _struct,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Margin = new(0, 6, 0, 0), Children = { cancel, ok } },
            },
        };
    }
}
