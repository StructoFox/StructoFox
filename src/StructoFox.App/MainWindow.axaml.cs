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
    public const string Version = "0.9.9 BETA";

    string? _project;
    Section _section = Section.Class;
    readonly ContentControl _body = new();       // home browser  OR  project cockpit (added once)
    ContentControl _content = new();             // active cockpit section (re-created per cockpit build)
    readonly Dictionary<Section, Button> _railButtons = new();

    // Home browser state.
    string?  _homeSource;                        // null = Recent, else a library path
    bool     _sketchMode;                        // home showing the sketchbook (standalone diagrams)
    SketchType? _sketchType;                     // sketch type filter (null = all)
    ContentControl _sketchList = new();          // sketch-list region (re-created per build)
    StackPanel? _sketchFilterBar;                // collapsible name+sort bar in the sketch view
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
    HomeSort _secSort = HomeSort.NameAsc;
    Dictionary<string, string> _nsNames = new();   // namespace id → display name (for entity rows)
    bool     _secFilterOpen;
    StackPanel? _secFilterArea;
    ContentControl _secList = new();       // the filtered list region (re-rendered without rebuilding the bar)

    // Builds the shell window and shows the project browser.
    // Embedded (one-shot) mode: launched with a project path (e.g. from ClaudetRelay) → open that project's
    // cockpit directly, with no way back to the home browser; closing the window quits the app.
    readonly bool _embedded;

    // Parameterless ctor required by Avalonia's XAML runtime loader (avares://…/MainWindow.axaml); delegates to
    // the real one. The app itself always constructs via the string overload (App.axaml.cs).
    public MainWindow() : this(null) { }

    public MainWindow(string? projectPath)
    {
        InitializeComponent();
        Title = "StructoFox";
        Width = 1140; Height = 740; MinWidth = 760; MinHeight = 480;

        // Drop the OS title bar (keep the resizable border) so our top bar is the themed title bar.
        this.WindowDecorations = global::Avalonia.Controls.WindowDecorations.BorderOnly;
        Ui.ThemeWindow(this);

        Content = BuildShell();

        _embedded = !string.IsNullOrWhiteSpace(projectPath);
        if (_embedded)
            // Defer until the window is shown — creating a new subproject may need a modal syntax dialog.
            Opened += async (_, _) => await OpenEmbedded(projectPath!);
        else
            ShowHome();

        // Back up the open project when the app is quit, too (not just on explicit close).
        Closing += (_, _) => { if (_project is not null) TryAutoBackup(_project, interactive: false); };
    }

    // Opens the given folder as this project's cockpit; if it isn't a StructoFox project yet, creates it after
    // asking for the code syntax. Used only in embedded mode (a project path was passed on the command line).
    async Task OpenEmbedded(string path)
    {
        if (!ProjectService.IsProject(path))
        {
            Directory.CreateDirectory(path);
            var lang = await SyntaxDialog.Pick(this);
            if (lang is null) { Close(); return; }   // cancelled → nothing to open
            var info = ProjectService.Create(path, new DirectoryInfo(path.TrimEnd('/', '\\')).Name);
            info.Language = lang;
            ProjectService.Save(path, info);
        }
        OpenProject(path);
    }

    // Top bar (row 0) over the swappable body (row 1).
    Control BuildShell()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        var top = BuildTopBar(); Grid.SetRow(top, 0); root.Children.Add(top);
        // _body is a reused field; on a rebuild (e.g. language switch) it still hangs in the old shell,
        // and Avalonia forbids a second visual parent — so detach it first.
        (_body.Parent as Panel)?.Children.Remove(_body);
        Grid.SetRow(_body, 1); root.Children.Add(_body);
        return root;
    }

    // The themed title bar: fox brand (drag handle) + a discreet menu and window controls.
    Control BuildTopBar()
    {
        var bar = new Border { Padding = new(14, 8) };
        Ui.Theme(bar, Border.BackgroundProperty, "SidebarBgBrush");

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        // The fox logo (or the emoji as a fallback if the image resource is missing).
        if (Ui.AppLogo() is { } logo)
            brand.Children.Add(new Image { Source = logo, Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Center });
        else
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

        // "Close project" already takes you back to the project list; when no project is open you're already
        // there — so a separate "Home" entry was redundant. Only show close (+ its divider) with a project open.
        // In embedded mode there's no home to return to (the window IS the project), so it's hidden.
        if (_project is not null && !_embedded)
        {
            var close = new MenuItem { Header = Loc.S("Menu_CloseProject") };
            close.Click += (_, _) => ShowHome();
            cm.Items.Add(close);
            cm.Items.Add(new Separator());
        }

        // Print / Export composer — lay diagrams out on paper-sized pages and export to PDF/TIFF/PNG.
        // Available in a project AND in the sketchbook home view (the sketchbook isn't "opened" like a project,
        // so it has no _project — we target its root folder directly).
        if (_project is not null || _sketchMode)
        {
            var target = _project ?? SketchbookService.Root;
            var key    = "print:" + (_project ?? "sketchbook");
            var print  = new MenuItem { Header = "🖨  " + Loc.S("Menu_Print") };
            print.Click += (_, _) => DiagramWindows.OpenOrActivate(key, () => new PrintComposerWindow(target));
            cm.Items.Add(print);
            cm.Items.Add(new Separator());
        }

        var theme = new MenuItem { Header = Loc.S("Menu_Theme") };
        foreach (var (name, path) in ThemeManager.Available())
        {
            var p = path;
            var mi = new MenuItem { Header = name };
            mi.Click += (_, _) => ThemeManager.Apply(Application.Current!, p);
            theme.Items.Add(mi);
        }
        cm.Items.Add(theme);

        var pal = new MenuItem { Header = Loc.S("Menu_Palette") };
        pal.Click += (_, _) => new PaletteEditorWindow().Show();
        cm.Items.Add(pal);

        // Language: a radio-style pick per built-in language, applied live.
        var lang = new MenuItem { Header = Loc.S("Menu_Language") };
        foreach (var (code, name) in Loc.Builtins)
        {
            var c = code;
            var mi = new MenuItem { Header = name, ToggleType = MenuItemToggleType.Radio, IsChecked = Loc.Lang == code };
            mi.Click += (_, _) => SetLanguage(c);
            lang.Items.Add(mi);
        }
        cm.Items.Add(lang);

        // Options: norm-compliance toggles + a checkbox per suppressible message.
        var options = new MenuItem { Header = Loc.S("Menu_Options") };

        var normWarn = new MenuItem { Header = Loc.S("Opt_NormWarn"), ToggleType = MenuItemToggleType.CheckBox, IsChecked = AppSettings.NormWarn };
        ToolTip.SetTip(normWarn, Loc.S("Opt_NormWarnTip"));
        normWarn.Click += (_, _) => AppSettings.Set(AppSettings.NormWarnKey, normWarn.IsChecked);
        options.Items.Add(normWarn);
        var normMark = new MenuItem { Header = Loc.S("Opt_NormMark"), ToggleType = MenuItemToggleType.CheckBox, IsChecked = AppSettings.NormMark };
        ToolTip.SetTip(normMark, Loc.S("Opt_NormMarkTip"));
        normMark.Click += (_, _) => AppSettings.Set(AppSettings.NormMarkKey, normMark.IsChecked);
        options.Items.Add(normMark);
        options.Items.Add(new Separator());

        var defHeader = new MenuItem { Header = Loc.S("Menu_DefaultHeader") };
        ToolTip.SetTip(defHeader, Loc.S("Menu_DefaultHeaderTip"));
        defHeader.Click += (_, _) => CrashHandler.Safe(() => _ = DefaultHeaderDialog.Show(this), "DefaultHeader");
        options.Items.Add(defHeader);

        var userInfo = new MenuItem { Header = Loc.S("Menu_UserInfo") };
        ToolTip.SetTip(userInfo, Loc.S("Menu_UserInfoTip"));
        userInfo.Click += (_, _) => CrashHandler.Safe(() => _ = UserInfoDialog.Show(this), "UserInfo");
        options.Items.Add(userInfo);

        var backup = new MenuItem { Header = Loc.S("Menu_Backup") };
        ToolTip.SetTip(backup, Loc.S("Menu_BackupTip"));
        backup.Click += (_, _) => CrashHandler.Safe(() => _ = BackupSettingsDialog.Show(this), "BackupSettings");
        options.Items.Add(backup);
        options.Items.Add(new Separator());

        foreach (var (key, labelKey) in SuppressStore.Known)
        {
            var k = key;
            var mi = new MenuItem { Header = Loc.S(labelKey), ToggleType = MenuItemToggleType.CheckBox, IsChecked = !SuppressStore.IsSuppressed(key) };
            ToolTip.SetTip(mi, Loc.S("Opt_SuppressTip"));
            mi.Click += (_, _) => { if (mi.IsChecked) SuppressStore.Unsuppress(k); else SuppressStore.Suppress(k); };
            options.Items.Add(mi);
        }
        cm.Items.Add(options);

        // Extensions: every command contributed by a loaded plugin. With no plugins installed the menu is
        // hidden entirely (rather than showing an empty/disabled entry), so a plain deployment stays clean.
        if (PluginHost.Plugins.Count > 0)
        {
            var ext = new MenuItem { Header = Loc.S("Menu_Extensions") };
            foreach (var plugin in PluginHost.Plugins)
                foreach (var cmd in plugin.Commands)
                {
                    var c = cmd;
                    var mi = new MenuItem { Header = cmd.Title };
                    mi.Click += (_, _) => CrashHandler.Safe(() => c.Run(new PluginCtx(this)), "Plugin:" + c.Title);
                    ext.Items.Add(mi);
                }
            cm.Items.Add(ext);
        }

        cm.Items.Add(new Separator());

        var about = new MenuItem { Header = string.Format(Loc.S("Menu_About"), Version) };
        about.Click += (_, _) => ShowAbout();
        cm.Items.Add(about);

        cm.Open(anchor);
    }

    // Switches language and rebuilds the whole shell so every string re-resolves immediately. Already-open
    // child windows keep their old language until reopened (they read strings at construction).
    void SetLanguage(string code) => CrashHandler.Safe(() =>
    {
        if (Loc.Lang == code) return;
        Loc.SetLanguage(code);
        Content = BuildShell();
        if (_project is null) ShowHome(); else ShowSection(_section);
    }, "SetLanguage");

    // Read-only text panel a plugin can pop up (generated code, a lookup result, a message).
    internal void ShowPluginText(string title, string content)
    {
        var box = new TextBox
        {
            Text = content, IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, BorderThickness = new(0),
            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
        };
        var win = new Window
        {
            Title = string.IsNullOrWhiteSpace(title) ? "StructoFox" : title,
            Width = 620, Height = 440, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = box, Padding = new(12) },
        };
        Ui.ThemeWindow(win);
        win.Show(this);
    }

    // The context handed to a plugin command: the open project + UI helpers (so Core stays UI-free).
    sealed class PluginCtx(MainWindow w) : IPluginContext
    {
        public string? ProjectFolder => w._project;
        public string Language => Loc.Lang;
        public object? OwnerWindow => w;
        public void ApplyTheme(object window) { if (window is Window win) Ui.ThemeWindow(win); }
        public void ShowText(string title, string content) => w.ShowPluginText(title, content);
        public void Notify(string message) => w.ShowPluginText("", message);
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
        if (_project is not null) TryAutoBackup(_project, interactive: true);   // closing the current project
        _project = null;
        _body.Content = BuildHome();
    }, "ShowHome");

    // Auto-zips the project into the backup folder on close, if enabled and it changed since the last backup.
    // When interactive (the app is staying open), a bad backup folder or a failed backup is reported to the user;
    // on app exit that's skipped (a dialog can't reliably show while the window is closing).
    void TryAutoBackup(string projectFolder, bool interactive) => CrashHandler.Safe(() =>
    {
        if (!AppSettings.BackupOnClose) return;
        var root = AppSettings.BackupFolder;
        if (string.IsNullOrWhiteSpace(root)) return;

        if (BackupService.RootConflicts(root, projectFolder))
        {
            if (interactive) _ = MessageDialog.Show(this, Loc.S("Backup_ErrInside"), Loc.S("Backup_ErrTitle"));
            return;
        }
        if (!BackupService.NeedsBackup(projectFolder, root)) return;   // nothing changed → nothing to do
        if (BackupService.CreateBackup(projectFolder, root, AppSettings.BackupKeep) is null && interactive)
            _ = MessageDialog.Show(this, string.Format(Loc.S("Backup_ErrFailed"), root), Loc.S("Backup_ErrTitle"));
    }, "AutoBackup");

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

        // Two mutually-exclusive tabs — Projects / Drafts. The active one is shown in the right pane,
        // so no separate heading there is needed.
        var topStack = new StackPanel { Spacing = 6 };
        topStack.Children.Add(HomeTab(Loc.S("Home_TabProjects"), null,                 active: !_sketchMode, () => { if (_sketchMode) { _sketchMode = false; _body.Content = BuildHome(); } }));
        topStack.Children.Add(HomeTab(Loc.S("Sketch_Nav"),       Loc.S("Sketch_NavTip"), active:  _sketchMode, () => { if (!_sketchMode) { _sketchMode = true; _homeSource = null; _body.Content = BuildHome(); } }));
        Grid.SetRow(topStack, 0); grid.Children.Add(topStack);

        // The library folders float in the middle, centred, scrolling if many. Only show libraries that
        // still exist on disk (a deleted folder shouldn't linger; a disconnected drive just hides until back).
        if (_homeSource is not null && !Directory.Exists(_homeSource)) _homeSource = null;
        var libs = Libraries.Load().Where(Directory.Exists).ToList();
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

    // A left-rail tab (Projects / Drafts): accent-filled when active, otherwise neutral.
    Button HomeTab(string label, string? tip, bool active, Action onClick)
    {
        var b = new Button
        {
            Content = label, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new(8, 6), CornerRadius = new(6),
        };
        Ui.Theme(b, TemplatedControl.BackgroundProperty, active ? "AccentBgBrush"  : "ControlBgBrush");
        Ui.Theme(b, TemplatedControl.ForegroundProperty, active ? "AccentTextBrush" : "SidebarTextBrush");
        if (!string.IsNullOrEmpty(tip)) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
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
        b.Click += (_, _) => { _homeSource = active ? null : source; _sketchMode = false; _body.Content = BuildHome(); };

        ToolTip.SetTip(b, source);   // full library path on hover
        var remove = new MenuItem { Header = Loc.S("Home_RemoveLibrary") };
        remove.Click += (_, _) => { Libraries.Remove(source); if (_homeSource == source) _homeSource = null; _body.Content = BuildHome(); };
        var cm = new ContextMenu();
        cm.Items.Add(remove);
        b.ContextMenu = cm;
        return b;
    }

    // Right column: top third = the most-recent project (hero); bottom two-thirds = the project list.
    // The sketchbook view — mirrors the project view: create buttons, a "most recent" hero, the list,
    // and a pinned control row (view switcher + name/sort filter + a PAP/Structogram/Board type filter).
    Control BuildSketchView()
    {
        var grid = new Grid { Margin = new(20) };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));                        // create row
        grid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));  // hero
        grid.RowDefinitions.Add(new RowDefinition(new GridLength(2, GridUnitType.Star)));  // list + controls

        var create = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(0, 0, 0, 12) };
        void NewBtn(string label, SketchType type) { var b = Ui.Btn(label); b.Click += async (_, _) => await NewSketch(type); create.Children.Add(b); }
        NewBtn(Loc.S("Sketch_NewPap"),    SketchType.Pap);
        NewBtn(Loc.S("Sketch_NewStruct"), SketchType.Structogram);
        NewBtn(Loc.S("Sketch_NewBoard"),  SketchType.Board);
        create.Children.Add(new Border { Width = 16 });
        var browse = Ui.Btn(Loc.S("Sketch_Open"), Loc.S("Sketch_OpenTip"));
        browse.Click += (_, _) => OpenSketchbookWorkspace();
        create.Children.Add(browse);
        // Print / Export lives in the ≡ menu (same place as in a project) once the sketchbook workspace is open —
        // no separate button here, so the entry point is identical everywhere.
        Grid.SetRow(create, 0); grid.Children.Add(create);

        var hero = SketchHero(); Grid.SetRow(hero, 1); grid.Children.Add(hero);
        var bottom = BuildSketchBottom(); Grid.SetRow(bottom, 2); grid.Children.Add(bottom);
        return grid;
    }

    // Top third of the sketch view: the most-recently-opened sketch as a centred hero (or a placeholder).
    Control SketchHero()
    {
        var last = SketchbookService.Load().FirstOrDefault();
        Control card = last is null ? PlaceholderCard() : SketchTile(last, HomeView.BigCards);
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment   = VerticalAlignment.Center;
        return card;
    }

    // List region + a pinned control row: view dropdown, 🔎 (name+sort), and the type filter.
    Control BuildSketchBottom()
    {
        _sketchList = new ContentControl();
        var grid = new Grid { Margin = new(0, 8, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var listScroll = new ScrollViewer { Content = _sketchList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(listScroll, 0); grid.Children.Add(listScroll);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(0, 8, 0, 0) };
        row.Children.Add(ViewDropdown());

        var filter = Ui.Btn("🔎", Loc.S("Sec_FilterSortTip")); filter.Padding = new(10, 4);
        _sketchFilterBar = BuildFilterBar(RefreshSketchList); _sketchFilterBar.IsVisible = _homeFilterOpen;
        filter.Click += (_, _) => { _homeFilterOpen = !_homeFilterOpen; _sketchFilterBar.IsVisible = _homeFilterOpen; };
        row.Children.Add(filter);
        row.Children.Add(_sketchFilterBar);

        // Always-visible type filter as a dropdown (clearer at a glance than toggle buttons, and no
        // accidental click that hides everything).
        row.Children.Add(new Border { Width = 8 });
        const string allId = "all";
        var typeCombo = Ui.Combo(170);
        typeCombo.Items.Add(new ComboItem(Loc.S("Sketch_TypeAll"), allId));
        typeCombo.Items.Add(new ComboItem(Loc.S("Diag_Pap"),   nameof(SketchType.Pap)));
        typeCombo.Items.Add(new ComboItem(Loc.S("Diag_Ns"),    nameof(SketchType.Structogram)));
        typeCombo.Items.Add(new ComboItem(Loc.S("Diag_Board"), nameof(SketchType.Board)));
        var curId = _sketchType?.ToString() ?? allId;
        typeCombo.SelectedItem = typeCombo.Items.OfType<ComboItem>().FirstOrDefault(c => c.Id == curId) ?? typeCombo.Items[0];
        typeCombo.SelectionChanged += (_, _) =>
        {
            var id = (typeCombo.SelectedItem as ComboItem)?.Id;
            _sketchType = id is null or allId ? null : Enum.Parse<SketchType>(id);
            RefreshSketchList();
        };
        row.Children.Add(typeCombo);

        var bar = new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(bar, 1); grid.Children.Add(bar);

        RefreshSketchList();
        return grid;
    }

    // Filters (name + type), sorts and renders the sketch list into the chosen view.
    void RefreshSketchList() => CrashHandler.Safe(() =>
    {
        if (!_sketchMode) return;
        var items = SketchbookService.Load();
        if (_sketchType is { } t) items = items.Where(s => s.Type == t).ToList();
        if (!string.IsNullOrWhiteSpace(_homeFilter))
            items = items.Where(s => s.Name.Contains(_homeFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        items = _homeSort switch
        {
            HomeSort.DateAsc  => items.OrderBy(s => s.UpdatedAt).ToList(),
            HomeSort.NameAsc  => items.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            HomeSort.NameDesc => items.OrderByDescending(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            _                 => items.OrderByDescending(s => s.UpdatedAt).ToList(),
        };

        if (items.Count == 0) { _sketchList.Content = Note(Loc.S("Sketch_Empty")); return; }
        Panel panel = _homeView == HomeView.DetailList ? new StackPanel { Spacing = 4 } : new WrapPanel();
        foreach (var s in items) panel.Children.Add(SketchTile(s, _homeView));
        _sketchList.Content = panel;
    }, "RefreshSketchList");

    // One sketch tile in the chosen view; click opens it, right-click → rename / delete.
    Control SketchTile(Sketch s, HomeView view)
    {
        var icon = s.Type switch { SketchType.Pap => "🔁", SketchType.Structogram => "▦", _ => "🗺" };
        var date = s.UpdatedAt.ToLocalTime().ToString("g");
        TextBlock Name(double size) { var t = new TextBlock { Text = s.Name, FontSize = size, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }; Ui.Theme(t, TextBlock.ForegroundProperty, "ContentTextBrush"); return t; }
        TextBlock Ico(double size) => new() { Text = icon, FontFamily = Emoji, FontSize = size, VerticalAlignment = VerticalAlignment.Center };

        Control content = view switch
        {
            HomeView.BigCards => new StackPanel { Width = 230, Spacing = 4, Children = { Ico(26), Name(16), Dim(date, 11) } },
            HomeView.DetailList => new DockPanel { Children = { Tuck(Dim(date, 11), Dock.Right), Ico(16), new Border { Width = 6 }, Name(13) } },
            HomeView.MultiList => new DockPanel { Width = 240, Children = { Ico(15), new Border { Width = 6 }, Name(13) } },
            _ => new StackPanel { Width = 160, Spacing = 3, Children = { Ico(22), Name(14), Dim(date, 10) } },
        };

        var card = new Border { Padding = new(10, 8), CornerRadius = new(8), Cursor = new Cursor(StandardCursorType.Hand), Child = content };
        Ui.Theme(card, Border.BackgroundProperty, "ControlBgBrush");
        if (view is HomeView.Cards or HomeView.BigCards or HomeView.MultiList) card.Margin = new(0, 0, 10, 10);

        card.PointerPressed += (_, e) => { if (!e.GetCurrentPoint(card).Properties.IsRightButtonPressed) OpenSketch(s); };
        var cm = new ContextMenu();
        var rename = new MenuItem { Header = Loc.S("Proj_Rename") };
        rename.Click += async (_, _) =>
        {
            var n = await PromptDialog.Show(this, Loc.S("Sketch_NamePrompt"), s.Name);
            if (string.IsNullOrWhiteSpace(n)) return;
            SketchbookService.Rename(s.Id, n); _body.Content = BuildHome();
        };
        var del = new MenuItem { Header = Loc.S("Sec_Delete") };
        del.Click += async (_, _) =>
        {
            var res = await MessageDialog.Show(this, string.Format(Loc.S("Sketch_DeleteConfirm"), s.Name), Loc.S("Sec_DeleteTitle"), DialogButtons.YesNo);
            if (res != DialogResult.Yes) return;
            SketchbookService.Delete(s.Id); _body.Content = BuildHome();
        };
        var openFolder = new MenuItem { Header = Loc.S("Proj_OpenFolder") };
        openFolder.Click += (_, _) => OpenInExplorer(SketchbookService.Root);
        cm.Items.Add(openFolder);
        cm.Items.Add(rename); cm.Items.Add(new Separator()); cm.Items.Add(del);
        card.ContextMenu = cm;
        return card;
    }

    // Docks a control to a side and returns it (small inline helper for tile layouts).
    static Control Tuck(Control c, Dock side) { DockPanel.SetDock(c, side); return c; }

    // Prompts for a name, creates a sketch of the given type and opens it.
    Task NewSketch(SketchType type) => CrashHandler.SafeAsync(async () =>
    {
        var name = await PromptDialog.Show(this, Loc.S("Sketch_NamePrompt"), "");
        if (name is null) return;
        var s = SketchbookService.Create(type, name);
        OpenSketch(s);
        _body.Content = BuildHome();   // refresh the list
    }, "NewSketch");

    // Opens the whole sketchbook folder in the cockpit — the browser for cleaning up entities/boards that
    // were created on a sketch (so deleting a board's classes/objects is possible). Not added to recents.
    void OpenSketchbookWorkspace() => CrashHandler.Safe(() =>
    {
        var root = SketchbookService.Root;
        System.IO.Directory.CreateDirectory(root);
        // Name the workspace exactly like its folder ("Sketchbook") in every language, so the cockpit name
        // matches what the user sees in the file system. Also corrects an older marker (e.g. "Skizzenbuch").
        var info = ProjectService.Load(root);
        if (info is null || info.Name != "Sketchbook") ProjectService.Create(root, "Sketchbook");
        _project = root;
        _sketchMode = false;
        _section = Section.Class;   // land on a section the restricted sketchbook rail actually shows
        _railButtons.Clear();
        _body.Content = BuildCockpit();
        ShowSection(_section);
    }, "OpenSketchbookWorkspace");

    // Opens a sketch in its editor (the sketchbook folder doubles as the project folder).
    void OpenSketch(Sketch s) => CrashHandler.Safe(() =>
    {
        SketchbookService.Touch(s.Id);
        var root = SketchbookService.Root;
        System.IO.Directory.CreateDirectory(root);
        switch (s.Type)
        {
            case SketchType.Pap:         DiagramWindows.OpenOrActivate(DiagramWindows.FlowId(root, s.Id),   () => new FlowChartWindow(root, s.Id, s.Name, null)); break;
            case SketchType.Structogram: DiagramWindows.OpenOrActivate(DiagramWindows.StructId(root, s.Id), () => new StructogramWindow(root, s.Id, s.Name, null)); break;
            default:                     DiagramWindows.OpenOrActivate(DiagramWindows.BoardId(root, s.Id),  () => new CodeBoardWindow(root, new CodeBoard { Id = s.Id, Name = s.Name, Symbol = "🗺" }, null)); break;
        }
    }, "OpenSketch");

    Control BuildHomeMain()
    {
        if (_sketchMode) return BuildSketchView();

        var grid = new Grid { Margin = new(20) };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));                        // New project (top)
        grid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));  // hero
        grid.RowDefinitions.Add(new RowDefinition(new GridLength(2, GridUnitType.Star)));  // list

        var newBtn = Ui.Btn(Loc.S("Home_NewProject"), Loc.S("Home_NewProjectTip"));
        newBtn.HorizontalAlignment = HorizontalAlignment.Left;
        newBtn.Margin = new(0, 0, 0, 10);
        newBtn.Click += async (_, _) => await NewProject();
        Grid.SetRow(newBtn, 0); grid.Children.Add(newBtn);

        var hero = BuildHero(); Grid.SetRow(hero, 1); grid.Children.Add(hero);
        var bottom = BuildHomeBottom(); Grid.SetRow(bottom, 2); grid.Children.Add(bottom);
        return grid;
    }

    // True for the built-in sketchbook folder (a project-shaped folder that must not show among projects).
    static bool IsSketchbook(string path) =>
        string.Equals(path.TrimEnd('/', '\\'), SketchbookService.Root.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);

    // Top third: the most-recent project as a centred hero card, or a placeholder tile if there is none.
    Control BuildHero()
    {
        var last = RecentProjects.Load().FirstOrDefault(e => !IsSketchbook(e.Path) && Directory.Exists(e.Path));
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

        var t1 = new TextBlock { Text = Loc.S("Home_RecentLabel"), FontSize = 13, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center };
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

    // The collapsible filter + sort bar: a name filter and two sort toggles. The refresh action lets it
    // serve both the projects list and the sketch list.
    StackPanel BuildFilterBar(Action? refresh = null)
    {
        refresh ??= RefreshHomeList;
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var nameBox = new TextBox { PlaceholderText = Loc.S("Sec_FilterName"), Width = 200, Text = _homeFilter };
        nameBox.TextChanged += (_, _) => { _homeFilter = nameBox.Text ?? ""; refresh(); };
        bar.Children.Add(nameBox);

        // Two sort toggles (consistent with the cockpit): Date ↓↑ and A–Z / Z–A, active one highlighted.
        Button dateBtn = null!, azBtn = null!;
        void Restyle()
        {
            bool dateActive = _homeSort is HomeSort.DateDesc or HomeSort.DateAsc;
            dateBtn.Content = _homeSort == HomeSort.DateAsc ? "Date ↑" : "Date ↓";
            azBtn.Content   = _homeSort == HomeSort.NameDesc ? "Z–A" : "A–Z";
            Ui.Theme(dateBtn, TemplatedControl.BackgroundProperty, dateActive ? "AccentBgBrush"  : "ControlBgBrush");
            Ui.Theme(dateBtn, TemplatedControl.ForegroundProperty, dateActive ? "AccentTextBrush" : "SidebarTextBrush");
            Ui.Theme(azBtn,   TemplatedControl.BackgroundProperty, !dateActive ? "AccentBgBrush"  : "ControlBgBrush");
            Ui.Theme(azBtn,   TemplatedControl.ForegroundProperty, !dateActive ? "AccentTextBrush" : "SidebarTextBrush");
        }
        dateBtn = Ui.Btn("Date ↓"); dateBtn.Padding = new(8, 4);
        dateBtn.Click += (_, _) => { _homeSort = _homeSort == HomeSort.DateDesc ? HomeSort.DateAsc : HomeSort.DateDesc; Restyle(); refresh(); };
        azBtn = Ui.Btn("A–Z"); azBtn.Padding = new(8, 4);
        azBtn.Click += (_, _) => { _homeSort = _homeSort == HomeSort.NameAsc ? HomeSort.NameDesc : HomeSort.NameAsc; Restyle(); refresh(); };
        Restyle();
        bar.Children.Add(dateBtn);
        bar.Children.Add(azBtn);
        return bar;
    }

    // The project paths for the active source, each with a representative date (for sorting/display).
    // The sketchbook folder is a project-shaped folder too, but it belongs under the Sketchbook tab — never
    // list it among real projects.
    List<(string path, DateTime date)> HomeItems()
    {
        if (_homeSource is null)
            return RecentProjects.Load().Where(e => !IsSketchbook(e.Path) && Directory.Exists(e.Path)).Select(e => (e.Path, e.Opened)).ToList();
        return ProjectService.Scan(_homeSource).Where(p => !IsSketchbook(p)).Select(p => (p, ProjectDate(p))).ToList();
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
        if (_sketchMode) return;   // sketch view manages its own list
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
        while (true)
        {
            var res = await NewProjectDialog.Show(this, Libraries.Load());
            if (res is not { } r) return;
            var (parent, name, language) = r;
            if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(name)) return;

            var folder = Path.Combine(parent, SafeFolder(name));

            // Name/folder already a project in this library? Don't silently reopen it — ask.
            if (ProjectService.IsProject(folder))
            {
                var ans = await MessageDialog.Show(this, string.Format(Loc.S("NewProj_ExistsMsg"), name),
                    Loc.S("NewProj_ExistsTitle"), DialogButtons.YesNoCancel);
                if (ans == DialogResult.Cancel) return;
                if (ans == DialogResult.No) continue;   // pick another name
                // Yes → open the existing one
                Libraries.Add(parent);
                OpenProject(folder);
                return;
            }

            Libraries.Add(parent);   // register the chosen folder as a library (no-op if already one)
            var info = ProjectService.Create(folder, name);
            if (!string.IsNullOrEmpty(language)) { info.Language = language; ProjectService.Save(folder, info); }
            OpenProject(folder);
            return;
        }
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
        var name = new TextBlock { Text = ProjectName(path), FontSize = big ? 20 : 14, FontWeight = FontWeight.Bold };
        Ui.Theme(name, TextBlock.ForegroundProperty, "ContentTextBrush");

        var stack = new StackPanel { Spacing = 3, Children = { HeaderRow(ThemeSwatch(info?.PreferredTheme), name, date) } };
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
        // The project's generated-code folder (the codegen default). Create it on demand so it always opens.
        var openCode = new MenuItem { Header = Loc.S("Proj_OpenCode") };
        openCode.Click += (_, _) => CrashHandler.Safe(() =>
        {
            var code = System.IO.Path.Combine(path, "Code");
            System.IO.Directory.CreateDirectory(code);
            OpenInExplorer(code);
        }, "OpenCodeFolder");
        var cm = new ContextMenu();
        cm.Items.Add(rename);
        cm.Items.Add(openFolder);
        cm.Items.Add(openCode);
        c.ContextMenu = cm;
    }

    // Renames a project (its display name in the marker — the folder is left untouched), then refreshes.
    Task RenameProject(string path) => CrashHandler.SafeAsync(async () =>
    {
        var name = await PromptDialog.Show(this, Loc.S("Proj_RenamePrompt"), ProjectName(path), Loc.S("Proj_Rename"));
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (name.Length > ProjectService.MaxNameLength) name = name[..ProjectService.MaxNameLength];   // keep tiles tidy
        var info = ProjectService.Load(path) ?? ProjectService.Create(path, name);
        info.Name = name;
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

    // A card's header: the date as a small line on top, the name BELOW it on its own full-width row (with the
    // optional theme swatch). The name wraps rather than being clipped, so it never collides with the date.
    Control HeaderRow(Control? swatch, TextBlock name, DateTime date)
    {
        var when = Dim(Friendly(date), 10);
        when.HorizontalAlignment = HorizontalAlignment.Right;

        name.TextWrapping = TextWrapping.Wrap;   // full width, wraps to more lines instead of truncating

        var nameRow = new Grid { ColumnDefinitions = new("Auto,*") };
        if (swatch is not null)
        {
            swatch.Margin = new(0, 2, 6, 0);
            swatch.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(swatch, 0); nameRow.Children.Add(swatch);
        }
        Grid.SetColumn(name, 1); nameRow.Children.Add(name);

        return new StackPanel { Spacing = 2, Children = { when, nameRow } };
    }

    // A large, detail-rich tile: theme swatch + name, description, content counts, date and path.
    Control BigCard(string path, DateTime date)
    {
        var card = new Border { Width = 250, Padding = new(16), Margin = new(5), CornerRadius = new(8), Cursor = new Cursor(StandardCursorType.Hand) };
        Ui.Theme(card, Border.BackgroundProperty, "ControlBgBrush");

        var info = ProjectService.Load(path);
        var name = new TextBlock { Text = ProjectName(path), FontSize = 18, FontWeight = FontWeight.Bold };
        Ui.Theme(name, TextBlock.ForegroundProperty, "ContentTextBrush");

        var stack = new StackPanel { Spacing = 4, Children = { HeaderRow(ThemeSwatch(info?.PreferredTheme), name, date) } };
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

        var stack = new StackPanel { Spacing = 4, Margin = new(0, 0, 8, 0) };   // right gutter so the scrollbar doesn't overlap buttons
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
            // Code export now lives per-structogram (the "Code skeleton" button in the structogram editor), not as a
            // project section next to the entity libraries — it isn't a library and was easy to confuse with printing.
        };
        // The sketchbook opened as a workspace tidies up the entities a board created there; Namespaces and Main make
        // no sense for standalone sketches, and Boards are already on the sketchbook home — so hide those three.
        if (_project is not null && IsSketchbook(_project))
            items = items.Where(t => t.sec is not (Section.Namespace or Section.Main or Section.Boards)).ToArray();

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
        _content = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };   // fill so a section can pin its footer
        var host = new Border();
        Ui.Theme(host, Border.BackgroundProperty, "ContentBgBrush");

        var layered = new Grid();
        var comb = new HoneycombBackdrop();
        Ui.Theme(comb, HoneycombBackdrop.LineBrushProperty, "AccentBgBrush");
        layered.Children.Add(comb);
        layered.Children.Add(_content);   // the section view fills the area and manages its own scroll + footer
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
        var content = new StackPanel { Spacing = 12, MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left };
        content.Children.Add(new TextBlock { Text = ProjectName(_project ?? ""), FontFamily = Mono, FontSize = 11, Opacity = 0.6 });

        Control? bottomBar = null;
        if (section == Section.Boards) { BuildBoardsView(content); bottomBar = BuildBoardsBottomBar(); }
        else if (section == Section.Main) { BuildMainView(content); }
        else if (section == Section.Export) { BuildExportView(content); }
        else
        {
            content.Children.Add(Heading(PluralLabel(section)));
            var add = Ui.Btn(string.Format(Loc.S("Sec_New"), SingularLabel(section)));
            add.HorizontalAlignment = HorizontalAlignment.Left;
            add.Click += async (_, _) => await NewEntity(section);
            content.Children.Add(add);
            _secList = new ContentControl();
            content.Children.Add(_secList);
            RefreshSecList(section);
            bottomBar = BuildSectionBottomBar(section);
        }

        // Scroll the content; pin the filter/view bar at the bottom for a continuous menu (like home).
        var scroll = new ScrollViewer { Content = content, Padding = new(24, 24, 24, 8), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        if (bottomBar is null) return scroll;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(scroll, 0); grid.Children.Add(scroll);
        Grid.SetRow(bottomBar, 1); grid.Children.Add(bottomBar);
        return grid;
    }

    // The pinned bottom bar of an entity section: [view ▾] [🔎 name+sort] [Namespace ▾ — always shown].
    Control BuildSectionBottomBar(Section section)
    {
        var bar = new Border { Padding = new(24, 8) };
        Ui.Theme(bar, Border.BackgroundProperty, "SidebarBgBrush");

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

        var viewBtn = Ui.Btn(ViewIcon(_secView) + "  ▾", "View"); viewBtn.Padding = new(10, 4);
        viewBtn.Click += (_, _) =>
        {
            var cm = new ContextMenu();
            void Item(string label, HomeView v) { var mi = new MenuItem { Header = label }; mi.Click += (_, _) => { _secView = v; viewBtn.Content = ViewIcon(_secView) + "  ▾"; RefreshSecList(section); }; cm.Items.Add(mi); }
            Item("▦  Kacheln", HomeView.Cards);
            Item("▣  Große Kacheln", HomeView.BigCards);
            Item("≣  Einspaltige Liste (Details)", HomeView.DetailList);
            Item("☷  Mehrspaltige Liste", HomeView.MultiList);
            cm.Open(viewBtn);
        };
        row.Children.Add(viewBtn);

        var find = Ui.Btn("🔎", Loc.S("Sec_FilterSortTip")); find.Padding = new(10, 4);
        _secFilterArea = BuildSecFilterArea(section);
        _secFilterArea.IsVisible = _secFilterOpen;
        find.Click += (_, _) => { _secFilterOpen = !_secFilterOpen; _secFilterArea.IsVisible = _secFilterOpen; };
        row.Children.Add(find);

        // Namespace dropdown — always visible. Items carry the namespace id; filtering is by id.
        const string nsAll = "all";
        var nsCombo = Ui.Combo(180);
        nsCombo.Items.Add(new ComboItem(Loc.S("Sec_NsAll"), nsAll));
        nsCombo.Items.Add(new ComboItem(Loc.S("Sec_NsNone"), ""));
        if (_project is not null)
        {
            var nsFull = NamespaceService.FullNames(_project);
            foreach (var n in CodeEntityService.LoadAll(_project, "Namespace")
                         .OrderBy(n => nsFull.GetValueOrDefault(n.Id, n.Name), StringComparer.OrdinalIgnoreCase))
                nsCombo.Items.Add(new ComboItem(nsFull.GetValueOrDefault(n.Id, n.Name), n.Id));
        }
        var curId = _secNamespace ?? nsAll;
        nsCombo.SelectedItem = nsCombo.Items.OfType<ComboItem>().FirstOrDefault(c => c.Id == curId) ?? nsCombo.Items[0];
        nsCombo.SelectionChanged += (_, _) =>
        {
            var id = (nsCombo.SelectedItem as ComboItem)?.Id;
            _secNamespace = id == nsAll ? null : id;
            RefreshSecList(section);
        };
        row.Children.Add(new TextBlock { Text = Loc.S("CodeEdit_Namespace"), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(nsCombo);

        row.Children.Add(_secFilterArea);

        bar.Child = new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        return bar;
    }

    // The collapsible part behind 🔎: a name filter + two sort toggles (Date ↓↑, A–Z / Z–A).
    StackPanel BuildSecFilterArea(Section section)
    {
        var nameBox = new TextBox { Width = 180, Text = _secFilter, PlaceholderText = Loc.S("Sec_FilterName") };
        Ui.Theme(nameBox, TextBox.BackgroundProperty,  "InputBgBrush");
        Ui.Theme(nameBox, TextBox.ForegroundProperty,  "SidebarTextBrush");
        Ui.Theme(nameBox, TextBox.BorderBrushProperty, "ControlBorderBrush");
        nameBox.TextChanged += (_, _) => { _secFilter = nameBox.Text ?? ""; RefreshSecList(section); };

        Button dateBtn = null!, azBtn = null!;
        void Restyle()
        {
            bool dateActive = _secSort is HomeSort.DateDesc or HomeSort.DateAsc;
            dateBtn.Content = _secSort == HomeSort.DateAsc ? "Date ↑" : "Date ↓";
            azBtn.Content   = _secSort == HomeSort.NameDesc ? "Z–A" : "A–Z";
            Ui.Theme(dateBtn, TemplatedControl.BackgroundProperty, dateActive ? "AccentBgBrush"  : "ControlBgBrush");
            Ui.Theme(dateBtn, TemplatedControl.ForegroundProperty, dateActive ? "AccentTextBrush" : "SidebarTextBrush");
            Ui.Theme(azBtn,   TemplatedControl.BackgroundProperty, !dateActive ? "AccentBgBrush"  : "ControlBgBrush");
            Ui.Theme(azBtn,   TemplatedControl.ForegroundProperty, !dateActive ? "AccentTextBrush" : "SidebarTextBrush");
        }
        dateBtn = Ui.Btn("Date ↓"); dateBtn.Padding = new(8, 4);
        dateBtn.Click += (_, _) => { _secSort = _secSort == HomeSort.DateDesc ? HomeSort.DateAsc : HomeSort.DateDesc; Restyle(); RefreshSecList(section); };
        azBtn = Ui.Btn("A–Z"); azBtn.Padding = new(8, 4);
        azBtn.Click += (_, _) => { _secSort = _secSort == HomeSort.NameAsc ? HomeSort.NameDesc : HomeSort.NameAsc; Restyle(); RefreshSecList(section); };
        Restyle();

        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0),
            Children = { nameBox, dateBtn, azBtn },
        };
    }

    // Re-renders just the list region for the current filter + sort (so the bar keeps focus).
    void RefreshSecList(Section section) => CrashHandler.Safe(() =>
    {
        _nsNames = _project is null ? new() : NamespaceService.FullNames(_project);
        var entities = _project is null ? new() : CodeEntityService.LoadAll(_project, section.ToString());
        if (section == Section.Function) entities = entities.Where(e => !e.IsEntryPoint).ToList();
        entities = entities.Where(e =>
            (string.IsNullOrEmpty(_secFilter) || e.Name.Contains(_secFilter, StringComparison.OrdinalIgnoreCase)) &&
            (_secNamespace is null || (_secNamespace == "" ? string.IsNullOrEmpty(e.Namespace) : e.Namespace == _secNamespace)))
            .ToList();
        DateTime FT(CodeEntity e) => CodeEntityService.FileTime(_project!, section.ToString(), e.Id);
        entities = _secSort switch
        {
            HomeSort.DateDesc => entities.OrderByDescending(FT).ToList(),
            HomeSort.DateAsc  => entities.OrderBy(FT).ToList(),
            HomeSort.NameDesc => entities.OrderByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            _                 => entities.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        };
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
        foreach (var e in entities)   // already filtered + sorted by RefreshSecList
            list.Children.Add(EntityRow(e, section, _secView));

        var overlay = new Canvas { IsHitTestVisible = false };
        var holder  = new Grid { Background = Brushes.Transparent };
        holder.Children.Add(list);
        holder.Children.Add(overlay);
        WireListRubberBand(holder, overlay);
        return holder;
    }

    // The inner content of an entity tile/row, laid out for the chosen view.
    Control EntityRowContent(CodeEntity e, HomeView view)
    {
        // The namespace (full nested path) is shown as a small dimmed "App." prefix in front of the bold
        // name, rather than a separate 🏷 tag — clearer, and reads like the real dotted name.
        var nsName = string.IsNullOrWhiteSpace(e.Namespace) ? null
            : (_nsNames.TryGetValue(e.Namespace, out var nsn) ? nsn : e.Namespace);

        Control NamePlate(double size)
        {
            var name = new TextBlock { Text = e.Name, FontSize = size, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Ui.Theme(name, TextBlock.ForegroundProperty, "ContentTextBrush");
            if (string.IsNullOrEmpty(nsName)) return name;
            var prefix = new TextBlock { Text = nsName + ".", FontSize = size, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center };
            Ui.Theme(prefix, TextBlock.ForegroundProperty, "ContentTextBrush");
            return new StackPanel { Orientation = Orientation.Horizontal, Children = { prefix, name } };
        }

        var summary = EntitySummary(e);   // empty for kinds with nothing to summarise (e.g. namespaces)
        switch (view)
        {
            case HomeView.Cards:
            {
                var st = new StackPanel { Width = 180, Spacing = 2, Children = { NamePlate(14) } };
                if (summary.Length > 0) st.Children.Add(Dim(summary, 11));
                return st;
            }
            case HomeView.BigCards:
            {
                var st = new StackPanel { Width = 250, Spacing = 3, Children = { NamePlate(16) } };
                if (summary.Length > 0) st.Children.Add(Dim(summary, 12));
                return st;
            }
            case HomeView.MultiList:
            {
                var dock = new DockPanel { Width = 240 };
                if (summary.Length > 0) { var sum = Dim(summary, 11); sum.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(sum, Dock.Right); dock.Children.Add(sum); }
                var nm = NamePlate(13); DockPanel.SetDock(nm, Dock.Left); dock.Children.Add(nm);
                return dock;
            }
            default: // DetailList — full-width row: name left, summary right
            {
                var dock = new DockPanel();
                if (summary.Length > 0) { var sum = Dim(summary, 11); sum.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(sum, Dock.Right); dock.Children.Add(sum); }
                var nm = NamePlate(13); DockPanel.SetDock(nm, Dock.Left); dock.Children.Add(nm);
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

        // Hint: still referenced by other documents (those links will be cleared on delete).
        var usedBy = ids.SelectMany(id => ProjectUsageService.FindReferrers(_project, id))
            .Select(u => $"•  {u.Referrer}  ({Loc.S("Usage_" + u.Kind)})").Distinct().ToList();
        if (usedBy.Count > 0)
            msg += "\n\n" + string.Format(Loc.S("Link_UsedByDelete"), "\n" + string.Join("\n", usedBy));

        if (await MessageDialog.Show(this, msg, Loc.S("Sec_DeleteTitle"), DialogButtons.YesNo) != DialogResult.Yes) return;
        foreach (var id in ids)
        {
            ProjectUsageService.RemoveReferences(_project, id);   // clear dangling links before removing the entity
            CodeEntityService.Delete(_project, section.ToString(), id);
        }
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
        var taken = CodeEntityService.LoadAll(_project, "Function").Select(e => e.Id);
        CodeEntityService.Save(_project, "Function",
            new CodeEntity { Name = "main", EntityType = CodeEntityType.Function, IsEntryPoint = true, Id = NameKeys.From("main", taken) });
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
    string  _boardFilter = "";
    HomeSort _boardSort = HomeSort.NameAsc;
    bool     _boardFilterOpen;
    StackPanel? _boardFilterArea;
    ContentControl _boardList = new();

    void BuildBoardsView(StackPanel root)
    {
        root.Children.Add(Heading("Boards"));

        var add = Ui.Btn(Loc.S("Boards_New"));
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Click += async (_, _) => await NewBoard();
        root.Children.Add(add);

        _boardList = new ContentControl();
        root.Children.Add(_boardList);
        RefreshBoardList();
    }

    // Re-renders the boards gallery for the current name filter + sort.
    void RefreshBoardList() => CrashHandler.Safe(() =>
    {
        _boardTiles.Clear(); _selBoard = null;
        var boards = _project is null ? new() : CodeBoardRegistryService.Load(_project);
        boards = boards.Where(b => string.IsNullOrEmpty(_boardFilter) || b.Name.Contains(_boardFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        boards = _boardSort switch
        {
            HomeSort.DateDesc => boards.OrderByDescending(b => b.UpdatedAt).ToList(),
            HomeSort.DateAsc  => boards.OrderBy(b => b.UpdatedAt).ToList(),
            HomeSort.NameDesc => boards.OrderByDescending(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            _                 => boards.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        };
        if (boards.Count == 0) { _boardList.Content = Note(Loc.S("Boards_Empty")); return; }
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in boards) wrap.Children.Add(BoardCard(b));
        _boardList.Content = wrap;
    }, "RefreshBoardList");

    // The boards gallery's pinned bottom bar: just 🔎 (name filter + sort toggles) — no namespace/views.
    Control BuildBoardsBottomBar()
    {
        var bar = new Border { Padding = new(24, 8) };
        Ui.Theme(bar, Border.BackgroundProperty, "SidebarBgBrush");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

        var find = Ui.Btn("🔎", Loc.S("Sec_FilterSortTip")); find.Padding = new(10, 4);
        _boardFilterArea = BuildBoardFilterArea();
        _boardFilterArea.IsVisible = _boardFilterOpen;
        find.Click += (_, _) => { _boardFilterOpen = !_boardFilterOpen; _boardFilterArea.IsVisible = _boardFilterOpen; };
        row.Children.Add(find);
        row.Children.Add(_boardFilterArea);

        bar.Child = new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        return bar;
    }

    StackPanel BuildBoardFilterArea()
    {
        var nameBox = new TextBox { Width = 180, Text = _boardFilter, PlaceholderText = Loc.S("Sec_FilterName") };
        Ui.Theme(nameBox, TextBox.BackgroundProperty,  "InputBgBrush");
        Ui.Theme(nameBox, TextBox.ForegroundProperty,  "SidebarTextBrush");
        Ui.Theme(nameBox, TextBox.BorderBrushProperty, "ControlBorderBrush");
        nameBox.TextChanged += (_, _) => { _boardFilter = nameBox.Text ?? ""; RefreshBoardList(); };

        Button dateBtn = null!, azBtn = null!;
        void Restyle()
        {
            bool dateActive = _boardSort is HomeSort.DateDesc or HomeSort.DateAsc;
            dateBtn.Content = _boardSort == HomeSort.DateAsc ? "Date ↑" : "Date ↓";
            azBtn.Content   = _boardSort == HomeSort.NameDesc ? "Z–A" : "A–Z";
            Ui.Theme(dateBtn, TemplatedControl.BackgroundProperty, dateActive ? "AccentBgBrush"  : "ControlBgBrush");
            Ui.Theme(dateBtn, TemplatedControl.ForegroundProperty, dateActive ? "AccentTextBrush" : "SidebarTextBrush");
            Ui.Theme(azBtn,   TemplatedControl.BackgroundProperty, !dateActive ? "AccentBgBrush"  : "ControlBgBrush");
            Ui.Theme(azBtn,   TemplatedControl.ForegroundProperty, !dateActive ? "AccentTextBrush" : "SidebarTextBrush");
        }
        dateBtn = Ui.Btn("Date ↓"); dateBtn.Padding = new(8, 4);
        dateBtn.Click += (_, _) => { _boardSort = _boardSort == HomeSort.DateDesc ? HomeSort.DateAsc : HomeSort.DateDesc; Restyle(); RefreshBoardList(); };
        azBtn = Ui.Btn("A–Z"); azBtn.Padding = new(8, 4);
        azBtn.Click += (_, _) => { _boardSort = _boardSort == HomeSort.NameAsc ? HomeSort.NameDesc : HomeSort.NameAsc; Restyle(); RefreshBoardList(); };
        Restyle();

        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0), Children = { nameBox, dateBtn, azBtn } };
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
        var rename = new MenuItem { Header = Loc.S("Boards_Rename") };
        rename.Click += async (_, _) => await RenameBoard(board);
        var delete = new MenuItem { Header = Loc.S("Boards_Delete") };
        delete.Click += async (_, _) => await DeleteBoard(board);
        var cm = new ContextMenu();
        cm.Items.Add(open);
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

    // Opens a board on its own canvas window; its export buttons open the exporter on the chosen entities.
    void OpenBoard(CodeBoard board) => CrashHandler.Safe(() =>
    {
        if (_project is null) return;
        DiagramWindows.OpenOrActivate(DiagramWindows.BoardId(_project, board.Id),
            () => new CodeBoardWindow(_project, board, null));
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
        var bn = name.Trim();
        boards.Add(new CodeBoard { Name = bn, Id = NameKeys.From(bn, boards.Select(b => b.Id)) });
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
        // Keep the board's readable data-file name in step with its name.
        var newId = NameKeys.From(match.Name, boards.Where(b => b != match).Select(b => b.Id));
        if (newId != match.Id) { CodeBoardDataService.Rename(_project, match.Id, newId); match.Id = newId; }
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
        CodeEntityType.Namespace => "",   // a namespace has no fields/methods to summarise
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
        name = name.Trim();

        // For a new namespace, pick its parent right away (nesting). Cancelling the picker aborts.
        var nsId = "";
        if (section == Section.Namespace)
        {
            var nsFull = NamespaceService.FullNames(_project);
            var items = new List<(string, string)> { ("", Loc.S("Sec_NsNone")) };
            items.AddRange(CodeEntityService.LoadAll(_project, "Namespace")
                .OrderBy(n => nsFull.GetValueOrDefault(n.Id, n.Name), StringComparer.OrdinalIgnoreCase)
                .Select(n => (n.Id, nsFull.GetValueOrDefault(n.Id, n.Name))));
            var picked = await PickListDialog.Show(this, Loc.S("CodeEdit_ParentNamespace"), items);
            if (picked is null) return;
            nsId = picked;
        }

        // Warn if that name is already taken in the chosen namespace, so the user knows they're about to make a
        // twin rather than silently stacking duplicates. Class/Struct/Interface/Enum all declare a NAMED TYPE and
        // share one type-name space — a same-named type of a DIFFERENT kind would collide in the generated code, so
        // check across all of them (not just this section). Other kinds (Function/Object/Namespace) check same-kind.
        string[] typeKinds = { "Class", "Struct", "Interface", "Enum" };
        var pool = typeKinds.Contains(section.ToString())
            ? typeKinds.SelectMany(t => CodeEntityService.LoadAll(_project, t))
            : CodeEntityService.LoadAll(_project, section.ToString());
        var clash = pool.FirstOrDefault(x => x.Namespace == nsId && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
        {
            // Name the EXISTING kind ("… a Class named X already exists"), which may differ from the one being added.
            var res = await MessageDialog.Show(this,
                string.Format(Loc.S("Sec_DupMsg"), Loc.S("SecSg_" + clash.EntityType), name), Loc.S("Sec_DupTitle"), DialogButtons.YesNo);
            if (res != DialogResult.Yes) return;
        }

        var takenKeys = CodeEntityService.LoadAll(_project, section.ToString()).Select(e => e.Id);
        var entity = new CodeEntity
        {
            Name = name, EntityType = Enum.Parse<CodeEntityType>(section.ToString()), Namespace = nsId,
            Id = NameKeys.From(name, takenKeys),   // readable, unique document key (= filename)
        };
        CodeEntityService.Save(_project, section.ToString(), entity);
        ShowSection(section);
    }, "NewEntity");

    // Plural heading label for a section (localized).
    static string PluralLabel(Section s) => Loc.S("SecPl_" + s);

    // Singular label (the entity type name) for "New …" actions (localized).
    static string SingularLabel(Section s) => Loc.S("SecSg_" + s);

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
