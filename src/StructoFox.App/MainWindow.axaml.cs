using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using StructoFox.Core;
using StructoFox.Core.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StructoFox.App;

/// <summary>
/// The StructoFox shell. Opens on a project browser (recent projects + add folder); opening a
/// project switches to the "Dev Cockpit" — a left icon rail of sections over a themed, honeycombed
/// content area. Theme + palette live in a discreet top-bar menu, not in the foreground.
/// </summary>
public partial class MainWindow : Window
{
    enum Section { Boards, Main, Namespace, Class, Struct, Interface, Enum, Function, Object, Export }
    enum HomeView { Cards, BigCards, DetailList, MultiList }
    enum HomeSort { DateDesc, DateAsc, NameAsc, NameDesc }

    static readonly FontFamily Mono = new("Consolas, Menlo, Courier New, monospace");

    /// <summary>A colour-emoji font so the fox keeps its colours even after a re-layout (otherwise
    /// Avalonia can fall back to a monochrome glyph that inherits the themed text colour).</summary>
    static readonly FontFamily Emoji = new("Segoe UI Emoji, Apple Color Emoji, Noto Color Emoji");

    /// <summary>Display version of the app, shown in the About box.</summary>
    public const string Version = "0.5 ALPHA";

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

    // Cockpit entity selection (for the currently shown entity section).
    readonly HashSet<string> _selEntities = new();
    string? _selAnchor;
    readonly List<(CodeEntity e, Border row)> _sectionRows = new();

    // Cockpit entity-section filter state.
    string  _secFilter = "";
    string? _secNamespace = null;          // null = all namespaces, "" = no namespace, else the name
    HomeView _secView = HomeView.DetailList;
    ContentControl _secList = new();       // the filtered list region (re-rendered without rebuilding the bar)

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
        brand.Children.Add(new TextBlock { Text = "🦊", FontFamily = Emoji, FontSize = 22, VerticalAlignment = VerticalAlignment.Center });
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

        cm.Items.Add(new Separator());

        var about = new MenuItem { Header = string.Format(Loc.S("Menu_About"), Version) };
        about.Click += (_, _) => ShowAbout();
        cm.Items.Add(about);

        cm.Open(anchor);
    }

    // A small themed About box: fox, name, version and tagline (à la Theminator / ClaudetRelay).
    void ShowAbout() => CrashHandler.Safe(() =>
    {
        var dlg = new Window
        {
            Title = Loc.S("Menu_AboutTitle"),
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        Ui.ThemeWindow(dlg);

        TextBlock Line(string text, double size, FontWeight weight, double opacity = 1)
        {
            var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight, Opacity = opacity, HorizontalAlignment = HorizontalAlignment.Center };
            Ui.Theme(t, TextBlock.ForegroundProperty, "ContentTextBrush");
            return t;
        }

        var ok = Ui.Btn(Loc.S("Common_OK")); ok.IsDefault = true; ok.IsCancel = true;
        ok.HorizontalAlignment = HorizontalAlignment.Center;
        ok.Click += (_, _) => dlg.Close();

        dlg.Content = new StackPanel
        {
            Margin = new(28, 24), Spacing = 6, MinWidth = 280,
            Children =
            {
                new TextBlock { Text = "🦊", FontFamily = Emoji, FontSize = 52, HorizontalAlignment = HorizontalAlignment.Center },
                Line("StructoFox", 22, FontWeight.Bold),
                Line(Loc.S("App_Tagline"), 12, FontWeight.Normal, 0.7),
                Line("Version " + Version, 13, FontWeight.SemiBold),
                new Border { Height = 10 },
                ok,
            },
        };
        dlg.ShowDialog(this);
    }, "ShowAbout");

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

    // Left column: "New project" pinned at the top, the library folders (separate from projects) in the
    // middle, and "Add library" anchored at the bottom. No "Recent" entry — date sorting covers that;
    // the default list is the recent projects, and clicking an active library again returns to it.
    Control BuildHomeNav()
    {
        var grid = new Grid { Width = 162, Margin = new(16, 16, 8, 20) };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));  // New project (top)
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));  // library folders (middle)
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));  // Add library (bottom)

        var newBtn = Ui.Btn(Loc.S("Home_NewProject"), Loc.S("Home_NewProjectTip"));
        newBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        newBtn.Click += async (_, _) => await NewProject();
        Grid.SetRow(newBtn, 0); grid.Children.Add(newBtn);

        // The library folders float in the middle, centred, scrolling if many.
        var libs = Libraries.Load();
        if (libs.Count > 0)
        {
            var libPanel = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            foreach (var lib in libs) libPanel.Children.Add(NavEntry("📁  " + ShortName(lib), lib));
            var libScroll = new ScrollViewer { Content = libPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 12, 0, 12) };
            Grid.SetRow(libScroll, 1); grid.Children.Add(libScroll);
        }

        var libBtn = Ui.Btn(Loc.S("Home_AddLibrary"), Loc.S("Home_AddLibraryTip"));
        libBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        libBtn.Click += async (_, _) => await AddLibrary();
        Grid.SetRow(libBtn, 2); grid.Children.Add(libBtn);

        return grid;
    }

    // One library entry; left-click selects it as the list source (click the active one again to go back
    // to recent). Libraries are removed via a right-click context menu (no easy-to-misclick ✕).
    Control NavEntry(string label, string source)
    {
        var active = _homeSource == source;
        var b = new Button
        {
            Content = label, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new(8, 6), CornerRadius = new(6),
        };
        Ui.Theme(b, TemplatedControl.BackgroundProperty, active ? "AccentBgBrush"  : "ControlBgBrush");
        Ui.Theme(b, TemplatedControl.ForegroundProperty, active ? "AccentTextBrush" : "SidebarTextBrush");
        b.Click += (_, _) => { _homeSource = active ? null : source; _body.Content = BuildHome(); };

        ToolTip.SetTip(b, source);   // full library path on hover
        var remove = new MenuItem { Header = Loc.S("Home_RemoveLibrary") };
        remove.Click += (_, _) => { Libraries.Remove(source); if (_homeSource == source) _homeSource = null; _body.Content = BuildHome(); };
        var cm = new ContextMenu();
        cm.Items.Add(remove);
        b.ContextMenu = cm;
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
            Item("▣  Große Kacheln", HomeView.BigCards);
            Item("≣  Einspaltige Liste (Details)", HomeView.DetailList);
            Item("☷  Mehrspaltige Liste", HomeView.MultiList);
            cm.Open(b);
        };
        return b;
    }

    // The glyph for a view (shown on the dropdown button).
    static string ViewIcon(HomeView v) => v switch { HomeView.BigCards => "▣", HomeView.DetailList => "≣", HomeView.MultiList => "☷", _ => "▦" };

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
            case HomeView.BigCards:
                var bigs = new WrapPanel();
                foreach (var (p, d) in items) bigs.Children.Add(BigCard(p, d));
                return bigs;
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

        var name = new TextBlock { Text = ProjectName(path), FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Ui.Theme(name, TextBlock.ForegroundProperty, "ContentTextBrush");
        var loc = new TextBlock { Text = path, FontSize = 12, FontFamily = Mono, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center, Margin = new(10, 0, 10, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        Ui.Theme(loc, TextBlock.ForegroundProperty, "ContentTextBrush");
        var when = Dim(Friendly(date), 13); when.VerticalAlignment = VerticalAlignment.Center;

        Grid.SetColumn(name, 0); Grid.SetColumn(loc, 1); Grid.SetColumn(when, 2);
        grid.Children.Add(name); grid.Children.Add(loc); grid.Children.Add(when);

        row.Child = grid;
        ToolTip.SetTip(row, StatsTip(path));
        row.PointerPressed += (_, e) => { if (e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) OpenProject(path); };
        AttachProjectMenu(row, path);
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

        var stack = new StackPanel { Spacing = 3, Children = { HeaderRow(nameRow, date) } };
        if (!string.IsNullOrWhiteSpace(info?.Description)) stack.Children.Add(Dim(info!.Description, big ? 13 : 11));
        if (big) stack.Children.Add(new TextBlock { Text = path, FontSize = 11, FontFamily = Mono, Opacity = 0.55, TextWrapping = TextWrapping.Wrap });

        card.Child = stack;
        ToolTip.SetTip(card, StatsTip(path));
        card.PointerPressed += (_, e) => { if (e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) OpenProject(path); };
        AttachProjectMenu(card, path);
        return card;
    }

    // Attaches the per-project right-click menu (rename + open folder) to a tile or row.
    void AttachProjectMenu(Control c, string path)
    {
        var rename = new MenuItem { Header = Loc.S("Proj_Rename") };
        rename.Click += async (_, _) => await RenameProject(path);
        var openFolder = new MenuItem { Header = Loc.S("Proj_OpenFolder") };
        openFolder.Click += (_, _) => OpenInExplorer(path);
        var cm = new ContextMenu();
        cm.Items.Add(rename);
        cm.Items.Add(openFolder);
        c.ContextMenu = cm;
    }

    // Renames a project (its display name in the marker — the folder is left untouched), then refreshes.
    Task RenameProject(string path) => CrashHandler.SafeAsync(async () =>
    {
        var name = await PromptDialog.Show(this, Loc.S("Proj_RenamePrompt"), ProjectName(path), Loc.S("Proj_Rename"));
        if (string.IsNullOrWhiteSpace(name)) return;
        var info = ProjectService.Load(path) ?? ProjectService.Create(path, name);
        info.Name = name.Trim();
        ProjectService.Save(path, info);
        _body.Content = BuildHome();
    }, "RenameProject");

    // Opens the project's folder in the OS file explorer (Explorer / Finder / xdg-open).
    void OpenInExplorer(string path) => CrashHandler.Safe(() =>
    {
        if (!Directory.Exists(path)) return;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Process.Start(new ProcessStartInfo("open", $"\"{path}\"") { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo("xdg-open", $"\"{path}\"") { UseShellExecute = true });
    }, "OpenInExplorer");

    // A card's top row: the name block on the left, the date tucked into the top-right corner.
    Control HeaderRow(Control nameRow, DateTime date)
    {
        var when = Dim(Friendly(date), 10);
        when.VerticalAlignment = VerticalAlignment.Top;
        when.Margin = new(8, 0, 0, 0);
        var dock = new DockPanel();
        DockPanel.SetDock(when, Dock.Right);
        dock.Children.Add(when);
        dock.Children.Add(nameRow);
        return dock;
    }

    // A large, detail-rich tile: theme swatch + name, description, content counts, date and path.
    Control BigCard(string path, DateTime date)
    {
        var card = new Border { Width = 250, Padding = new(16), Margin = new(5), CornerRadius = new(8), Cursor = new Cursor(StandardCursorType.Hand) };
        Ui.Theme(card, Border.BackgroundProperty, "ControlBgBrush");

        var info = ProjectService.Load(path);
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (ThemeSwatch(info?.PreferredTheme) is { } sw) nameRow.Children.Add(sw);
        var name = new TextBlock { Text = ProjectName(path), FontSize = 18, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Ui.Theme(name, TextBlock.ForegroundProperty, "ContentTextBrush");
        nameRow.Children.Add(name);

        var stack = new StackPanel { Spacing = 4, Children = { HeaderRow(nameRow, date) } };
        if (!string.IsNullOrWhiteSpace(info?.Description)) stack.Children.Add(Dim(info!.Description, 12));
        stack.Children.Add(new TextBlock { Text = path, FontSize = 11, FontFamily = Mono, Opacity = 0.5, TextWrapping = TextWrapping.Wrap });

        card.Child = stack;
        ToolTip.SetTip(card, StatsTip(path));
        card.PointerPressed += (_, e) => { if (e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) OpenProject(path); };
        AttachProjectMenu(card, path);
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
        row.PointerPressed += (_, e) => { if (e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) OpenProject(path); };
        AttachProjectMenu(row, path);
        return row;
    }

    // A hover tooltip with the project's at-a-glance content counts (one line per kind).
    static string StatsTip(string path)
    {
        var (t, boards) = ProjectService.ContentCounts(path);
        int N(string k) => t.TryGetValue(k, out var n) ? n : 0;
        return string.Join("\n", new[]
        {
            $"Namespaces: {N("Namespace")}",
            $"Classes: {N("Class")}",
            $"Structs: {N("Struct")}",
            $"Interfaces: {N("Interface")}",
            $"Enums: {N("Enum")}",
            $"Objects: {N("Object")}",
            $"Functions: {N("Function")}",
            $"Boards: {boards}",
        });
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
            (Section.Boards,    "🖼", "Boards"),
            (Section.Main,      "▶", "Main"),
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
        _sectionRows.Clear(); _selEntities.Clear(); _selAnchor = null;   // selection is per shown section
        var root = new StackPanel { Spacing = 12, MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left };
        root.Children.Add(new TextBlock { Text = ProjectName(_project ?? ""), FontFamily = Mono, FontSize = 11, Opacity = 0.6 });

        if (section == Section.Boards) { BuildBoardsView(root); return root; }
        if (section == Section.Main)   { BuildMainView(root); return root; }
        if (section == Section.Export) { BuildExportView(root); return root; }

        // Entity section: heading, a "New …" action, a filter bar, then the filtered list.
        root.Children.Add(Heading(PluralLabel(section)));
        var add = Ui.Btn(string.Format(Loc.S("Sec_New"), SingularLabel(section)));
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Click += async (_, _) => await NewEntity(section);
        root.Children.Add(add);

        root.Children.Add(BuildSectionFilterBar(section));

        _secList = new ContentControl();
        root.Children.Add(_secList);
        RefreshSecList(section);
        return root;
    }

    // Filter bar for an entity section: a name filter + an always-visible Namespace dropdown.
    Control BuildSectionFilterBar(Section section)
    {
        var nameBox = new TextBox { Width = 200, Text = _secFilter, PlaceholderText = Loc.S("Sec_FilterName") };
        Ui.Theme(nameBox, TextBox.BackgroundProperty,  "InputBgBrush");
        Ui.Theme(nameBox, TextBox.ForegroundProperty,  "SidebarTextBrush");
        Ui.Theme(nameBox, TextBox.BorderBrushProperty, "ControlBorderBrush");
        nameBox.TextChanged += (_, _) => { _secFilter = nameBox.Text ?? ""; RefreshSecList(section); };

        var nsCombo = Ui.Combo(190);
        nsCombo.Items.Add(Loc.S("Sec_NsAll"));
        nsCombo.Items.Add(Loc.S("Sec_NsNone"));
        var spaces = _project is null ? new List<string>()
            : LoadAllEntities(_project).Values.Select(e => e.Namespace).Where(n => !string.IsNullOrWhiteSpace(n))
              .Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var ns in spaces) nsCombo.Items.Add(ns);
        nsCombo.SelectedItem = _secNamespace is null ? Loc.S("Sec_NsAll") : _secNamespace == "" ? Loc.S("Sec_NsNone") : (spaces.Contains(_secNamespace) ? _secNamespace : Loc.S("Sec_NsAll"));
        nsCombo.SelectionChanged += (_, _) =>
        {
            var sel = nsCombo.SelectedItem as string;
            _secNamespace = sel == Loc.S("Sec_NsAll") ? null : sel == Loc.S("Sec_NsNone") ? "" : sel;
            RefreshSecList(section);
        };

        // View selector (same four modes as the home page).
        var viewBtn = Ui.Btn(ViewIcon(_secView) + "  ▾", "View"); viewBtn.Padding = new(10, 4);
        viewBtn.Click += (_, _) =>
        {
            var cm = new ContextMenu();
            void Item(string label, HomeView v) { var mi = new MenuItem { Header = label }; mi.Click += (_, _) => { _secView = v; RefreshSecList(section); }; cm.Items.Add(mi); }
            Item("▦  Kacheln", HomeView.Cards);
            Item("▣  Große Kacheln", HomeView.BigCards);
            Item("≣  Einspaltige Liste (Details)", HomeView.DetailList);
            Item("☷  Mehrspaltige Liste", HomeView.MultiList);
            cm.Open(viewBtn);
        };

        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children = { nameBox, new TextBlock { Text = Loc.S("CodeEdit_Namespace"), VerticalAlignment = VerticalAlignment.Center }, nsCombo, viewBtn },
        };
    }

    // Re-renders just the list region for the current filter (so the filter bar keeps focus).
    void RefreshSecList(Section section) => CrashHandler.Safe(() =>
    {
        var entities = _project is null ? new() : CodeEntityService.LoadAll(_project, section.ToString());
        if (section == Section.Function) entities = entities.Where(e => !e.IsEntryPoint).ToList();
        entities = entities.Where(e =>
            (string.IsNullOrEmpty(_secFilter) || e.Name.Contains(_secFilter, StringComparison.OrdinalIgnoreCase)) &&
            (_secNamespace is null || (_secNamespace == "" ? string.IsNullOrEmpty(e.Namespace) : e.Namespace == _secNamespace)))
            .ToList();
        _secList.Content = BuildEntityList(section, entities);
    }, "RefreshSecList");

    // Builds the (filtered) entity list with rubber-band selection, or an "empty" note.
    Control BuildEntityList(Section section, List<CodeEntity> entities)
    {
        _sectionRows.Clear(); _selEntities.Clear(); _selAnchor = null;
        if (entities.Count == 0)
            return Note(string.Format(Loc.S("Sec_Empty"), PluralLabel(section)));

        // Single-column for the detail list; a wrap grid for the (big) card and multi-column views.
        Panel list = _secView == HomeView.DetailList ? new StackPanel { Spacing = 4 } : new WrapPanel();
        foreach (var e in entities.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            list.Children.Add(EntityRow(e, section, _secView));

        var overlay = new Canvas { IsHitTestVisible = false };
        var holder  = new Grid { Background = Brushes.Transparent };
        holder.Children.Add(list);
        holder.Children.Add(overlay);
        WireListRubberBand(holder, overlay);
        return holder;
    }

    // Assigns a namespace (prompted) to the selection (or the right-clicked entity), then refreshes.
    Task AssignNamespace(CodeEntity e, Section section) => CrashHandler.SafeAsync(async () =>
    {
        if (_project is null) return;
        var ids = _selEntities.Contains(e.Id) && _selEntities.Count > 0 ? _selEntities.ToList() : new() { e.Id };
        var ns = await PromptDialog.Show(this, Loc.S("Sec_NsPrompt"), e.Namespace, Loc.S("Sec_SetNs"));
        if (ns is null) return;
        foreach (var ent in CodeEntityService.LoadAll(_project, section.ToString()))
            if (ids.Contains(ent.Id)) { ent.Namespace = ns.Trim(); CodeEntityService.Save(_project, section.ToString(), ent); }
        ShowSection(section);   // rebuild so the namespace dropdown picks up any new value
    }, "AssignNamespace");

    // The inner content of an entity tile/row, laid out for the chosen view.
    Control EntityRowContent(CodeEntity e, HomeView view)
    {
        TextBlock Name(double size) { var t = new TextBlock { Text = e.Name, FontSize = size, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }; Ui.Theme(t, TextBlock.ForegroundProperty, "ContentTextBrush"); return t; }
        var nsText = string.IsNullOrWhiteSpace(e.Namespace) ? null : "🏷 " + e.Namespace;

        switch (view)
        {
            case HomeView.Cards:
            {
                var st = new StackPanel { Width = 180, Spacing = 2, Children = { Name(14), Dim(EntitySummary(e), 11) } };
                if (nsText is not null) st.Children.Add(Dim(nsText, 10));
                return st;
            }
            case HomeView.BigCards:
            {
                var st = new StackPanel { Width = 250, Spacing = 3, Children = { Name(16), Dim(EntitySummary(e), 12) } };
                if (nsText is not null) st.Children.Add(Dim(nsText, 11));
                return st;
            }
            case HomeView.MultiList:
            {
                var dock = new DockPanel { Width = 240 };
                var sum = Dim(EntitySummary(e), 11); sum.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(sum, Dock.Right); dock.Children.Add(sum);
                var nm = Name(13); DockPanel.SetDock(nm, Dock.Left); dock.Children.Add(nm);
                return dock;
            }
            default: // DetailList — full-width row: name left, namespace + summary right
            {
                var dock = new DockPanel();
                var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
                if (nsText is not null) right.Children.Add(Dim(nsText, 11));
                right.Children.Add(Dim(EntitySummary(e), 11));
                DockPanel.SetDock(right, Dock.Right); dock.Children.Add(right);
                var nm = Name(13); DockPanel.SetDock(nm, Dock.Left); dock.Children.Add(nm);
                return dock;
            }
        }
    }

    // One entity tile/row. Left-click selects (Ctrl toggles, Shift ranges), double-click or
    // right-click → Edit opens it; delete sits below a separator in the menu to dodge mis-clicks.
    Control EntityRow(CodeEntity e, Section section, HomeView view)
    {
        var row = new Border { Padding = new(10, 7), CornerRadius = new(6), BorderThickness = new(2), BorderBrush = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
        Ui.Theme(row, Border.BackgroundProperty, "ControlBgBrush");
        if (view is HomeView.Cards or HomeView.BigCards or HomeView.MultiList) row.Margin = new(0, 0, 8, 8);
        row.Child = EntityRowContent(e, view);
        _sectionRows.Add((e, row));

        var pressPos = default(Point);
        PointerPressedEventArgs? pressArgs = null;   // kept so a drag can start from PointerMoved

        row.PointerPressed += (_, ev) =>
        {
            var pt = ev.GetCurrentPoint(row);
            if (pt.Properties.IsRightButtonPressed)
            {
                if (!_selEntities.Contains(e.Id)) { _selEntities.Clear(); _selEntities.Add(e.Id); _selAnchor = e.Id; RefreshEntitySelection(); }
                return;   // let the ContextMenu open via ContextRequested
            }
            if (!pt.Properties.IsLeftButtonPressed) return;
            if (ev.ClickCount >= 2) { _ = EditEntity(e, section); ev.Handled = true; return; }

            var mods = ev.KeyModifiers;
            if (mods.HasFlag(KeyModifiers.Shift)) SelectEntityRangeTo(e.Id);
            else if (mods.HasFlag(KeyModifiers.Control)) { if (!_selEntities.Add(e.Id)) _selEntities.Remove(e.Id); _selAnchor = e.Id; }
            // Plain press on an already-selected row keeps the (multi-)selection so it can be dragged;
            // pressing an unselected row selects it exclusively.
            else if (!_selEntities.Contains(e.Id)) { _selEntities.Clear(); _selEntities.Add(e.Id); _selAnchor = e.Id; }
            RefreshEntitySelection();
            pressArgs = ev; pressPos = ev.GetPosition(row);
            ev.Handled = true;
        };
        row.PointerMoved += (_, ev) =>
        {
            if (pressArgs is null || !ev.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
            var d = ev.GetPosition(row) - pressPos;
            if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4) return;   // movement threshold
            var args = pressArgs; pressArgs = null;
            StartEntityDrag(args);
        };
        row.PointerReleased += (_, _) => pressArgs = null;

        row.ContextMenu = BuildEntityMenu(e, section);
        return row;
    }

    // Starts a drag carrying the current entity selection (project + ids), so they can be dropped onto
    // an open board window as cards. (Avalonia 12 needs the originating PointerPressedEventArgs.)
    void StartEntityDrag(PointerPressedEventArgs press)
    {
        if (_project is null || _selEntities.Count == 0) return;
        var item = new DataTransferItem();
        item.Set(CodeBoardWindow.EntityDragFormat, _project + CodeBoardWindow.DragSep + string.Join(",", _selEntities));
        var transfer = new DataTransfer();
        transfer.Add(item);
        _ = DragDrop.DoDragDropAsync(press, transfer, DragDropEffects.Copy);
    }

    // The per-entity right-click menu: Edit (+ Sketch flow / Set as main for functions), then — below a
    // separator to reduce mis-clicks — Delete (which removes the whole selection, with confirmation).
    ContextMenu BuildEntityMenu(CodeEntity e, Section section)
    {
        var cm = new ContextMenu();
        var edit = new MenuItem { Header = Loc.S("Code_Edit") };
        edit.Click += async (_, _) => await EditEntity(e, section);
        cm.Items.Add(edit);

        if (section == Section.Function)
        {
            var flow = new MenuItem { Header = Loc.S("Code_SketchFlow") };
            flow.Click += (_, _) => { if (_project is not null) _ = DiagramLauncher.ChooseAndOpen(this, _project!, e.Id, e.Name, null); };
            cm.Items.Add(flow);
            var setMain = new MenuItem { Header = Loc.S("Main_SetAs") };
            setMain.Click += (_, _) => SetAsMain(e);
            cm.Items.Add(setMain);
        }

        var setNs = new MenuItem { Header = Loc.S("Sec_SetNs") };
        setNs.Click += async (_, _) => await AssignNamespace(e, section);
        cm.Items.Add(setNs);

        cm.Items.Add(new Separator());
        var del = new MenuItem { Header = Loc.S("Sec_Delete") };
        del.Click += async (_, _) =>
        {
            var ids = _selEntities.Contains(e.Id) && _selEntities.Count > 0 ? _selEntities.ToList() : new() { e.Id };
            await DeleteEntities(section, ids);
        };
        cm.Items.Add(del);
        return cm;
    }

    // Repaints each row's border to reflect the current selection (transparent → accent).
    void RefreshEntitySelection()
    {
        foreach (var (ent, r) in _sectionRows)
        {
            if (_selEntities.Contains(ent.Id)) Ui.Theme(r, Border.BorderBrushProperty, "AccentBgBrush");
            else r.BorderBrush = Brushes.Transparent;
        }
    }

    // Selects the display-order range from the anchor to the given id (inclusive).
    void SelectEntityRangeTo(string id)
    {
        var order = _sectionRows.Select(t => t.e.Id).ToList();
        if (_selAnchor is null || !order.Contains(_selAnchor)) { _selEntities.Clear(); _selEntities.Add(id); _selAnchor = id; return; }
        int a = order.IndexOf(_selAnchor), b = order.IndexOf(id);
        if (a < 0 || b < 0) { _selEntities.Add(id); return; }
        if (a > b) (a, b) = (b, a);
        _selEntities.Clear();
        for (int i = a; i <= b; i++) _selEntities.Add(order[i]);
    }

    // Deletes a set of entities (the current multi-selection) after a single confirmation.
    Task DeleteEntities(Section section, IReadOnlyList<string> ids) => CrashHandler.SafeAsync(async () =>
    {
        if (_project is null || ids.Count == 0) return;
        var msg = ids.Count == 1
            ? string.Format(Loc.S("Sec_DeleteConfirm1"), _sectionRows.FirstOrDefault(t => t.e.Id == ids[0]).e?.Name ?? "")
            : string.Format(Loc.S("Sec_DeleteConfirmN"), ids.Count);

        // Warn (but never auto-delete) when a board is assigned to one of these entities or its methods.
        var assignedBoards = ids.SelectMany(id => CodeBoardRegistryService.BoardsAssignedTo(_project, id))
            .GroupBy(b => b.Id).Select(g => g.First().Name).ToList();
        if (assignedBoards.Count > 0)
            msg += "\n\n" + string.Format(Loc.S("Sec_DeleteBoardWarn"), string.Join(", ", assignedBoards));

        if (await MessageDialog.Show(this, msg, Loc.S("Sec_DeleteTitle"), DialogButtons.YesNo) != DialogResult.Yes) return;
        foreach (var id in ids) CodeEntityService.Delete(_project, section.ToString(), id);
        ShowSection(section);
    }, "DeleteEntities");

    // Rubber-band selection over the entity list: drag on empty space to box-select rows.
    void WireListRubberBand(Grid holder, Canvas overlay)
    {
        var selecting = false;
        var start = default(Point);
        Avalonia.Controls.Shapes.Rectangle? rect = null;

        holder.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(holder).Properties.IsLeftButtonPressed) return;   // row presses are handled, won't reach here
            _selEntities.Clear(); _selAnchor = null; RefreshEntitySelection();
            start = e.GetPosition(holder); selecting = true; e.Pointer.Capture(holder);
        };
        holder.PointerMoved += (_, e) =>
        {
            if (!selecting) return;
            var cur = e.GetPosition(holder);
            if (rect is null)
            {
                rect = new Avalonia.Controls.Shapes.Rectangle { Stroke = Brushes.DodgerBlue, StrokeThickness = 1, StrokeDashArray = new AvaloniaList<double> { 4, 2 }, Fill = new SolidColorBrush(Color.FromArgb(30, 30, 144, 255)), IsHitTestVisible = false };
                overlay.Children.Add(rect);
            }
            double x = Math.Min(start.X, cur.X), y = Math.Min(start.Y, cur.Y);
            Canvas.SetLeft(rect, x); Canvas.SetTop(rect, y);
            rect.Width = Math.Abs(cur.X - start.X); rect.Height = Math.Abs(cur.Y - start.Y);
        };
        holder.PointerReleased += (_, e) =>
        {
            if (!selecting) return;
            selecting = false; e.Pointer.Capture(null);
            if (rect is null) return;
            double rx = Canvas.GetLeft(rect), ry = Canvas.GetTop(rect), rw = rect.Width, rh = rect.Height;
            foreach (var (ent, r) in _sectionRows)
            {
                var b = r.Bounds;
                if (b.X < rx + rw && b.X + b.Width > rx && b.Y < ry + rh && b.Y + b.Height > ry) _selEntities.Add(ent.Id);
            }
            overlay.Children.Remove(rect); rect = null;
            RefreshEntitySelection();
        };
    }

    // ── Main (entry point) ───────────────────────────────────────────────────

    // The Main tab: the project's single entry-point function, surfaced on its own so it stays visible.
    void BuildMainView(StackPanel root)
    {
        root.Children.Add(Heading("Main"));
        root.Children.Add(Note(Loc.S("Main_Blurb")));
        if (_project is null) return;

        var main = CodeEntityService.LoadAll(_project, "Function").FirstOrDefault(e => e.IsEntryPoint);
        if (main is null)
        {
            var create = Ui.Btn(Loc.S("Main_Create"));
            create.HorizontalAlignment = HorizontalAlignment.Left;
            create.Click += (_, _) => CreateMain();
            root.Children.Add(create);
            return;
        }

        var card = new Border { Padding = new(14), CornerRadius = new(8), BorderThickness = new(1) };
        Ui.Theme(card, Border.BackgroundProperty, "ControlBgBrush");
        Ui.Theme(card, Border.BorderBrushProperty, "AccentBgBrush");

        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        nameRow.Children.Add(new TextBlock { Text = "▶", FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        var nm = new TextBlock { Text = main.Name, FontSize = 16, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        Ui.Theme(nm, TextBlock.ForegroundProperty, "ContentTextBrush");
        nameRow.Children.Add(nm);

        var edit = Ui.Btn(Loc.S("Code_Edit"));   edit.Click += async (_, _) => await EditEntity(main, Section.Main);
        var flow = Ui.Btn(Loc.S("Code_SketchFlow")); flow.Click += (_, _) => _ = DiagramLauncher.ChooseAndOpen(this, _project!, main.Id, main.Name, null);
        var unset = Ui.Btn(Loc.S("Main_Unset")); unset.Click += (_, _) => UnsetMain(main);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { edit, flow, unset } };

        card.Child = new StackPanel { Spacing = 10, Children = { nameRow, Dim(EntitySummary(main), 11), btns } };
        root.Children.Add(card);
    }

    // Creates a fresh entry-point function named "main".
    void CreateMain() => CrashHandler.Safe(() =>
    {
        if (_project is null) return;
        CodeEntityService.Save(_project, "Function", new CodeEntity { Name = "main", EntityType = CodeEntityType.Function, IsEntryPoint = true });
        ShowSection(Section.Main);
    }, "CreateMain");

    // Promotes a function to THE entry point, clearing the flag from any previous main (single main).
    void SetAsMain(CodeEntity e) => CrashHandler.Safe(() =>
    {
        if (_project is null) return;
        foreach (var fn in CodeEntityService.LoadAll(_project, "Function"))
        {
            var shouldBe = fn.Id == e.Id;
            if (fn.IsEntryPoint != shouldBe) { fn.IsEntryPoint = shouldBe; CodeEntityService.Save(_project, "Function", fn); }
        }
        ShowSection(Section.Main);
    }, "SetAsMain");

    // Demotes the entry point back to an ordinary function (it returns to the Functions tab).
    void UnsetMain(CodeEntity e) => CrashHandler.Safe(() =>
    {
        if (_project is null) return;
        e.IsEntryPoint = false;
        CodeEntityService.Save(_project, "Function", e);
        ShowSection(Section.Function);
    }, "UnsetMain");

    // Opens the structure editor for an entity; on save (which may change its type) refreshes the list.
    Task EditEntity(CodeEntity e, Section section) => CrashHandler.SafeAsync(async () =>
    {
        if (_project is null) return;
        var known = LoadAllEntities(_project);
        var saved = await CodeEntityEditorDialog.Edit(this, _project, e, known, null);
        if (saved) ShowSection(section);
    }, "EditEntity");

    // Every entity in the project, keyed by id — the candidate pool for inheritance / instance combos.
    static IReadOnlyDictionary<string, CodeEntity> LoadAllEntities(string projFolder)
    {
        var all = new Dictionary<string, CodeEntity>();
        foreach (var t in Enum.GetValues<CodeEntityType>())
            foreach (var ent in CodeEntityService.LoadAll(projFolder, t.ToString()))
                all[ent.Id] = ent;
        return all;
    }

    // ── Boards gallery ───────────────────────────────────────────────────────

    // Lists the project's code boards as cards with a "New board" action; clicking a card opens it.
    string? _selBoard;
    readonly List<(string id, Border tile)> _boardTiles = new();

    void BuildBoardsView(StackPanel root)
    {
        _boardTiles.Clear(); _selBoard = null;
        root.Children.Add(Heading("Boards"));

        var add = Ui.Btn(Loc.S("Boards_New"));
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Click += async (_, _) => await NewBoard();
        root.Children.Add(add);

        var boards = _project is null ? new() : CodeBoardRegistryService.Load(_project);
        if (boards.Count == 0)
        {
            root.Children.Add(Note(Loc.S("Boards_Empty")));
            return;
        }

        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in boards.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            wrap.Children.Add(BoardCard(b));
        root.Children.Add(wrap);
    }

    // One board tile: symbol + name; left-click opens it, right-click renames/deletes.
    Control BoardCard(CodeBoard board)
    {
        var tile = new Border { Width = 200, Padding = new(14), Margin = new(0, 0, 10, 10), CornerRadius = new(8), BorderThickness = new(2), BorderBrush = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
        Ui.Theme(tile, Border.BackgroundProperty, "ControlBgBrush");
        _boardTiles.Add((board.Id, tile));

        var stack = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = board.Symbol, FontSize = 28 },
                new TextBlock { Text = board.Name, FontWeight = FontWeight.Bold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        };
        // Show the assignment, if any ("→ Class.Method"), so a body-authoring board reads as such.
        if (_project is not null && !string.IsNullOrEmpty(board.TargetKey))
            stack.Children.Add(Dim("→ " + CodeBoardRegistryService.TargetLabel(_project, board.TargetKey), 11));
        tile.Child = stack;

        // Handled like entity rows: left-click selects, double-click opens, right-click → menu (Open…).
        tile.PointerPressed += (_, ev) =>
        {
            var pt = ev.GetCurrentPoint(tile);
            if (pt.Properties.IsRightButtonPressed) { _selBoard = board.Id; RefreshBoardSelection(); return; }
            if (!pt.Properties.IsLeftButtonPressed) return;
            if (ev.ClickCount >= 2) { OpenBoard(board); return; }
            _selBoard = board.Id; RefreshBoardSelection();
        };

        var open = new MenuItem { Header = Loc.S("Boards_Open") };
        open.Click += (_, _) => OpenBoard(board);
        var assign = new MenuItem { Header = Loc.S("Boards_Assign") };
        assign.Click += async (_, _) => await AssignBoard(board);
        var rename = new MenuItem { Header = Loc.S("Boards_Rename") };
        rename.Click += async (_, _) => await RenameBoard(board);
        var delete = new MenuItem { Header = Loc.S("Boards_Delete") };
        delete.Click += async (_, _) => await DeleteBoard(board);
        var cm = new ContextMenu();
        cm.Items.Add(open);
        cm.Items.Add(assign);
        if (!string.IsNullOrEmpty(board.TargetKey))
        {
            var clear = new MenuItem { Header = Loc.S("Boards_ClearAssign") };
            clear.Click += (_, _) => SetBoardTarget(board.Id, "");
            cm.Items.Add(clear);
        }
        cm.Items.Add(new Separator());
        cm.Items.Add(rename); cm.Items.Add(delete);
        tile.ContextMenu = cm;

        return tile;
    }

    // Highlights the selected board tile (single selection, mirrors the entity rows).
    void RefreshBoardSelection()
    {
        foreach (var (id, tile) in _boardTiles)
        {
            if (id == _selBoard) Ui.Theme(tile, Border.BorderBrushProperty, "AccentBgBrush");
            else tile.BorderBrush = Brushes.Transparent;
        }
    }

    // Picks a function/method for this board to author, then stores the assignment.
    Task AssignBoard(CodeBoard board) => CrashHandler.SafeAsync(async () =>
    {
        if (_project is null) return;
        // A board with classes/objects on it is an architecture view, not a function body.
        if (CodeBoardCodeGen.ContainsNonFunction(_project, board.Id))
        { await MessageDialog.Show(this, Loc.S("Boards_HasNonFunc"), Loc.S("Boards_Assign")); return; }
        var targets = CodeBoardRegistryService.AssignableTargets(_project);
        if (targets.Count == 0) { await MessageDialog.Show(this, Loc.S("Boards_NoTargets"), Loc.S("Boards_Assign")); return; }
        var key = await PickListDialog.Show(this, Loc.S("Boards_AssignTitle"), targets.Select(t => (t.Key, t.Label)).ToList());
        if (key is null) return;
        SetBoardTarget(board.Id, key);
    }, "AssignBoard");

    // Writes a board's TargetKey and refreshes the gallery.
    void SetBoardTarget(string boardId, string key) => CrashHandler.Safe(() =>
    {
        if (_project is null) return;
        var boards = CodeBoardRegistryService.Load(_project);
        var b = boards.FirstOrDefault(x => x.Id == boardId);
        if (b is null) return;
        b.TargetKey = key;
        CodeBoardRegistryService.Save(_project, boards);
        ShowSection(Section.Boards);
    }, "SetBoardTarget");

    // Opens a board on its own canvas window; its export buttons open the exporter on the chosen entities.
    void OpenBoard(CodeBoard board) => CrashHandler.Safe(() =>
    {
        if (_project is null) return;
        new CodeBoardWindow(_project, board, null,
            ents => new ExportWindow(_project, ents, board.Name).Show()).Show();
    }, "OpenBoard");

    // ── Export section ─────────────────────────────────────────────────────

    // Intro + an "Open exporter" action that exports every entity in the project.
    void BuildExportView(StackPanel root)
    {
        root.Children.Add(Heading("Export"));
        root.Children.Add(Note(Loc.S("Export_Intro")));

        var entities = _project is null ? new() : LoadAllEntities(_project).Values.ToList();
        if (entities.Count == 0) { root.Children.Add(Note(Loc.S("Export_Empty"))); return; }

        var open = Ui.Btn(Loc.S("Export_Open"));
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Click += (_, _) => CrashHandler.Safe(() =>
        {
            if (_project is null) return;
            new ExportWindow(_project, entities, ProjectName(_project)).Show();
        }, "OpenExporter");
        root.Children.Add(open);
    }

    Task NewBoard() => CrashHandler.SafeAsync(async () =>
    {
        if (_project is null) return;
        var name = await PromptDialog.Show(this, Loc.S("Boards_NewPrompt"), Loc.S("Boards_Default"), Loc.S("Boards_New"));
        if (string.IsNullOrWhiteSpace(name)) return;
        var boards = CodeBoardRegistryService.Load(_project);
        boards.Add(new CodeBoard { Name = name.Trim() });
        CodeBoardRegistryService.Save(_project, boards);
        ShowSection(Section.Boards);
    }, "NewBoard");

    Task RenameBoard(CodeBoard board) => CrashHandler.SafeAsync(async () =>
    {
        if (_project is null) return;
        var name = await PromptDialog.Show(this, Loc.S("Boards_NewPrompt"), board.Name, Loc.S("Boards_Rename"));
        if (string.IsNullOrWhiteSpace(name)) return;
        var boards = CodeBoardRegistryService.Load(_project);
        var match = boards.FirstOrDefault(b => b.Id == board.Id);
        if (match is null) return;
        match.Name = name.Trim();
        CodeBoardRegistryService.Save(_project, boards);
        ShowSection(Section.Boards);
    }, "RenameBoard");

    Task DeleteBoard(CodeBoard board) => CrashHandler.SafeAsync(async () =>
    {
        if (_project is null) return;
        var res = await MessageDialog.Show(this, string.Format(Loc.S("Boards_DeleteConfirm"), board.Name), Loc.S("Boards_DeleteTitle"), DialogButtons.YesNo);
        if (res != DialogResult.Yes) return;
        var boards = CodeBoardRegistryService.Load(_project);
        boards.RemoveAll(b => b.Id == board.Id);
        CodeBoardRegistryService.Save(_project, boards);
        ShowSection(Section.Boards);
    }, "DeleteBoard");

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
