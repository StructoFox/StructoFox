using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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

    public static Task<string?> Show(Window owner, string title, DiagramStyle style)
        => new DiagramDecorDialog(title, style).ShowDialog<string?>(owner);

    DiagramDecorDialog(string title, DiagramStyle style)
    {
        _style = style;
        Title                 = Loc.S("Decor_Title");
        Width                 = 460;
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

        foreach (var p in Enum.GetValues<TitlePos>()) _titlePos.Items.Add(p);
        _titlePos.SelectedItem = style.TitlePosition;
        _titleSize.Text = style.TitleFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(style.TitleColor)) _titleColor.Inherit = true;
        else { _titleColor.Inherit = false; try { _titleColor.Color = Color.Parse(style.TitleColor); } catch { } }

        _watermark.Text = style.Watermark;
        _watermarkImg.Text = style.WatermarkImage;
        _wmAngle.Text = style.WatermarkAngle.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _logo.Text = style.LogoPath;
        foreach (var c in Enum.GetValues<DecorCorner>()) _corner.Items.Add(c);
        _corner.SelectedItem = style.LogoCorner;

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

        Content = new StackPanel
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
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _watermarkImg, browseWm, clearWm } },
                Label(Loc.S("Decor_Logo")),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _logo, browse, clear } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
                    Children = { Label(Loc.S("Decor_Corner")), _corner } },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new(0, 6, 0, 0), Children = { cancel, ok } },
            },
        };
    }

    // Writes the edits back into the style and closes with the (possibly changed) title.
    void Apply()
    {
        _style.ShowTitle      = _showTitle.IsChecked == true;
        _style.TitlePosition  = _titlePos.SelectedItem is TitlePos tp ? tp : _style.TitlePosition;
        if (double.TryParse(_titleSize.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var ts) && ts > 0) _style.TitleFontSize = ts;
        _style.TitleBold      = _titleBold.IsChecked == true;
        _style.TitleColor     = _titleColor.Inherit ? "" : HexColorPicker.HexOf(_titleColor.Color);
        _style.Watermark      = (_watermark.Text ?? "").Trim();
        _style.WatermarkImage = (_watermarkImg.Text ?? "").Trim();
        if (double.TryParse(_wmAngle.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var wa)) _style.WatermarkAngle = wa;
        _style.LogoPath       = (_logo.Text ?? "").Trim();
        _style.LogoCorner = _corner.SelectedItem is DecorCorner c ? c : _style.LogoCorner;
        Close((_title.Text ?? "").Trim());
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
