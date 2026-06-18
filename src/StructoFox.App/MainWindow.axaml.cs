using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using StructoFox.Core;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// The StructoFox shell. Opens on a project browser (recent projects + add folder); opening a
/// project switches to the "Dev Cockpit" — a left icon rail of sections over a themed, honeycombed
/// content area. Theme + palette live in a discreet top-bar menu, not in the foreground.
/// </summary>
public partial class MainWindow : Window
{
    enum Section { Boards, Namespace, Class, Struct, Interface, Enum, Function, Object, Export }
    enum HomeView { Cards, DetailList, MultiList }
    enum HomeSort { DateDesc, DateAsc, NameAsc, NameDesc }

    static readonly FontFamily Mono = new("Consolas, Menlo, Courier New, monospace");

    string? _project;
    Section _section = Section.Class;
    readonly ContentControl _body = new();       // home browser  OR  project cockpit (added once)
    ContentControl _content = new();             // active cockpit section (re-created per cockpit build)
    readonly Dictionary<Section, Button> _railButtons = new();

    // Home browser state.
    string?  _homeSource;                        // null = Recent, else a library path
    HomeView _homeView   = HomeView.Cards;
    HomeSort _homeSort   = HomeSort.DateDesc;
    string   _homeFilter = "";
    bool     _homeFilterOpen;
    ContentControl _homeList = new();            // project-list region (re-created per home build)
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
    void ShowHome() => CrashHandler.Safe(() =>
    {
        _project = null;
        _body.Content = BuildHome();
    }, "ShowHome");

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
        cols.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));   // left nav
        cols.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));   // divider
        cols.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));   // main
        var nav = BuildHomeNav(); Grid.SetColumn(nav, 0); cols.Children.Add(nav);

        var divider = new Border { Width = 1, Margin = new(0, 16, 0, 16), Background = Brushes.Gray, Opacity = 0.3 };
        Grid.SetColumn(divider, 1); cols.Children.Add(divider);

        var main = BuildHomeMain(); Grid.SetColumn(main, 2); cols.Children.Add(main);
        layered.Children.Add(cols);

        host.Child = layered;
        RefreshHomeList();
        return host;
    }

    // Left column: New/Add-library actions pinned at the top, the sources list anchored at the bottom.
    // No coloured sidebar — buttons sit on the same surface as everything else.
    Control BuildHomeNav()
    {
        var grid = new Grid { Width = 162, Margin = new(16, 16, 8, 20) };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));  // actions (top)
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));  // libraries (middle)
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));  // Recent (bottom)

        var top = new StackPanel { Spacing = 6 };
        var newBtn = Ui.Btn(Loc.S("Home_NewProject"), Loc.S("Home_NewProjectTip"));
        newBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        newBtn.Click += async (_, _) => await NewProject();
        var libBtn = Ui.Btn(Loc.S("Home_AddLibrary"), Loc.S("Home_AddLibraryTip"));
        libBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        libBtn.Click += async (_, _) => await AddLibrary();
        top.Children.Add(newBtn);
        top.Children.Add(libBtn);
        Grid.SetRow(top, 0); grid.Children.Add(top);

        // Libraries float in the middle, centred, scrolling if many — a button-height gap above & below.
        var libs = Libraries.Load();
        if (libs.Count > 0)
        {
            var libPanel = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            foreach (var lib in libs) libPanel.Children.Add(NavEntry("📁  " + ShortName(lib), lib, true));
            var libScroll = new ScrollViewer { Content = libPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 36, 0, 36) };
            Grid.SetRow(libScroll, 1); grid.Children.Add(libScroll);
        }

        var recent = NavEntry(Loc.S("Home_Recent"), null, false);
        Grid.SetRow(recent, 2); grid.Children.Add(recent);

        return grid;
    }

    // One sources entry; left-click selects it as the list source. Libraries are removed via a
    // right-click context menu (no easy-to-misclick ✕).
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

        if (removable && source is not null)
        {
            ToolTip.SetTip(b, source);   // full library path on hover
            var remove = new MenuItem { Header = Loc.S("Home_RemoveLibrary") };
            remove.Click += (_, _) => { Libraries.Remove(source); if (_homeSource == source) _homeSource = null; _body.Content = BuildHome(); };
            var cm = new ContextMenu();
            cm.Items.Add(remove);
            b.ContextMenu = cm;
        }
        return b;
    }

    // Right column: top third = the most-recent project (hero); bottom two-thirds = the project list.
    Control BuildHomeMain()
    {
        var grid = new Grid { Margin = new(20) };
        grid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));  // top 1/3
        grid.RowDefinitions.Add(new RowDefinition(new GridLength(2, GridUnitType.Star)));  // bottom 2/3
        var hero = BuildHero(); Grid.SetRow(hero, 0); grid.Children.Add(hero);
        var bottom = BuildHomeBottom(); Grid.SetRow(bottom, 1); grid.Children.Add(bottom);
        return grid;
    }

    // Top third: the most-recent project as a centred hero card, or a placeholder tile if there is none.
    Control BuildHero()
    {
        var last = RecentProjects.Load().FirstOrDefault();
        Control card = last is null ? PlaceholderCard() : ProjectCard(last.Path, last.Opened, big: true);
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment   = VerticalAlignment.Center;
        card.MaxWidth = 520;
        return card;
    }

    // A card-styled empty state for the hero slot when no project has been opened yet.
    Control PlaceholderCard()
    {
        var card = new Border { Padding = new(20), CornerRadius = new(8), MinWidth = 300, MinHeight = 96 };
        Ui.Theme(card, Border.BackgroundProperty, "ControlBgBrush");
        Ui.Theme(card, Border.BorderBrushProperty, "AccentBgBrush");
        card.BorderThickness = new(1);

        var t1 = new TextBlock { Text = "Recent", FontSize = 13, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center };
        Ui.Theme(t1, TextBlock.ForegroundProperty, "ContentTextBrush");
        var t2 = new TextBlock { Text = "No projects", FontSize = 18, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center };
        Ui.Theme(t2, TextBlock.ForegroundProperty, "ContentTextBrush");
        card.Child = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { t1, t2 } };
        return card;
    }

    // Bottom two-thirds: the project list above, and a single control row pinned along the very bottom.
    Control BuildHomeBottom()
    {
        _homeList = new ContentControl();   // fresh instance each build (a control has one parent)

        var grid = new Grid { Margin = new(0, 8, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));  // list
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));  // bottom control row

        var listScroll = new ScrollViewer { Content = _homeList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(listScroll, 0); grid.Children.Add(listScroll);

        var bottom = BuildBottomBar(); Grid.SetRow(bottom, 1); grid.Children.Add(bottom);

        RefreshHomeList();
        return grid;
    }

    // The bottom control row: [Recent] | [view ▾] [🔎], with the filter+sort inputs unfolding to the right.
    Control BuildBottomBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(0, 8, 0, 0) };

        row.Children.Add(ViewDropdown());

        var filter = Ui.Btn("🔎", "Filter & sort"); filter.Padding = new(10, 4);
        filter.Click += (_, _) => { _homeFilterOpen = !_homeFilterOpen; if (_homeFilterBar is not null) _homeFilterBar.IsVisible = _homeFilterOpen; };
        row.Children.Add(filter);

        _homeFilterBar = BuildFilterBar(); _homeFilterBar.IsVisible = _homeFilterOpen;
        row.Children.Add(_homeFilterBar);

        return new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    // A single button showing the current view; opens a menu of the three view modes.
    Control ViewDropdown()
    {
        var b = Ui.Btn(ViewIcon(_homeView) + "  ▾", "View");
        b.Click += (_, _) =>
        {
            var cm = new ContextMenu();
            void Item(string label, HomeView view) { var mi = new MenuItem { Header = label }; mi.Click += (_, _) => { _homeView = view; _body.Content = BuildHome(); }; cm.Items.Add(mi); }
            Item("▦  Kacheln", HomeView.Cards);
            Item("≣  Einspaltige Liste (Details)", HomeView.DetailList);
            Item("☷  Mehrspaltige Liste", HomeView.MultiList);
            cm.Open(b);
        };
        return b;
    }

    // The glyph for a view (shown on the dropdown button).
    static string ViewIcon(HomeView v) => v switch { HomeView.DetailList => "≣", HomeView.MultiList => "☷", _ => "▦" };

    // The collapsible filter + sort bar: a name filter and four sort buttons.
    StackPanel BuildFilterBar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
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
    void RefreshHomeList() => CrashHandler.Safe(() =>
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
    }, "RefreshHomeList");

    // Renders the project list per the active view: cards, single-column detail rows, or multi-column rows.
    Control BuildProjectList(List<(string path, DateTime date)> items)
    {
        if (items.Count == 0) return Note("No projects.");
        switch (_homeView)
        {
            case HomeView.MultiList:
                var grid = new WrapPanel();
                foreach (var (p, d) in items) grid.Children.Add(ListRow(p, d, width: 260));
                return grid;
            case HomeView.DetailList:
                var sp = new StackPanel { Spacing = 2 };
                foreach (var (p, d) in items) sp.Children.Add(DetailRow(p, d));
                return sp;
            default:
                var cards = new WrapPanel();
                foreach (var (p, d) in items) cards.Children.Add(ProjectCard(p, d, big: false));
                return cards;
        }
    }

    // A single-column detail row: name, path (muted, mono), and the date right-aligned.
    Control DetailRow(string path, DateTime date)
    {
        var row = new Border { Padding = new(10, 7), CornerRadius = new(6), Cursor = new Cursor(StandardCursorType.Hand) };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var name = new TextBlock { Text = ProjectName(path), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        Ui.Theme(name, TextBlock.ForegroundProperty, "ContentTextBrush");
        var loc = new TextBlock { Text = path, FontSize = 10, FontFamily = Mono, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center, Margin = new(10, 0, 10, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        Ui.Theme(loc, TextBlock.ForegroundProperty, "ContentTextBrush");
        var when = Dim(Friendly(date), 11); when.VerticalAlignment = VerticalAlignment.Center;

        Grid.SetColumn(name, 0); Grid.SetColumn(loc, 1); Grid.SetColumn(when, 2);
        grid.Children.Add(name); grid.Children.Add(loc); grid.Children.Add(when);

        row.Child = grid;
        ToolTip.SetTip(row, StatsTip(path));
        row.PointerPressed += (_, _) => OpenProject(path);
        return row;
    }

    // Asks (via NewProjectDialog) where + what to name the project, registers the library, creates & opens it.
    Task NewProject() => CrashHandler.SafeAsync(async () =>
    {
        var res = await NewProjectDialog.Show(this, Libraries.Load());
        if (res is not { } r) return;
        var (parent, name) = r;
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(name)) return;

        Libraries.Add(parent);   // register the chosen folder as a library (no-op if already one)
        var folder = Path.Combine(parent, SafeFolder(name));
        ProjectService.Create(folder, name);
        OpenProject(folder);
    }, "NewProject");

    // Registers a folder as a library to scan for projects.
    Task AddLibrary() => CrashHandler.SafeAsync(async () =>
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a library folder to scan for projects", AllowMultiple = false,
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } local) { Libraries.Add(local); ShowHome(); }
    }, "AddLibrary");

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
    void OpenProject(string path) => CrashHandler.Safe(() =>
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
    }, "OpenProject");

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

    // The left icon rail: section buttons at the top, an "exit to projects" door pinned at the bottom.
    Control BuildRail()
    {
        var rail = new Border { Width = 84, Padding = new(6, 10) };
        Ui.Theme(rail, Border.BackgroundProperty, "SidebarBgBrush");

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));  // sections
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));  // exit door

        var stack = new StackPanel { Spacing = 4 };
        (Section sec, string icon, string label)[] items =
        {
            (Section.Boards,    "🗂", "Boards"),
            (Section.Namespace, "Ⓝ", "Namespaces"),
            (Section.Class,     "Ⓒ", "Classes"),
            (Section.Struct,    "Ⓢ", "Structs"),
            (Section.Interface, "Ⓘ", "Interfaces"),
            (Section.Enum,      "Ⓔ", "Enums"),
            (Section.Function,  "ƒ",  "Functions"),
            (Section.Object,    "Ⓞ", "Objects"),
            (Section.Export,    "⇩",  "Export"),
        };
        foreach (var (sec, icon, label) in items) stack.Children.Add(RailButton(sec, icon, label));
        var sectionsScroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(sectionsScroll, 0); grid.Children.Add(sectionsScroll);

        var exit = ExitButton();
        Grid.SetRow(exit, 1); grid.Children.Add(exit);

        rail.Child = grid;
        return rail;
    }

    // The back arrow at the bottom of the rail — returns to the project browser (not a program exit).
    Button ExitButton()
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
                    new TextBlock { Text = "←", FontSize = 24, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = Loc.S("Cockpit_Exit"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };
        ToolTip.SetTip(b, Loc.S("Cockpit_ExitTip"));
        Ui.Theme(b, TemplatedControl.BackgroundProperty, "ControlBgBrush");
        Ui.Theme(b, TemplatedControl.ForegroundProperty, "SidebarTextBrush");
        b.Click += (_, _) => ShowHome();
        return b;
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
        _content = new ContentControl();   // fresh instance each build (a control has one parent)
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
    void ShowSection(Section section) => CrashHandler.Safe(() =>
    {
        _section = section;
        foreach (var (s, b) in _railButtons)
        {
            var active = s == section;
            Ui.Theme(b, TemplatedControl.BackgroundProperty, active ? "AccentBgBrush"  : "ControlBgBrush");
            Ui.Theme(b, TemplatedControl.ForegroundProperty, active ? "AccentTextBrush" : "SidebarTextBrush");
        }
        _content.Content = BuildSectionView(section);
    }, "ShowSection");

    // Builds a section view: Boards/Export are placeholders; entity sections list their entities
    // (loaded from the project) with a "New …" action.
    Control BuildSectionView(Section section)
    {
        var root = new StackPanel { Spacing = 12, MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left };
        root.Children.Add(new TextBlock { Text = ProjectName(_project ?? ""), FontFamily = Mono, FontSize = 11, Opacity = 0.6 });

        if (section == Section.Boards) { root.Children.Add(Heading("Boards")); root.Children.Add(Note(Loc.S("Sec_BoardsBlurb"))); return root; }
        if (section == Section.Export) { root.Children.Add(Heading("Export")); root.Children.Add(Note(Loc.S("Sec_ExportBlurb"))); return root; }

        // Entity section: heading, a "New …" action, then the list of entities of this type.
        root.Children.Add(Heading(PluralLabel(section)));
        var add = Ui.Btn(string.Format(Loc.S("Sec_New"), SingularLabel(section)));
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Click += async (_, _) => await NewEntity(section);
        root.Children.Add(add);

        var entities = _project is null ? new() : CodeEntityService.LoadAll(_project, section.ToString());
        if (entities.Count == 0)
            root.Children.Add(Note(string.Format(Loc.S("Sec_Empty"), PluralLabel(section))));
        else
        {
            var list = new StackPanel { Spacing = 4 };
            foreach (var e in entities.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                list.Children.Add(EntityRow(e, section));
            root.Children.Add(list);
        }
        return root;
    }

    // One entity row: name + a small summary; functions open the diagram chooser; right-click to delete.
    Control EntityRow(CodeEntity e, Section section)
    {
        var row = new Border { Padding = new(10, 7), CornerRadius = new(6), Cursor = new Cursor(StandardCursorType.Hand) };
        Ui.Theme(row, Border.BackgroundProperty, "ControlBgBrush");

        var name = new TextBlock { Text = e.Name, FontSize = 13, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Ui.Theme(name, TextBlock.ForegroundProperty, "ContentTextBrush");
        var sum = Dim(EntitySummary(e), 11); sum.VerticalAlignment = VerticalAlignment.Center;

        var dock = new DockPanel();
        DockPanel.SetDock(sum, Dock.Right); dock.Children.Add(sum);
        DockPanel.SetDock(name, Dock.Left); dock.Children.Add(name);
        row.Child = dock;

        if (section == Section.Function && _project is not null)
            row.PointerPressed += (_, ev) => { if (ev.GetCurrentPoint(row).Properties.IsLeftButtonPressed) _ = DiagramLauncher.ChooseAndOpen(this, _project!, e.Id, e.Name, null); };

        var del = new MenuItem { Header = Loc.S("Sec_Delete") };
        del.Click += (_, _) => { if (_project is not null) CodeEntityService.Delete(_project, section.ToString(), e.Id); ShowSection(section); };
        var cm = new ContextMenu(); cm.Items.Add(del);
        row.ContextMenu = cm;
        return row;
    }

    // A short, type-appropriate summary of an entity's contents.
    static string EntitySummary(CodeEntity e) => e.EntityType switch
    {
        CodeEntityType.Enum     => $"{e.EnumValues.Count} values",
        CodeEntityType.Function => $"{e.Ports.Count} ports",
        CodeEntityType.Object   => string.IsNullOrEmpty(e.InstanceOfId) ? "object" : "instance",
        _                       => $"{e.Fields.Count} fields · {e.Methods.Count} methods",
    };

    // Prompts for a name and creates a bare entity of the section's type (full editing comes later).
    Task NewEntity(Section section) => CrashHandler.SafeAsync(async () =>
    {
        if (_project is null) return;
        var name = await PromptDialog.Show(this, string.Format(Loc.S("Sec_NewPrompt"), SingularLabel(section)), SingularLabel(section), SingularLabel(section));
        if (string.IsNullOrWhiteSpace(name)) return;
        var entity = new CodeEntity { Name = name.Trim(), EntityType = Enum.Parse<CodeEntityType>(section.ToString()) };
        CodeEntityService.Save(_project, section.ToString(), entity);
        ShowSection(section);
    }, "NewEntity");

    // Plural heading label for a section.
    static string PluralLabel(Section s) => s switch
    {
        Section.Namespace => "Namespaces", Section.Class => "Classes", Section.Struct => "Structs",
        Section.Interface => "Interfaces", Section.Enum => "Enums", Section.Function => "Functions",
        Section.Object => "Objects", Section.Boards => "Boards", _ => "Export",
    };

    // Singular label (the entity type name) for "New …" actions.
    static string SingularLabel(Section s) => s.ToString();

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
