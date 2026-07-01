using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using StructoFox.Core;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// Edits a diagram's branding: its title (text + whether it's shown on the plan), a faint watermark,
/// and a corner logo image. Mutates the passed <see cref="DiagramStyle"/> in place; returns the new
/// title on OK, or null on cancel. The fox stamping its letterhead onto a plan.
/// </summary>
public class DiagramDecorDialog : Window
{
    readonly DiagramStyle _style;
    readonly TextBox  _title     = new() { MinWidth = 280 };
    readonly CheckBox _showTitle = new();
    readonly ComboBox _titlePos  = Ui.Combo(140);
    readonly TextBox  _titleSize = new() { Width = 64 };
    readonly CheckBox _titleBold = new();
    readonly ColorField _titleColor = new(Loc.S("Decor_TitleColor"));
    readonly TextBox  _watermark = new() { MinWidth = 280 };
    readonly TextBox  _watermarkImg = new() { MinWidth = 280 };
    readonly TextBox  _wmAngle   = new() { Width = 64 };
    readonly TextBox  _logo      = new() { MinWidth = 280 };
    readonly ComboBox _corner    = Ui.Combo(160);

    // Info field ("title block").
    readonly CheckBox _showInfo  = new();
    readonly ComboBox _infoPos   = Ui.Combo(140);
    readonly TextBox  _infoName      = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 46 };
    readonly TextBox  _infoProject   = new();
    readonly TextBox  _infoProjectNo = new();
    readonly TextBox  _infoVersion   = new();
    readonly TextBox  _infoDate      = new();
    readonly TextBox  _infoAuthor    = new();
    readonly TextBox  _infoDept      = new();
    readonly TextBox  _infoExtra     = new() { AcceptsReturn = true, MinHeight = 56, TextWrapping = TextWrapping.Wrap };

    public static Task<string?> Show(Window owner, string title, DiagramStyle style,
        Func<(DiagramStyle style, string title)?>? fromPap = null, string? projectName = null)
        => new DiagramDecorDialog(title, style, fromPap, projectName).ShowDialog<string?>(owner);

    DiagramDecorDialog(string title, DiagramStyle style, Func<(DiagramStyle style, string title)?>? fromPap, string? projectName)
    {
        _style = style;
        Title                 = Loc.S("Decor_Title");
        Width                 = 560;
        SizeToContent         = SizeToContent.Height;
        CanResize             = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Ui.ThemeWindow(this);

        _title.Text = title;
        _showTitle.Content = Loc.S("Decor_ShowTitle");
        _showTitle.IsChecked = style.ShowTitle;
        ThemeInput(_title); ThemeInput(_titleSize); ThemeInput(_watermark); ThemeInput(_watermarkImg); ThemeInput(_wmAngle); ThemeInput(_logo);
        Ui.Theme(_showTitle, CheckBox.ForegroundProperty, "ContentTextBrush");
        _titleBold.Content = Loc.S("Decor_TitleBold");
        _titleBold.IsChecked = style.TitleBold;
        Ui.Theme(_titleBold, CheckBox.ForegroundProperty, "ContentTextBrush");

        foreach (var p in Enum.GetValues<DecorPos>()) _titlePos.Items.Add(p);
        _titlePos.SelectedItem = style.TitlePosition;
        _titleSize.Text = style.TitleFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(style.TitleColor)) _titleColor.Inherit = true;
        else { _titleColor.Inherit = false; try { _titleColor.Color = Color.Parse(style.TitleColor); } catch { } }

        _watermark.Text = style.Watermark;
        _watermarkImg.Text = style.WatermarkImage;
        _wmAngle.Text = style.WatermarkAngle.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _logo.Text = style.LogoPath;
        foreach (var c in Enum.GetValues<DecorPos>()) _corner.Items.Add(c);
        _corner.SelectedItem = style.LogoPosition;

        // Info field setup.
        _showInfo.Content = Loc.S("Decor_ShowInfo"); _showInfo.IsChecked = style.ShowInfo;
        Ui.Theme(_showInfo, CheckBox.ForegroundProperty, "ContentTextBrush");
        foreach (var p in Enum.GetValues<DecorPos>()) _infoPos.Items.Add(p);
        _infoPos.SelectedItem = style.InfoPosition;
        _infoName.Text = style.InfoName; _infoProject.Text = style.InfoProject; _infoProjectNo.Text = style.InfoProjectNo;
        _infoVersion.Text = style.InfoVersion; _infoDate.Text = style.InfoDate; _infoAuthor.Text = style.InfoAuthor;
        _infoDept.Text = style.InfoDepartment; _infoExtra.Text = style.InfoExtra;
        foreach (var b in new[] { _infoName, _infoProject, _infoProjectNo, _infoVersion, _infoDate, _infoAuthor, _infoDept, _infoExtra })
            ThemeInput(b);

        // Autofill suggestions for empty fields: project name, the diagram/function name, today's date, and the
        // user name from Options. Only fill empties so an existing header is never overwritten.
        if (string.IsNullOrWhiteSpace(_infoProject.Text) && !string.IsNullOrWhiteSpace(projectName)) _infoProject.Text = projectName;
        if (string.IsNullOrWhiteSpace(_infoName.Text)    && !string.IsNullOrWhiteSpace(title))       _infoName.Text = title;
        if (string.IsNullOrWhiteSpace(_infoDate.Text))                                                _infoDate.Text = DateTime.Now.ToString("d");
        if (string.IsNullOrWhiteSpace(_infoAuthor.Text)  && !string.IsNullOrWhiteSpace(AppSettings.UserName)) _infoAuthor.Text = AppSettings.UserName;
        if (string.IsNullOrWhiteSpace(_infoDept.Text)    && !string.IsNullOrWhiteSpace(AppSettings.UserDepartment)) _infoDept.Text = AppSettings.UserDepartment;

        // Date field with a "today" (calendar) button that (re)sets it to the current date.
        var todayBtn = Ui.Btn("📅"); todayBtn.Padding = new(8, 4); ToolTip.SetTip(todayBtn, Loc.S("Decor_Today"));
        todayBtn.Click += (_, _) => _infoDate.Text = DateTime.Now.ToString("d");
        var dateCell = new Grid { ColumnSpacing = 4, ColumnDefinitions = new("*,Auto") };
        _infoDate.SetValue(Grid.ColumnProperty, 0); todayBtn.SetValue(Grid.ColumnProperty, 1);
        dateCell.Children.Add(_infoDate); dateCell.Children.Add(todayBtn);

        var browse = Ui.Btn(Loc.S("Decor_Browse"));
        browse.Click += async (_, _) => { var p = await PickImageAsync(); if (p is not null) _logo.Text = p; };
        var clear = Ui.Btn(Loc.S("Decor_ClearLogo"));
        clear.Click += (_, _) => _logo.Text = "";

        var browseWm = Ui.Btn(Loc.S("Decor_Browse"));
        browseWm.Click += async (_, _) => { var p = await PickImageAsync(); if (p is not null) _watermarkImg.Text = p; };
        var clearWm = Ui.Btn(Loc.S("Decor_ClearLogo"));
        clearWm.Click += (_, _) => _watermarkImg.Text = "";

        var ok = Ui.Btn(Loc.S("Common_OK")); ok.IsDefault = true; ok.Click += (_, _) => Apply();
        var cancel = Ui.Btn(Loc.S("Common_Cancel")); cancel.IsCancel = true; cancel.Click += (_, _) => Close(null);

        // Optional "get from flowchart": copies the matching PAP's whole header (contents + positions) here.
        Button? fromPapBtn = null;
        if (fromPap is not null)
        {
            fromPapBtn = Ui.Btn(Loc.S("Decor_FromPap"));
            fromPapBtn.Click += (_, _) => { if (fromPap() is { } src) ImportFrom(src.style, src.title); };
        }

        // Save the current header as a reusable template / apply a saved one.
        var saveTpl = Ui.Btn(Loc.S("Decor_SaveTemplate"));
        saveTpl.Click += async (_, _) =>
        {
            var n = await PromptDialog.Show(this, Loc.S("Decor_TemplateName"), "");
            if (string.IsNullOrWhiteSpace(n)) return;
            var tmp = new DiagramStyle(); ApplyTo(tmp);
            HeaderTemplateService.Save(n.Trim(), HeaderData.Capture(tmp));
        };
        var applyTpl = Ui.Btn(Loc.S("Decor_ApplyTemplate"));
        applyTpl.Click += async (_, _) =>
        {
            var n = await TemplatePickerDialog.Show(this);
            if (n is not null && HeaderTemplateService.Load(n) is { } h) { var s = new DiagramStyle(); h.ApplyTo(s); PopulateHeader(s); }
        };
        var tplRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { saveTpl, applyTpl } };

        // A text field followed by its action buttons: the field fills, the buttons keep their full width
        // (a plain horizontal StackPanel squeezed the buttons in the fixed-width window — "Durchsuchen" → "L").
        static Grid FieldRow(Control field, params Control[] buttons)
        {
            var g = new Grid { ColumnSpacing = 8 };
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            field.SetValue(Grid.ColumnProperty, 0); g.Children.Add(field);
            for (int i = 0; i < buttons.Length; i++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                buttons[i].SetValue(Grid.ColumnProperty, i + 1);
                g.Children.Add(buttons[i]);
            }
            return g;
        }

        // The six short info fields in a compact two-pair grid (label + box, label + box).
        Grid InfoGrid()
        {
            var g = new Grid { ColumnSpacing = 8, RowSpacing = 6 };
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            (string key, Control box)[] pairs =
            {
                ("Decor_InfoName", _infoName), ("Decor_InfoProject", _infoProject),
                ("Decor_InfoProjectNo", _infoProjectNo), ("Decor_InfoVersion", _infoVersion),
                ("Decor_InfoDate", dateCell), ("Decor_InfoAuthor", _infoAuthor),
                ("Decor_InfoDept", _infoDept),
            };
            for (int i = 0; i < pairs.Length; i++)
            {
                int row = i / 2, col = (i % 2) * 2;
                if (col == 0) g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var lbl = Label(Loc.S(pairs[i].key));
                lbl.VerticalAlignment = VerticalAlignment.Center;
                lbl.SetValue(Grid.RowProperty, row); lbl.SetValue(Grid.ColumnProperty, col); g.Children.Add(lbl);
                pairs[i].box.SetValue(Grid.RowProperty, row); pairs[i].box.SetValue(Grid.ColumnProperty, col + 1);
                g.Children.Add(pairs[i].box);
            }
            return g;
        }

        var infoHeader = Label(Loc.S("Decor_InfoSection")); infoHeader.FontWeight = FontWeight.SemiBold;

        var panel = new StackPanel
        {
            Margin = new(18), Spacing = 10,
            Children =
            {
                Label(Loc.S("Decor_TitleText")), _title,
                _showTitle,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
                    Children = { Label(Loc.S("Decor_TitlePos")), _titlePos, Label(Loc.S("Decor_TitleSize")), _titleSize, _titleBold } },
                _titleColor,
                Label(Loc.S("Decor_Watermark")), _watermark,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
                    Children = { Label(Loc.S("Decor_WmAngle")), _wmAngle } },
                Label(Loc.S("Decor_WatermarkImg")),
                FieldRow(_watermarkImg, browseWm, clearWm),
                Label(Loc.S("Decor_Logo")),
                FieldRow(_logo, browse, clear),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
                    Children = { Label(Loc.S("Decor_Corner")), _corner } },
                new Border { BorderThickness = new(0, 1, 0, 0), Margin = new(0, 4, 0, 0), Padding = new(0, 8, 0, 0),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)), Child = infoHeader },
                _showInfo,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
                    Children = { Label(Loc.S("Decor_InfoPos")), _infoPos } },
                InfoGrid(),
                Label(Loc.S("Decor_InfoExtra")), _infoExtra,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new(0, 6, 0, 0), Children = { cancel, ok } },
            },
        };
        panel.Children.Insert(0, tplRow);                                   // template save/apply at the top
        if (fromPapBtn is not null) panel.Children.Insert(0, fromPapBtn);   // "get from flowchart" above that

        Content = new ScrollViewer { MaxHeight = 680, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
    }

    // Copies a whole header (the PAP's) into the dialog controls: contents and positions.
    void ImportFrom(DiagramStyle s, string title) { _title.Text = title; PopulateHeader(s); }

    // Loads the header fields of a style into the controls (no title text).
    void PopulateHeader(DiagramStyle s)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        _showTitle.IsChecked = s.ShowTitle; _titlePos.SelectedItem = s.TitlePosition;
        _titleSize.Text = s.TitleFontSize.ToString(inv); _titleBold.IsChecked = s.TitleBold;
        if (string.IsNullOrWhiteSpace(s.TitleColor)) _titleColor.Inherit = true;
        else { _titleColor.Inherit = false; try { _titleColor.Color = Color.Parse(s.TitleColor); } catch { } }
        _watermark.Text = s.Watermark; _watermarkImg.Text = s.WatermarkImage; _wmAngle.Text = s.WatermarkAngle.ToString(inv);
        _logo.Text = s.LogoPath; _corner.SelectedItem = s.LogoPosition;
        _showInfo.IsChecked = s.ShowInfo; _infoPos.SelectedItem = s.InfoPosition;
        _infoName.Text = s.InfoName; _infoProject.Text = s.InfoProject; _infoProjectNo.Text = s.InfoProjectNo;
        _infoVersion.Text = s.InfoVersion; _infoDate.Text = s.InfoDate; _infoAuthor.Text = s.InfoAuthor; _infoExtra.Text = s.InfoExtra;
    }

    // Writes the edits back into the style and closes with the (possibly changed) title.
    void Apply() { ApplyTo(_style); Close((_title.Text ?? "").Trim()); }

    // Writes the current control values into a style's header fields (used by Apply and by "save as template").
    void ApplyTo(DiagramStyle s)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        s.ShowTitle      = _showTitle.IsChecked == true;
        s.TitlePosition  = _titlePos.SelectedItem is DecorPos tp ? tp : s.TitlePosition;
        if (double.TryParse(_titleSize.Text, System.Globalization.NumberStyles.Any, inv, out var ts) && ts > 0) s.TitleFontSize = ts;
        s.TitleBold      = _titleBold.IsChecked == true;
        s.TitleColor     = _titleColor.Inherit ? "" : HexColorPicker.HexOf(_titleColor.Color);
        s.Watermark      = (_watermark.Text ?? "").Trim();
        s.WatermarkImage = (_watermarkImg.Text ?? "").Trim();
        if (double.TryParse(_wmAngle.Text, System.Globalization.NumberStyles.Any, inv, out var wa)) s.WatermarkAngle = wa;
        s.LogoPath       = (_logo.Text ?? "").Trim();
        s.LogoPosition   = _corner.SelectedItem is DecorPos c ? c : s.LogoPosition;

        s.ShowInfo       = _showInfo.IsChecked == true;
        s.InfoPosition   = _infoPos.SelectedItem is DecorPos ip ? ip : s.InfoPosition;
        s.InfoName       = (_infoName.Text ?? "").Trim();
        s.InfoProject    = (_infoProject.Text ?? "").Trim();
        s.InfoProjectNo  = (_infoProjectNo.Text ?? "").Trim();
        s.InfoVersion    = (_infoVersion.Text ?? "").Trim();
        s.InfoDate       = (_infoDate.Text ?? "").Trim();
        s.InfoAuthor     = (_infoAuthor.Text ?? "").Trim();
        s.InfoDepartment = (_infoDept.Text ?? "").Trim();
        s.InfoExtra      = (_infoExtra.Text ?? "").TrimEnd();
    }

    async Task<string?> PickImageAsync()
    {
        var res = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.S("Decor_Logo"), AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Images") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.svg" } } },
        });
        return res.Count > 0 ? res[0].TryGetLocalPath() : null;
    }

    TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text };
        Ui.Theme(t, TextBlock.ForegroundProperty, "ContentTextBrush");
        return t;
    }

    static void ThemeInput(TextBox b)
    {
        Ui.Theme(b, TextBox.BackgroundProperty,  "InputBgBrush");
        Ui.Theme(b, TextBox.ForegroundProperty,  "SidebarTextBrush");
        Ui.Theme(b, TextBox.BorderBrushProperty, "ControlBorderBrush");
    }
}
