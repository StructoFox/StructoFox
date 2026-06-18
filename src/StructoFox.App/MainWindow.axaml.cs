using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using StructoFox.Core;

namespace StructoFox.App;

/// <summary>
/// The StructoFox shell. Opens on a project browser (recent projects + add folder); opening a
/// project switches to the "Dev Cockpit" — a left icon rail of sections over a themed, honeycombed
/// content area. Theme + palette live in a discreet top-bar menu, not in the foreground.
/// </summary>
public partial class MainWindow : Window
{
    enum Section { Boards, Classes, Functions, Export }
    enum HomeView { Cards, Grid, List }
    enum HomeSort { DateDesc, DateAsc, NameAsc, NameDesc }

    static readonly FontFamily Mono = new("Consolas, Menlo, Courier New, monospace");

    string? _project;
    Section _section = Section.Functions;
    readonly ContentControl _body    = new();   // home browser  OR  project cockpit
    readonly ContentControl _content = new();   // the active section inside the cockpit
    readonly Dictionary<Section, Button> _railButtons = new();

    // Home browser state.
    string?  _homeSource;                        // null = Recent, else a library path
    HomeView _homeView   = HomeView.Cards;
    HomeSort _homeSort   = HomeSort.DateDesc;
    string   _homeFilter = "";
    bool     _homeFilterOpen;
    readonly ContentControl _homeList = new();   // the project-list region (refreshed in place)
    StackPanel? _homeFilterBar;

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

    // The landing surface: a two-column hub — sources nav on the left, hero + project list on the right.
    Control BuildHome()
    {
        var host = new Border();
        Ui.Theme(host, Border.BackgroundProperty, "ContentBgBrush");

        var layered = new Grid();
        var comb = new HoneycombBackdrop();
        Ui.Theme(comb, HoneycombBackdrop.LineBrushProperty, "AccentBgBrush");
        layered.Children.Add(comb);

        var cols = new Grid();
        cols.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(220)));
        cols.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var nav = BuildHomeNav(); Grid.SetColumn(nav, 0); cols.Children.Add(nav);
        var main = BuildHomeMain(); Grid.SetColumn(main, 1); cols.Children.Add(main);
        layered.Children.Add(cols);

        host.Child = layered;
        RefreshHomeList();
        return host;
    }

    // Left column: New/Add-library actions + a sources list (Recent + each library; libraries removable).
    Control BuildHomeNav()
    {
        var panel = new StackPanel { Spacing = 6 };
        var box = new Border { Width = 220, Padding = new(12) };
        Ui.Theme(box, Border.BackgroundProperty, "SidebarBgBrush");
        box.Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var newBtn = Ui.Btn("➕  New project");
        newBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        newBtn.Click += async (_, _) => await NewProject();
        var libBtn = Ui.Btn("📁  Add library");
        libBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        libBtn.Click += async (_, _) => await AddLibrary();
        panel.Children.Add(newBtn);
        panel.Children.Add(libBtn);

        panel.Children.Add(SectionLabel("Sources"));
        panel.Children.Add(NavEntry("🕘  Recent", null, false));
        foreach (var lib in Libraries.Load()) panel.Children.Add(NavEntry("📁  " + ShortName(lib), lib, true));
        return box;
    }

    // One sources entry; clicking selects it as the list source. Libraries get a remove (✕) button.
    Control NavEntry(string label, string? source, bool removable)
    {
        var active = _homeSource == source;
        var b = new Button
        {
            Content = label, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new(8, 6), CornerRadius = new(6),
        };
        Ui.Theme(b, TemplatedControl.BackgroundProperty, active ? "AccentBgBrush"  : "ControlBgBrush");
        Ui.Theme(b, TemplatedControl.ForegroundProperty, active ? "AccentTextBrush" : "SidebarTextBrush");
        b.Click += (_, _) => { _homeSource = source; _body.Content = BuildHome(); };

        if (!removable) return b;

        var dock = new DockPanel();
        var x = Ui.Btn("✕", "Remove this library"); x.Padding = new(6, 2);
        x.Click += (_, _) => { if (source is not null) Libraries.Remove(source); if (_homeSource == source) _homeSource = null; _body.Content = BuildHome(); };
        DockPanel.SetDock(x, Dock.Right);
        dock.Children.Add(x);
        dock.Children.Add(b);
        return dock;
    }

    // Right column: hero (most-recent project) over a toolbar, an optional filter bar, and the list.
    Control BuildHomeMain()
    {
        var grid = new Grid { Margin = new(20) };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // hero
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // toolbar
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // filter bar
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));   // list

        var hero = BuildHero(); Grid.SetRow(hero, 0); grid.Children.Add(hero);
        var toolbar = BuildHomeToolbar(); Grid.SetRow(toolbar, 1); grid.Children.Add(toolbar);
        _homeFilterBar = BuildFilterBar(); _homeFilterBar.IsVisible = _homeFilterOpen; Grid.SetRow(_homeFilterBar, 2); grid.Children.Add(_homeFilterBar);
        var listScroll = new ScrollViewer { Content = _homeList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 8, 0, 0) };
        Grid.SetRow(listScroll, 3); grid.Children.Add(listScroll);
        return grid;
    }

    // The "continue" hero: the most-recently opened project, prominently. Empty if there is none.
    Control BuildHero()
    {
        var last = RecentProjects.Load().FirstOrDefault();
        return last is null ? new Border { Height = 0 } : ProjectCard(last.Path, last.Opened, big: true);
    }

    // Toolbar: the source title, the view switcher (cards / multi-list / list), and a filter toggle.
    Control BuildHomeToolbar()
    {
        var dock = new DockPanel { Margin = new(0, 14, 0, 0) };
        var title = new TextBlock { Text = _homeSource is null ? "Recent" : ShortName(_homeSource), FontSize = 16, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        Ui.Theme(title, TextBlock.ForegroundProperty, "ContentTextBrush");

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(ViewBtn("▦", HomeView.Cards, "Cards"));
        right.Children.Add(ViewBtn("☷", HomeView.Grid, "Multi-column list"));
        right.Children.Add(ViewBtn("≣", HomeView.List, "List with date"));
        var filter = Ui.Btn("🔎 Filter", "Show filter & sort");
        filter.Click += (_, _) => { _homeFilterOpen = !_homeFilterOpen; if (_homeFilterBar is not null) _homeFilterBar.IsVisible = _homeFilterOpen; };
        right.Children.Add(filter);
        DockPanel.SetDock(right, Dock.Right);

        dock.Children.Add(right);
        dock.Children.Add(title);
        return dock;
    }

    // One view-switcher button, accent-highlighted when its view is active.
    Button ViewBtn(string glyph, HomeView view, string tip)
    {
        var b = Ui.Btn(glyph, tip); b.Padding = new(9, 4);
        var active = _homeView == view;
        Ui.Theme(b, TemplatedControl.BackgroundProperty, active ? "AccentBgBrush"  : "ControlBgBrush");
        Ui.Theme(b, TemplatedControl.ForegroundProperty, active ? "AccentTextBrush" : "SidebarTextBrush");
        b.Click += (_, _) => { _homeView = view; _body.Content = BuildHome(); };
        return b;
    }

    // The collapsible filter + sort bar: a name filter and four sort buttons.
    StackPanel BuildFilterBar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(0, 8, 0, 0) };
        var nameBox = new TextBox { PlaceholderText = "Filter by name…", Width = 200, Text = _homeFilter };
        nameBox.TextChanged += (_, _) => { _homeFilter = nameBox.Text ?? ""; RefreshHomeList(); };
        bar.Children.Add(nameBox);
        bar.Children.Add(SortBtn("Date ↓", HomeSort.DateDesc));
        bar.Children.Add(SortBtn("Date ↑", HomeSort.DateAsc));
        bar.Children.Add(SortBtn("A–Z", HomeSort.NameAsc));
        bar.Children.Add(SortBtn("Z–A", HomeSort.NameDesc));
        return bar;
    }

    // One sort button, accent-highlighted when active.
    Button SortBtn(string label, HomeSort sort)
    {
        var b = Ui.Btn(label); b.Padding = new(8, 4);
        var active = _homeSort == sort;
        Ui.Theme(b, TemplatedControl.BackgroundProperty, active ? "AccentBgBrush"  : "ControlBgBrush");
        Ui.Theme(b, TemplatedControl.ForegroundProperty, active ? "AccentTextBrush" : "SidebarTextBrush");
        b.Click += (_, _) => { _homeSort = sort; _body.Content = BuildHome(); };
        return b;
    }

    // The project paths for the active source, each with a representative date (for sorting/display).
    List<(string path, DateTime date)> HomeItems()
    {
        if (_homeSource is null)
            return RecentProjects.Load().Select(e => (e.Path, e.Opened)).ToList();
        return ProjectService.Scan(_homeSource).Select(p => (p, ProjectDate(p))).ToList();
    }

    // A project's date: its recent-opened time if known, else its marker's created time, else folder mtime.
    static DateTime ProjectDate(string p)
    {
        var rec = RecentProjects.Load().FirstOrDefault(e => string.Equals(e.Path, p, StringComparison.OrdinalIgnoreCase));
        if (rec is not null) return rec.Opened;
        if (ProjectService.Load(p) is { } info) return info.Created.ToLocalTime();
        try { return Directory.GetLastWriteTime(p); } catch { return DateTime.MinValue; }
    }

    // Recomputes the project list from the current source + filter + sort, refreshing it in place.
    void RefreshHomeList()
    {
        var items = HomeItems();
        if (!string.IsNullOrWhiteSpace(_homeFilter))
            items = items.Where(it => ProjectName(it.path).Contains(_homeFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        items = _homeSort switch
        {
            HomeSort.DateAsc  => items.OrderBy(it => it.date).ToList(),
            HomeSort.NameAsc  => items.OrderBy(it => ProjectName(it.path), StringComparer.OrdinalIgnoreCase).ToList(),
            HomeSort.NameDesc => items.OrderByDescending(it => ProjectName(it.path), StringComparer.OrdinalIgnoreCase).ToList(),
            _                 => items.OrderByDescending(it => it.date).ToList(),
        };
        _homeList.Content = BuildProjectList(items);
    }

    // Renders the project list per the active view: cards, multi-column rows, or a dated single list.
    Control BuildProjectList(List<(string path, DateTime date)> items)
    {
        if (items.Count == 0) return Note("No projects.");
        switch (_homeView)
        {
            case HomeView.Cards:
                var cards = new WrapPanel();
                foreach (var (p, d) in items) cards.Children.Add(ProjectCard(p, d, big: false));
                return cards;
            case HomeView.Grid:
                var grid = new WrapPanel();
                foreach (var (p, d) in items) grid.Children.Add(ListRow(p, d, width: 260));
                return grid;
            default:
                var sp = new StackPanel { Spacing = 2 };
                foreach (var (p, d) in items) sp.Children.Add(ListRow(p, d, width: 0, showDate: true));
                return sp;
        }
    }

    // Prompts for a name + a parent folder, creates the project there, and opens it.
    async Task NewProject()
    {
        var name = await PromptDialog.Show(this, "Project name:", "My Project", "New project");
        if (string.IsNullOrWhiteSpace(name)) return;

        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where to create the project", AllowMultiple = false,
        });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } parent) return;

        var folder = Path.Combine(parent, SafeFolder(name));
        ProjectService.Create(folder, name);
        OpenProject(folder);
    }

    // Registers a folder as a library to scan for projects.
    async Task AddLibrary()
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a library folder to scan for projects", AllowMultiple = false,
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } local) { Libraries.Add(local); ShowHome(); }
    }

    // Strips characters that can't appear in a folder name.
    static string SafeFolder(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }

    // A project card: theme swatch + name, optional description, last-opened date; counts on hover.
    // 'big' = the prominent hero card; otherwise a fixed-width grid card.
    Control ProjectCard(string path, DateTime date, bool big)
    {
        var card = new Border { Padding = new(big ? 16 : 12), CornerRadius = new(8), Cursor = new Cursor(StandardCursorType.Hand) };
        Ui.Theme(card, Border.BackgroundProperty, "ControlBgBrush");
        if (big) { Ui.Theme(card, Border.BorderBrushProperty, "AccentBgBrush"); card.BorderThickness = new(1); }
        else { card.Width = 220; card.Margin = new(4); }

        var info = ProjectService.Load(path);
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (ThemeSwatch(info?.PreferredTheme) is { } sw) nameRow.Children.Add(sw);
        var name = new TextBlock { Text = ProjectName(path), FontSize = big ? 20 : 14, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Ui.Theme(name, TextBlock.ForegroundProperty, "ContentTextBrush");
        nameRow.Children.Add(name);

        var stack = new StackPanel { Spacing = 3, Children = { nameRow } };
        if (!string.IsNullOrWhiteSpace(info?.Description)) stack.Children.Add(Dim(info!.Description, big ? 13 : 11));
        stack.Children.Add(Dim("opened " + Friendly(date), 10));
        if (big) stack.Children.Add(new TextBlock { Text = path, FontSize = 11, FontFamily = Mono, Opacity = 0.55, TextWrapping = TextWrapping.Wrap });

        card.Child = stack;
        ToolTip.SetTip(card, StatsTip(path));
        card.PointerPressed += (_, _) => OpenProject(path);
        return card;
    }

    // A compact clickable row; fixed-width for the multi-column grid, or full-width with a right-aligned date.
    Control ListRow(string path, DateTime date, double width, bool showDate = false)
    {
        var row = new Border { Padding = new(10, 7), CornerRadius = new(6), Cursor = new Cursor(StandardCursorType.Hand) };
        if (width > 0) { row.Width = width; row.Margin = new(4); }

        var name = new TextBlock { Text = ProjectName(path), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Ui.Theme(name, TextBlock.ForegroundProperty, "ContentTextBrush");

        var dock = new DockPanel();
        if (showDate)
        {
            var d = Dim(Friendly(date), 11); d.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(d, Dock.Right);
            dock.Children.Add(d);
        }
        DockPanel.SetDock(name, Dock.Left);
        dock.Children.Add(name);

        row.Child = dock;
        ToolTip.SetTip(row, StatsTip(path));
        row.PointerPressed += (_, _) => OpenProject(path);
        return row;
    }

    // A hover tooltip with the project's at-a-glance content counts + path.
    static string StatsTip(string path)
    {
        var (c, f, b) = ProjectService.QuickStats(path);
        return $"{c} classes · {f} functions · {b} boards\n{path}";
    }

    // A small colour chip of a project's preferred theme accent, or null if none/unresolvable.
    static Control? ThemeSwatch(string? themeName)
    {
        if (string.IsNullOrEmpty(themeName)) return null;
        var hit = ThemeManager.Available().FirstOrDefault(t => t.Name == themeName);
        if (hit.Path is null) return null;
        try
        {
            var dict = global::OXSUIT.Loaders.Avalonia.OxsuitLoader.Load(hit.Path);
            if (dict.TryGetResource("AccentBgBrush", null, out var v) && v is IBrush brush)
                return new Border { Width = 12, Height = 12, CornerRadius = new(3), Background = brush, BorderBrush = Brushes.Gray, BorderThickness = new(1), VerticalAlignment = VerticalAlignment.Center };
        }
        catch { /* best-effort */ }
        return null;
    }

    // A short, friendly date label.
    static string Friendly(DateTime d) => d == DateTime.MinValue ? "—" : d.ToString("yyyy-MM-dd HH:mm");

    // The last path segment of a folder, for compact display.
    static string ShortName(string path) => Path.GetFileName(path.TrimEnd('/', '\\'));

    // A muted, themed text line.
    static TextBlock Dim(string text, double size)
    {
        var t = new TextBlock { Text = text, FontSize = size, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        Ui.Theme(t, TextBlock.ForegroundProperty, "ContentTextBrush");
        return t;
    }

    // A muted section label (sources header).
    static TextBlock SectionLabel(string text) =>
        new() { Text = text, FontSize = 12, Opacity = 0.7, Margin = new(2, 10, 0, 2), FontWeight = FontWeight.Bold };

    // ── Project cockpit ─────────────────────────────────────────────────────

    // Opens a project: ensure it has a marker, apply its saved preferences, record it, show the cockpit.
    void OpenProject(string path)
    {
        var info = ProjectService.Load(path) ?? ProjectService.Create(path, "");
        _project = path;
        RecentProjects.Add(path);

        // Per-project preferences (if the marker stored any).
        if (info.PreferredTheme is { } pt)
        {
            var hit = ThemeManager.Available().FirstOrDefault(t => t.Name == pt);
            if (hit.Path is not null) ThemeManager.Apply(Application.Current!, hit.Path);
        }
        if (info.PreferredPalette is { } pp) PaletteStore.ActiveName = pp;

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

    // The project's display name (from its marker, else the folder name).
    static string ProjectName(string path) => string.IsNullOrEmpty(path) ? "—" : ProjectService.DisplayName(path);

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
}
