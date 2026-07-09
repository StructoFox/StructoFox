using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace StructoFox.App;

/// <summary>
/// Asks where to create a new project and what to call it. With no libraries yet, it offers a default
/// "Documents/StructoFox" folder + Browse; with libraries, it lists them (with free disk space) to pick
/// from. Returns (parentFolder, projectName) on OK, or null on cancel.
/// </summary>
public class NewProjectDialog : Window
{
    readonly TextBox  _name   = new() { PlaceholderText = "MyProject", MinWidth = 280, MaxLength = StructoFox.Core.ProjectService.MaxNameLength };
    readonly TextBox  _folder = new() { MinWidth = 320 };
    readonly ComboBox _lang   = new() { MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left };

    public static Task<(string parent, string name, string language)?> Show(Window owner, List<string> libraries)
        => new NewProjectDialog(libraries).ShowDialog<(string parent, string name, string language)?>(owner);

    NewProjectDialog(List<string> libraries)
    {
        // Drop library folders that no longer exist (e.g. the user deleted them) so stale entries aren't offered.
        libraries = libraries.Where(System.IO.Directory.Exists).ToList();

        Title                 = Loc.S("NewProj_Title");
        Width                 = 520;
        SizeToContent         = SizeToContent.Height;
        CanResize             = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Ui.ThemeWindow(this);

        var col = new StackPanel { Margin = new(18), Spacing = 12 };

        if (libraries.Count == 0)
        {
            col.Children.Add(WrapText(Loc.S("NewProj_NoLib")));
            _folder.Text = DefaultLibraryPath();
        }
        else
        {
            col.Children.Add(WrapText(Loc.S("NewProj_ChooseLib")));
            _folder.Text = libraries[0];
            var libPanel = new StackPanel { Spacing = 6 };
            foreach (var lib in libraries) libPanel.Children.Add(LibraryChoice(lib));
            // Cap the list: beyond ~4 libraries it scrolls instead of growing the (non-resizable) window.
            col.Children.Add(new ScrollViewer { Content = libPanel, MaxHeight = 248, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }

        var browse = Ui.Btn(Loc.S("NewProj_Browse"));
        browse.Click += async (_, _) => { var p = await PickFolderAsync(); if (p is not null) _folder.Text = p; };
        col.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _folder, browse } });

        col.Children.Add(new TextBlock { Text = Loc.S("NewProj_Name") });
        col.Children.Add(_name);

        // Code syntax for the autocomplete (per project; changeable later in the diagram toolbar).
        col.Children.Add(new TextBlock { Text = Loc.S("NewProj_Syntax") });
        foreach (var (_, label) in FlowChartWindow.AuthorLanguages) _lang.Items.Add(label);
        _lang.SelectedIndex = 0;
        col.Children.Add(_lang);

        var ok = Ui.Btn(Loc.S("Common_OK")); ok.IsDefault = true;
        ok.Click += (_, _) =>
        {
            var parent = (_folder.Text ?? "").Trim();
            var name   = (_name.Text ?? "").Trim();
            var lang   = FlowChartWindow.AuthorLanguages[Math.Max(0, _lang.SelectedIndex)].Lang.ToString();
            if (parent.Length > 0 && name.Length > 0) Close((parent, name, lang));
        };
        var cancel = Ui.Btn(Loc.S("Common_Cancel")); cancel.IsCancel = true; cancel.Click += (_, _) => Close(null);
        col.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } });

        Content = col;
    }

    // A clickable library row: folder name, full path, and free disk space; fills the folder field.
    Control LibraryChoice(string lib)
    {
        var btn = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new(8, 6), CornerRadius = new(6) };
        Ui.Theme(btn, TemplatedControl.BackgroundProperty, "ControlBgBrush");
        Ui.Theme(btn, TemplatedControl.ForegroundProperty, "SidebarTextBrush");
        btn.Content = new StackPanel
        {
            Spacing = 1,
            Children =
            {
                new TextBlock { Text = ShortName(lib), FontWeight = FontWeight.Bold },
                new TextBlock { Text = lib, FontSize = 10, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = FreeSpace(lib), FontSize = 10, Opacity = 0.6 },
            },
        };
        btn.Click += (_, _) => _folder.Text = lib;
        return btn;
    }

    async Task<string?> PickFolderAsync()
    {
        var res = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.S("NewProj_Title"), AllowMultiple = false });
        return res.Count > 0 ? res[0].TryGetLocalPath() : null;
    }

    // The default project library: <Documents>/StructoFox (resolves per-OS via the Documents folder).
    static string DefaultLibraryPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "StructoFox");

    // "123,456 MB free on C:" for the drive that holds the given path; empty if it can't be read.
    static string FreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return "";
            var mb = new DriveInfo(root).AvailableFreeSpace / (1024 * 1024);
            return string.Format(Loc.S("NewProj_FreeFmt"), mb.ToString("N0"), root.TrimEnd('/', '\\'));
        }
        catch { return ""; }
    }

    static string ShortName(string path) => Path.GetFileName(path.TrimEnd('/', '\\'));

    TextBlock WrapText(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        Ui.Theme(t, TextBlock.ForegroundProperty, "ContentTextBrush");
        return t;
    }
}
