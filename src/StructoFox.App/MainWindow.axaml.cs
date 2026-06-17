using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace StructoFox.App;

/// <summary>
/// The StructoFox shell. Opens on a project browser (recent projects + add folder); opening a
/// project switches to the "Dev Cockpit" — a left icon rail of sections over a themed, honeycombed
/// content area. Theme + palette live in a discreet top-bar menu, not in the foreground.
/// </summary>
public partial class MainWindow : Window
{
    enum Section { Boards, Classes, Functions, Export }

    static readonly FontFamily Mono = new("Consolas, Menlo, Courier New, monospace");

    string? _project;
    Section _section = Section.Functions;
    readonly ContentControl _body    = new();   // home browser  OR  project cockpit
    readonly ContentControl _content = new();   // the active section inside the cockpit
    readonly Dictionary<Section, Button> _railButtons = new();

    // Builds the shell window and shows the project browser.
    public MainWindow()
    {
        InitializeComponent();
        Title = "StructoFox";
        Width = 1140; Height = 740; MinWidth = 760; MinHeight = 480;

        // Drop the OS title bar (keep the resizable border) so our top bar is the themed title bar.
        this.WindowDecorations = global::Avalonia.Controls.WindowDecorations.BorderOnly;
        Ui.ThemeWindow(this);

        Content = BuildShell();
        ShowHome();
    }

    // Top bar (row 0) over the swappable body (row 1).
    Control BuildShell()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        var top = BuildTopBar(); Grid.SetRow(top, 0); root.Children.Add(top);
        Grid.SetRow(_body, 1); root.Children.Add(_body);
        return root;
    }

    // The themed title bar: fox brand (drag handle) + a discreet menu and window controls.
    Control BuildTopBar()
    {
        var bar = new Border { Padding = new(14, 8) };
        Ui.Theme(bar, Border.BackgroundProperty, "SidebarBgBrush");

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new TextBlock { Text = "🦊", FontSize = 22, VerticalAlignment = VerticalAlignment.Center });
        var title = new TextBlock { Text = "StructoFox", FontSize = 17, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        Ui.Theme(title, TextBlock.ForegroundProperty, "SidebarTextBrush");
        var tag = new TextBlock { Text = "Flow · Struct · Code", FontSize = 11, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(2, 0, 0, 2) };
        Ui.Theme(tag, TextBlock.ForegroundProperty, "SidebarTextBrush");
        brand.Children.Add(title);
        brand.Children.Add(tag);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var menu = Ui.Btn("≡", "Menu");
        menu.Click += (_, _) => OpenMainMenu(menu);
        right.Children.Add(menu);
        right.Children.Add(new Border { Width = 6 });
        right.Children.Add(WinButton("—", "Minimize", () => WindowState = WindowState.Minimized));
        right.Children.Add(WinButton("▢", "Maximize / restore",
            () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized));
        right.Children.Add(WinButton("✕", "Close", Close));

        var dock = new DockPanel();
        DockPanel.SetDock(brand, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(brand);
        dock.Children.Add(right);
        bar.Child = dock;

        bar.PointerPressed += (_, e) => { if (e.GetCurrentPoint(bar).Properties.IsLeftButtonPressed) BeginMoveDrag(e); };
        bar.DoubleTapped   += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        return bar;
    }

    // The discreet app menu: projects/home, theme submenu, palette editor — opened from the ≡ button.
    void OpenMainMenu(Control anchor)
    {
        var cm = new ContextMenu();

        var home = new MenuItem { Header = "🏠 Projects" };
        home.Click += (_, _) => ShowHome();
        cm.Items.Add(home);
        if (_project is not null)
        {
            var close = new MenuItem { Header = "Close project" };
            close.Click += (_, _) => ShowHome();
            cm.Items.Add(close);
        }

        cm.Items.Add(new Separator());

        var theme = new MenuItem { Header = "🎨 Theme" };
        foreach (var (name, path) in ThemeManager.Available())
        {
            var p = path;
            var mi = new MenuItem { Header = name };
            mi.Click += (_, _) => ThemeManager.Apply(Application.Current!, p);
            theme.Items.Add(mi);
        }
        cm.Items.Add(theme);

        var pal = new MenuItem { Header = "Palette editor…" };
        pal.Click += (_, _) => new PaletteEditorWindow().Show();
        cm.Items.Add(pal);

        cm.Open(anchor);
    }

    // A small themed window-control button (minimise / maximise / close).
    Button WinButton(string glyph, string tip, Action action)
    {
        var b = Ui.Btn(glyph, tip);
        b.Padding = new(10, 4);
        b.Click += (_, _) => action();
        return b;
    }

    // ── Home: project browser ──────────────────────────────────────────────

    // Shows the project browser (no project open).
    void ShowHome()
    {
        _project = null;
        _body.Content = BuildHome();
    }

    // The landing surface: the most-recent project up top, the rest below, and "add folder".
    Control BuildHome()
    {
        var host = new Border();
        Ui.Theme(host, Border.BackgroundProperty, "ContentBgBrush");

        var layered = new Grid();
        var comb = new HoneycombBackdrop();
        Ui.Theme(comb, HoneycombBackdrop.LineBrushProperty, "AccentBgBrush");
        layered.Children.Add(comb);

        var col = new StackPanel { Spacing = 12, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 48, 0, 24) };
        col.Children.Add(Centered(Heading("Open a project"), 26));

        var recents = RecentProjects.Load();
        if (recents.Count > 0)
        {
            col.Children.Add(BigProjectCard(recents[0]));
            if (recents.Count > 1)
            {
                col.Children.Add(new TextBlock { Text = "Recently opened", FontSize = 12, Opacity = 0.7, Margin = new(2, 10, 0, 0) });
                for (int i = 1; i < recents.Count; i++) col.Children.Add(SmallProjectRow(recents[i]));
            }
        }
        else
        {
            col.Children.Add(Note("No recent projects yet. Add a folder to get started."));
        }

        var add = Ui.Btn("➕  Add project folder…", "Pick a folder to use as a project");
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new(0, 12, 0, 0);
        add.Click += async (_, _) => await PickFolder();
        col.Children.Add(add);

        layered.Children.Add(new ScrollViewer { Padding = new(24), Content = col, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        host.Child = layered;
        return host;
    }

    // The prominent card for the most-recently opened project.
    Control BigProjectCard(string path)
    {
        var card = new Border { Padding = new(18, 16), CornerRadius = new(8), Cursor = new Cursor(StandardCursorType.Hand) };
        Ui.Theme(card, Border.BackgroundProperty, "ControlBgBrush");
        Ui.Theme(card, Border.BorderBrushProperty, "AccentBgBrush");
        card.BorderThickness = new(1);

        var name = new TextBlock { Text = ProjectName(path), FontSize = 20, FontWeight = FontWeight.Bold };
        Ui.Theme(name, TextBlock.ForegroundProperty, "ContentTextBrush");
        var loc = new TextBlock { Text = path, FontSize = 11, FontFamily = Mono, Opacity = 0.6, TextWrapping = TextWrapping.Wrap };
        Ui.Theme(loc, TextBlock.ForegroundProperty, "ContentTextBrush");
        card.Child = new StackPanel { Spacing = 4, Children = { name, loc } };
        card.PointerPressed += (_, _) => OpenProject(path);
        return card;
    }

    // A compact clickable row for an older recent project.
    Control SmallProjectRow(string path)
    {
        var row = new Border { Padding = new(10, 7), CornerRadius = new(6), Cursor = new Cursor(StandardCursorType.Hand) };
        var name = new TextBlock { Text = ProjectName(path), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        Ui.Theme(name, TextBlock.ForegroundProperty, "ContentTextBrush");
        var loc = new TextBlock { Text = path, FontSize = 10, FontFamily = Mono, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        Ui.Theme(loc, TextBlock.ForegroundProperty, "ContentTextBrush");
        var dock = new DockPanel();
        DockPanel.SetDock(name, Dock.Left);
        dock.Children.Add(name);
        dock.Children.Add(loc);
        row.Child = dock;
        row.PointerPressed += (_, _) => OpenProject(path);
        return row;
    }

    // Opens the system folder picker and, on pick, opens that folder as a project.
    async Task PickFolder()
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select a project folder", AllowMultiple = false,
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } local)
            OpenProject(local);
    }

    // ── Project cockpit ─────────────────────────────────────────────────────

    // Records the project as recent and switches to the cockpit view.
    void OpenProject(string path)
    {
        _project = path;
        RecentProjects.Add(path);
        _railButtons.Clear();
        _body.Content = BuildCockpit();
        ShowSection(_section);
    }

    // The cockpit: a left icon rail beside the themed, honeycombed content area.
    Control BuildCockpit()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var rail = BuildRail(); Grid.SetColumn(rail, 0); grid.Children.Add(rail);
        var host = BuildContentHost(); Grid.SetColumn(host, 1); grid.Children.Add(host);
        return grid;
    }

    // The left icon rail: one button per section, the active one accent-highlighted.
    Control BuildRail()
    {
        var rail = new Border { Width = 84, Padding = new(6, 10) };
        Ui.Theme(rail, Border.BackgroundProperty, "SidebarBgBrush");

        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(RailButton(Section.Boards,    "🗂", "Boards"));
        stack.Children.Add(RailButton(Section.Classes,   "🧩", "Classes"));
        stack.Children.Add(RailButton(Section.Functions, "ƒ",  "Functions"));
        stack.Children.Add(RailButton(Section.Export,    "⇩",  "Export"));
        rail.Child = stack;
        return rail;
    }

    // Builds one rail button (icon over label) wired to switch sections.
    Button RailButton(Section section, string icon, string label)
    {
        var b = new Button
        {
            Padding = new(0, 8), CornerRadius = new(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Content = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = icon, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = label, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };
        b.Click += (_, _) => ShowSection(section);
        _railButtons[section] = b;
        return b;
    }

    // The content area: a faint themed honeycomb backdrop with the scrollable section view on top.
    Control BuildContentHost()
    {
        var host = new Border();
        Ui.Theme(host, Border.BackgroundProperty, "ContentBgBrush");

        var layered = new Grid();
        var comb = new HoneycombBackdrop();
        Ui.Theme(comb, HoneycombBackdrop.LineBrushProperty, "AccentBgBrush");
        layered.Children.Add(comb);
        layered.Children.Add(new ScrollViewer { Padding = new(24), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _content });
        host.Child = layered;
        return host;
    }

    // Switches the active section: restyle the rail and rebuild the content view.
    void ShowSection(Section section)
    {
        _section = section;
        foreach (var (s, b) in _railButtons)
        {
            var active = s == section;
            Ui.Theme(b, TemplatedControl.BackgroundProperty, active ? "AccentBgBrush"  : "ControlBgBrush");
            Ui.Theme(b, TemplatedControl.ForegroundProperty, active ? "AccentTextBrush" : "SidebarTextBrush");
        }
        _content.Content = BuildSectionView(section);
    }

    // Builds the view for a section — a heading + (for now) a hint or the working demo actions.
    Control BuildSectionView(Section section)
    {
        var root = new StackPanel { Spacing = 12, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };

        root.Children.Add(new TextBlock { Text = ProjectName(_project ?? ""), FontFamily = Mono, FontSize = 11, Opacity = 0.6 });

        var (title, blurb) = section switch
        {
            Section.Boards    => ("Boards",    "Structure boards — arrange entities on a canvas. (Board canvas port coming.)"),
            Section.Classes   => ("Classes",   "Namespaces, classes, structs, interfaces, enums & objects. (Entity editor port coming.)"),
            Section.Functions => ("Functions", "Functions & methods — sketch their logic as a flowchart or structogram."),
            _                 => ("Export",    "Generate source from your structures in 10 languages. (Wiring coming.)"),
        };
        root.Children.Add(Heading(title));
        root.Children.Add(Note(blurb));

        if (section == Section.Functions && _project is not null)
        {
            var open = Ui.Btn("🔁 New diagram (demo)", "Open the PAP / structogram chooser");
            open.HorizontalAlignment = HorizontalAlignment.Left;
            open.Click += async (_, _) => await DiagramLauncher.ChooseAndOpen(this, _project!, "demo", "Greeter.Greet()", null);
            root.Children.Add(open);
        }
        return root;
    }

    // ── small helpers ────────────────────────────────────────────────────────

    // Folder name as the project's display name.
    static string ProjectName(string path) => string.IsNullOrEmpty(path) ? "—" : Path.GetFileName(path.TrimEnd('/', '\\'));

    static TextBlock Heading(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 22, FontWeight = FontWeight.Bold };
        Ui.Theme(t, TextBlock.ForegroundProperty, "ContentTextBrush");
        return t;
    }

    static TextBlock Note(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        Ui.Theme(t, TextBlock.ForegroundProperty, "ContentTextBrush");
        return t;
    }

    // Wraps a control in a horizontally-centred container with a top margin.
    static Control Centered(Control c, double topMargin)
    {
        c.HorizontalAlignment = HorizontalAlignment.Center;
        return new StackPanel { Margin = new(0, 0, 0, topMargin), Children = { c } };
    }
}
