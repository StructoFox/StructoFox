using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OXSUIT.Loaders.Avalonia;
using StructoFox.Core;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// Nassi-Shneiderman structogram editor (DIN 66261). Blocks nest space-fillingly —
/// no free positioning, no arrows. The structure is edited via context menus and
/// re-rendered on every change. Avalonia port of ClaudetRelay's editor.
/// </summary>
public class StructogramWindow : Window
{
    readonly string  _projFolder;
    readonly string  _key;
    readonly string? _themePath;
    StructogramData  _data;

    Border? _hostBorder;

    // The diagram surface look — user-controlled, theme-independent, persisted with the diagram.
    readonly DiagramStyle _style;
    IBrush _lineBrush = Brushes.Black;   // structural lines / borders
    IBrush _textBrush = Brushes.Black;   // block text
    IBrush _bgBrush   = Brushes.White;   // canvas background

    ContextMenu? _menu;   // the one open context menu, so a new one closes the old (no stacking)

    // Opens a context menu over an anchor, first closing any menu still showing.
    void OpenMenu(ContextMenu cm, Control anchor) { _menu?.Close(); _menu = cm; cm.Open(anchor); }

    // Loads (or starts) the structogram for one function/method and builds the editor surface.
    public StructogramWindow(string projFolder, string key, string title, string? themePath)
    {
        _projFolder = projFolder;
        _key        = key;
        _themePath  = themePath;
        _data       = StructogramService.Load(projFolder, key);
        if (string.IsNullOrEmpty(_data.Title)) _data.Title = title;
        _style      = _data.Style;   // persisted with the diagram

        Title                 = string.Format(Loc.S("Struct_Title"),
                                    string.IsNullOrEmpty(title) ? Loc.S("Common_Untitled") : title);
        Width                 = 760;
        Height                = 620;
        MinWidth              = 420;
        MinHeight             = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        if (!string.IsNullOrWhiteSpace(themePath))
            try { Resources.MergedDictionaries.Add(OxsuitLoader.Load(themePath)); } catch { /* unthemed is fine */ }
        Ui.ThemeWindow(this);

        Build();
    }

    // Persists the current structogram to disk after every edit.
    void Save() => StructogramService.Save(_projFolder, _key, _data);

    // Assembles the toolbar + scrollable diagram host, then renders the tree once.
    void Build()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        Content = root;
        _root = root;

        // Toolbar: a background-colour button plus a usage hint.
        var bar = new Border { Padding = new(12, 8, 12, 8) };
        Ui.Theme(bar, Border.BackgroundProperty, "SidebarBgBrush");
        Grid.SetRow(bar, 0); root.Children.Add(bar);

        var bgBtn = Ui.Btn("🎨", Loc.S("Flow_Background"));
        bgBtn.Click += async (_, _) =>
        {
            var hex = await ColorPickDialog.Pick(this, Loc.S("Flow_Background"), _style.BackgroundColor);
            if (hex is null) return;
            _style.BackgroundColor = hex;
            _bgBrush = new SolidColorBrush(Color.Parse(hex));
            if (_hostBorder is not null) _hostBorder.Background = _bgBrush;
            Save();
        };

        var hint = new TextBlock
        {
            Text = Loc.S("Struct_Hint"),
            VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Opacity = 0.8,
        };
        Ui.Theme(hint, TextBlock.ForegroundProperty, "SidebarTextBrush");

        var decorBtn = Ui.Btn(Loc.S("Decor_Open"), Loc.S("Decor_OpenTip"));
        decorBtn.Click += (_, _) => _ = OpenDecor();

        bar.Child = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children = { bgBtn, decorBtn, hint },
        };

        // Scrollable diagram host — the structogram can grow past the window.
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new(20),
        };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);

        // Resolve the diagram-surface brushes from the style (not the app theme).
        _lineBrush = new SolidColorBrush(Color.Parse(_style.LineColor));
        _textBrush = new SolidColorBrush(Color.Parse(_style.TextColor));
        _bgBrush   = new SolidColorBrush(Color.Parse(_style.BackgroundColor));

        _hostBorder = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Top,
            MinWidth            = 360,
            Background          = _bgBrush,
            Padding             = new(8),
        };
        scroll.Content = _hostBorder;

        Rebuild();
        RefreshDecor();
    }

    Grid? _root;
    Control? _decor;

    // Rebuilds the title/watermark/logo overlay over the canvas from the current diagram style.
    void RefreshDecor()
    {
        if (_root is null) return;
        if (_decor is not null) _root.Children.Remove(_decor);
        _decor = DiagramDecor.Build(_data.Title, _style, () => _ = OpenDecor());
        Grid.SetRow(_decor, 1);
        _root.Children.Add(_decor);
    }

    // Opens the decoration dialog (title / watermark / logo) and re-applies on OK.
    async Task OpenDecor()
    {
        var newTitle = await DiagramDecorDialog.Show(this, _data.Title, _style);
        if (newTitle is null) return;
        _data.Title = newTitle;
        Save();
        RefreshDecor();
        Title = string.Format(Loc.S("Struct_Title"), string.IsNullOrEmpty(newTitle) ? Loc.S("Common_Untitled") : newTitle);
    }

    // Re-renders the whole tree from the model — cheap enough to do on every change.
    void Rebuild()
    {
        if (_hostBorder is null) return;
        _hostBorder.Child = RenderSequence(_data.Root, isRoot: true);
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    // Stacks a sequence of blocks vertically; the root gets a trailing ＋ add affordance.
    Control RenderSequence(List<NsBlock> seq, bool isRoot = false)
    {
        var sp = new StackPanel();
        if (seq.Count == 0)
        {
            sp.Children.Add(EmptyPlaceholder(seq));
            return sp;
        }
        foreach (var b in seq)
            sp.Children.Add(RenderBlock(b, seq));
        if (isRoot) sp.Children.Add(AddRowButton(seq));
        return sp;
    }

    // Wraps one block in its bordered cell, picking the right shape and wiring right-click editing.
    Control RenderBlock(NsBlock b, List<NsBlock> parent)
    {
        Control inner = b.Kind switch
        {
            NsBlockKind.Statement => StatementBox(b),
            NsBlockKind.If        => IfBox(b),
            NsBlockKind.While     => LoopBox(b, preTest: true),
            NsBlockKind.DoWhile   => LoopBox(b, preTest: false),
            NsBlockKind.Case      => CaseBox(b),
            NsBlockKind.Subroutine => SubroutineBox(b),
            _                     => StatementBox(b),
        };

        var cell = new Border
        {
            BorderThickness = new(b.Flagged ? _style.LineThickness * 2 : _style.LineThickness),
            Child           = inner,
        };
        if (b.Flagged)
        {
            // Region the converter could not structure — pulsing amber↔white so it stays visible
            // on ANY background. The flag is a warning affordance, intentionally not part of DiagramStyle.
            cell.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xF5, 0x7F, 0x17));
            ToolTip.SetTip(cell, Loc.S("Struct_FlaggedTip"));
            ApplyFlaggedPulse(cell);
        }
        else
        {
            // Diagram-surface look, with optional per-block overrides on top of the style default.
            cell.BorderBrush = Solid(b.Style?.LineColor) ?? _lineBrush;
            if (b.Style?.LineThickness is double lt) cell.BorderThickness = new(lt);
            // Always give the cell a background (transparent if no fill) so the WHOLE area — including
            // empty space inside the block — is hit-testable for right-click, not just text/borders.
            cell.Background = Solid(b.Style?.FillColor) ?? Brushes.Transparent;
        }

        cell.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(cell).Properties.IsRightButtonPressed)
            {
                ShowBlockMenu(b, parent, cell);
                e.Handled = true;
            }
        };
        return cell;
    }

    /// <summary>Stepwise amber↔white pulse (border + glow) so a flagged block stays visible on any
    /// background. Avalonia-friendly DispatcherTimer toggle, replacing WPF's tweened DropShadowEffect.</summary>
    static void ApplyFlaggedPulse(Border cell)
    {
        var amber = Color.FromRgb(0xF5, 0x7F, 0x17);
        var white = Color.FromRgb(0xFF, 0xF3, 0xE0);
        var on    = false;

        cell.BorderBrush = new SolidColorBrush(amber);
        cell.BoxShadow   = BoxShadows.Parse("0 0 8 0 #88F57F17");

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        timer.Tick += (_, _) =>
        {
            on = !on;
            cell.BorderBrush = new SolidColorBrush(on ? white : amber);
            cell.BoxShadow   = BoxShadows.Parse(on ? "0 0 18 0 #CCFFF3E0" : "0 0 8 0 #88F57F17");
        };
        timer.Start();
        // Stop ticking once the cell leaves the tree, so a rebuild doesn't leak timers.
        cell.DetachedFromVisualTree += (_, _) => timer.Stop();
    }

    // A single statement line; flagged statements get a warning glyph and stronger styling.
    Control StatementBox(NsBlock b)
    {
        var text = b.Flagged
            ? "⚠ " + b.Text
            : (string.IsNullOrWhiteSpace(b.Text) ? Loc.S("Struct_PhStatement") : b.Text);
        var t = PrimaryLabel(text, b);
        t.Margin = new(8, 6, 8, 6);
        if (b.Flagged)
        {
            t.FontWeight = FontWeight.SemiBold;
            t.Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0x51, 0x00));
        }
        t.DoubleTapped += (_, _) => EditText(b);
        return t;
    }

    // The subroutine (predefined-process) box: a centred name with the two vertical inner bars,
    // double-click opens its linked diagram ("show chart").
    Control SubroutineBox(NsBlock b)
    {
        var text = string.IsNullOrWhiteSpace(b.Text) ? Loc.S("Struct_PhSubroutine") : b.Text;
        var t = PrimaryLabel(text, b);
        t.Margin = new(16, 6, 16, 6);
        t.TextAlignment = TextAlignment.Center;

        var bar   = Solid(b.Style?.LineColor) ?? _lineBrush;
        var left  = new Border { Width = 1, Background = bar, HorizontalAlignment = HorizontalAlignment.Left,  Margin = new(7, 0, 0, 0) };
        var right = new Border { Width = 1, Background = bar, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 0, 7, 0) };

        var g = new Grid { Children = { t, left, right } };
        ToolTip.SetTip(g, Loc.S("Struct_ShowChartTip"));
        g.DoubleTapped += (_, _) => ShowChart(b);
        return g;
    }

    // Opens the sub-program's diagram. A subroutine references a real Function in the library: on first
    // use it creates one (named after the block), so it can be reused across plans/boards.
    async void ShowChart(NsBlock b)
    {
        if (string.IsNullOrEmpty(b.RefId))
        {
            var id = await SubroutineLinkDialog.Show(this, _projFolder, "");
            if (string.IsNullOrEmpty(id)) return;
            var fn = CodeEntityService.LoadAll(_projFolder, "Function").FirstOrDefault(x => x.Id == id);
            if (fn is null) return;
            b.RefId = fn.Id; b.Text = fn.Name; Save(); Rebuild();
        }
        var f = CodeEntityService.LoadAll(_projFolder, "Function").FirstOrDefault(x => x.Id == b.RefId);
        _ = DiagramLauncher.ChooseAndOpen(this, _projFolder, b.RefId, f?.Name ?? b.Text, _themePath);
    }

    // The classic if/else box: centered condition, T/F labels, then two side-by-side branches.
    Control IfBox(NsBlock b)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // condition header
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // branch labels
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star)); // branches

        var cond = PrimaryLabel(string.IsNullOrWhiteSpace(b.Text) ? Loc.S("Struct_PhCondition") : b.Text, b);
        cond.TextAlignment = TextAlignment.Center;
        cond.Margin = new(8, 6, 8, 6);
        cond.DoubleTapped += (_, _) => EditText(b);
        Grid.SetRow(cond, 0);
        grid.Children.Add(cond);

        // Branch label row (true | false).
        var labelRow = new Grid();
        labelRow.ColumnDefinitions.Add(new ColumnDefinition());
        labelRow.ColumnDefinitions.Add(new ColumnDefinition());
        var tl = LabelText(Loc.S("Struct_True"));  tl.FontSize = 10; tl.Opacity = 0.7; tl.HorizontalAlignment = HorizontalAlignment.Center;
        var fl = LabelText(Loc.S("Struct_False")); fl.FontSize = 10; fl.Opacity = 0.7; fl.HorizontalAlignment = HorizontalAlignment.Center; Grid.SetColumn(fl, 1);
        labelRow.Children.Add(tl); labelRow.Children.Add(fl);
        var labelWrap = TopBorder(labelRow); Grid.SetRow(labelWrap, 1);
        grid.Children.Add(labelWrap);

        // Two branch columns.
        var cols = new Grid();
        cols.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        cols.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var thenCol = RenderSequence(b.Body);
        var elseCol = RenderSequence(b.Else);
        Grid.SetColumn(thenCol, 0);
        var elseWrap = LeftBorder(elseCol); Grid.SetColumn(elseWrap, 1);
        cols.Children.Add(thenCol);
        cols.Children.Add(elseWrap);
        var colsWrap = TopBorder(cols); Grid.SetRow(colsWrap, 2);
        grid.Children.Add(colsWrap);

        return grid;
    }

    // A loop box: condition above (pre-test/while) or below (post-test/do-while), body inset by a bracket.
    Control LoopBox(NsBlock b, bool preTest)
    {
        var outer = new StackPanel();
        var cond = PrimaryLabel(string.IsNullOrWhiteSpace(b.Text)
            ? (preTest ? Loc.S("Struct_PhWhile") : Loc.S("Struct_PhDoWhile"))
            : b.Text, b);
        cond.Margin = new(8, 5, 8, 5);
        cond.FontStyle = FontStyle.Italic;
        cond.DoubleTapped += (_, _) => EditText(b);

        var t = _style.LineThickness;
        var bodyWrap = new Border
        {
            Child           = RenderSequence(b.Body),
            Margin          = new(14, 0, 0, 0),       // inset = loop bracket
            BorderThickness = new(t, t, 0, t),
            BorderBrush     = _lineBrush,
        };

        if (preTest) { outer.Children.Add(cond); outer.Children.Add(bodyWrap); }
        else         { outer.Children.Add(bodyWrap); outer.Children.Add(TopBorder(cond)); }
        return outer;
    }

    // A multi-way case box: a selector header over N equal columns, each with a label and a body.
    Control CaseBox(NsBlock b)
    {
        var outer = new StackPanel();
        var head = PrimaryLabel(string.IsNullOrWhiteSpace(b.Text) ? Loc.S("Struct_PhSelector") : b.Text, b);
        head.TextAlignment = TextAlignment.Center;
        head.Margin = new(8, 5, 8, 5);
        head.DoubleTapped += (_, _) => EditText(b);
        outer.Children.Add(head);

        var cols = new Grid();
        if (b.Arms.Count == 0) b.Arms.Add(new NsArm());
        for (int i = 0; i < b.Arms.Count; i++)
            cols.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        for (int i = 0; i < b.Arms.Count; i++)
        {
            var arm = b.Arms[i];
            var col = new StackPanel();
            var lbl = LabelText(string.IsNullOrWhiteSpace(arm.Label) ? Loc.S("Struct_Case") : arm.Label);
            lbl.FontSize = 10; lbl.Opacity = 0.8; lbl.TextAlignment = TextAlignment.Center; lbl.Margin = new(4, 3, 4, 3);
            var capArm = arm;
            lbl.DoubleTapped += (_, _) => EditArmLabel(capArm);
            col.Children.Add(lbl);
            col.Children.Add(TopBorder(RenderSequence(arm.Body)));

            var colWrap = i == 0 ? (Control)col : LeftBorder(col);
            Grid.SetColumn(colWrap, i);
            cols.Children.Add(colWrap);
        }
        outer.Children.Add(TopBorder(cols));
        return outer;
    }

    // ── Editing ──────────────────────────────────────────────────────────────

    // The right-click menu for a block: edit, insert above/below, add into containers, delete.
    void ShowBlockMenu(NsBlock b, List<NsBlock> parent, Control anchor)
    {
        var cm = new ContextMenu();

        var edit = new MenuItem { Header = Loc.S("Flow_EditText") };
        edit.Click += (_, _) => EditText(b);
        cm.Items.Add(edit);

        if (b.Kind == NsBlockKind.Subroutine)
        {
            var chart = new MenuItem { Header = Loc.S("Struct_ShowChart") };
            chart.Click += (_, _) => ShowChart(b);
            cm.Items.Add(chart);
        }

        cm.Items.Add(new Separator());
        cm.Items.Add(InsertMenu(Loc.S("Struct_InsertAbove"), parent, parent.IndexOf(b)));
        cm.Items.Add(InsertMenu(Loc.S("Struct_InsertBelow"), parent, parent.IndexOf(b) + 1));

        // Containers can be filled directly.
        if (b.Kind is NsBlockKind.While or NsBlockKind.DoWhile)
            cm.Items.Add(InsertMenu(Loc.S("Struct_AddLoopBody"), b.Body, b.Body.Count));
        if (b.Kind == NsBlockKind.If)
        {
            cm.Items.Add(InsertMenu(Loc.S("Struct_AddTrue"), b.Body, b.Body.Count));
            cm.Items.Add(InsertMenu(Loc.S("Struct_AddFalse"), b.Else, b.Else.Count));
        }
        if (b.Kind == NsBlockKind.Case)
        {
            var addArm = new MenuItem { Header = Loc.S("Struct_AddArm") };
            addArm.Click += (_, _) => { b.Arms.Add(new NsArm()); Save(); Rebuild(); };
            cm.Items.Add(addArm);
        }

        cm.Items.Add(new Separator());
        var style = new MenuItem { Header = Loc.S("Style_Open") };
        style.Click += async (_, _) =>
        {
            var edited = await StyleEditorWindow.Edit(this, b.Style ?? new ElementStyle());
            if (edited is null) return;
            b.Style = IsEmptyStyle(edited) ? null : edited;   // all-inherit → drop the override entirely
            Save(); Rebuild();
        };
        cm.Items.Add(style);

        cm.Items.Add(new Separator());
        var del = new MenuItem { Header = Loc.S("Struct_DeleteBlock") };
        del.Click += async (_, _) =>
        {
            var wasSub = b.Kind == NsBlockKind.Subroutine && !string.IsNullOrEmpty(b.RefId);
            parent.Remove(b); Save(); Rebuild();
            if (wasSub) await InfoDialog.Show(this, "sub_remove", Loc.S("Sub_RemoveInfo"), Loc.S("Struct_KSubroutine"));
        };
        cm.Items.Add(del);

        OpenMenu(cm, anchor);
    }

    // True when a style carries no overrides at all (every field inherits) — used to drop empty styles.
    static bool IsEmptyStyle(ElementStyle s) =>
        s.LineColor is null && s.FillColor is null && s.TextColor is null && s.LineThickness is null;

    // Builds a submenu offering the five block kinds, each inserting at the given index.
    MenuItem InsertMenu(string header, List<NsBlock> seq, int index)
    {
        var mi = new MenuItem { Header = header };
        void Add(string label, NsBlockKind kind)
        {
            var sub = new MenuItem { Header = label };
            sub.Click += (_, _) =>
            {
                seq.Insert(Math.Clamp(index, 0, seq.Count), new NsBlock { Kind = kind, Text = DefaultText(kind) });
                Save(); Rebuild();
            };
            mi.Items.Add(sub);
        }
        Add(Loc.S("Struct_KStatement"), NsBlockKind.Statement);
        Add(Loc.S("Struct_KIf"), NsBlockKind.If);
        Add(Loc.S("Struct_KWhile"), NsBlockKind.While);
        Add(Loc.S("Struct_KDoWhile"), NsBlockKind.DoWhile);
        Add(Loc.S("Struct_KCase"), NsBlockKind.Case);
        Add(Loc.S("Struct_KSubroutine"), NsBlockKind.Subroutine);
        return mi;
    }

    // The trailing ＋ button on the root: a flat kind-picker that appends a new block.
    Button AddRowButton(List<NsBlock> seq) => KindPicker("＋", seq, Loc.S("Struct_AddBlockTip"), stretch: false);

    // The placeholder shown for an empty sequence: a full-width kind-picker that adds the first block.
    Control EmptyPlaceholder(List<NsBlock> seq) => KindPicker(Loc.S("Struct_AddInline"), seq, null, stretch: true);

    // Shared kind-picker button: on click, pops a flat menu of the five block kinds.
    Button KindPicker(string label, List<NsBlock> seq, string? tip, bool stretch)
    {
        var b = Ui.Btn(label, tip);
        b.FontSize = stretch ? 11 : 14;
        b.Margin   = stretch ? new(0) : new(0, 4, 0, 0);
        b.Cursor   = new Cursor(StandardCursorType.Hand);
        b.HorizontalAlignment = stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        if (stretch) b.Opacity = 0.7;
        Ui.Theme(b, TemplatedControl.BackgroundProperty, "ControlBgBrush");
        Ui.Theme(b, TemplatedControl.ForegroundProperty, "SidebarTextBrush");

        b.Click += (_, _) =>
        {
            var cm = new ContextMenu();
            void Add(string l, NsBlockKind kind)
            {
                var mi = new MenuItem { Header = l };
                mi.Click += (_, _) => { seq.Add(new NsBlock { Kind = kind, Text = DefaultText(kind) }); Save(); Rebuild(); };
                cm.Items.Add(mi);
            }
            Add(Loc.S("Struct_KStatement"), NsBlockKind.Statement);
            Add(Loc.S("Struct_KIf"), NsBlockKind.If);
            Add(Loc.S("Struct_KWhile"), NsBlockKind.While);
            Add(Loc.S("Struct_KDoWhile"), NsBlockKind.DoWhile);
            Add(Loc.S("Struct_KCase"), NsBlockKind.Case);
            Add(Loc.S("Struct_KSubroutine"), NsBlockKind.Subroutine);
            OpenMenu(cm, b);
        };
        return b;
    }

    // Prompts for a block's text/condition and saves the edit.
    async void EditText(NsBlock b)
    {
        var t = await PromptDialog.Show(this,
            b.Kind == NsBlockKind.Statement ? Loc.S("Struct_PromptStatement") : Loc.S("Struct_PromptCondition"),
            b.Text);
        if (t is null) return;
        b.Text = t; Save(); Rebuild();
    }

    // Prompts for a case arm's label and saves the edit.
    async void EditArmLabel(NsArm arm)
    {
        var t = await PromptDialog.Show(this, Loc.S("Struct_PromptCaseLabel"), arm.Label);
        if (t is null) return;
        arm.Label = t; Save(); Rebuild();
    }

    // The starter text a freshly-inserted block of each kind gets.
    static string DefaultText(NsBlockKind k) => k switch
    {
        NsBlockKind.If      => Loc.S("Struct_DefCondition"),
        NsBlockKind.While   => Loc.S("Struct_DefWhile"),
        NsBlockKind.DoWhile => Loc.S("Struct_DefDoWhile"),
        NsBlockKind.Case    => Loc.S("Struct_DefSelector"),
        NsBlockKind.Subroutine => Loc.S("Struct_DefSubroutine"),
        _                   => Loc.S("Struct_DefStatement"),
    };

    // ── Visual helpers ───────────────────────────────────────────────────────

    // Builds a block's primary label and applies its optional per-block text-colour override.
    TextBlock PrimaryLabel(string text, NsBlock b)
    {
        var t = LabelText(text);
        if (Solid(b.Style?.TextColor) is { } tb) t.Foreground = tb;
        return t;
    }

    // Turns an optional web-hex string into a brush, or null when unset/unparseable (→ caller inherits).
    static IBrush? Solid(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return new SolidColorBrush(Color.Parse(hex)); } catch { return null; }
    }

    // A wrapping label drawn in the diagram's own text colour/font — the workhorse of every box.
    TextBlock LabelText(string text)
    {
        return new TextBlock
        {
            Text              = text,
            TextWrapping      = TextWrapping.Wrap,
            FontSize          = _style.FontSize,
            FontFamily        = new FontFamily(_style.FontFamily),
            Foreground        = _textBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    // Draws a top divider line above the child — how NS boxes separate stacked regions.
    Border TopBorder(Control child) =>
        new() { Child = child, BorderThickness = new(0, _style.LineThickness, 0, 0), BorderBrush = _lineBrush };

    // Draws a left divider line beside the child — how NS boxes separate side-by-side columns.
    Border LeftBorder(Control child) =>
        new() { Child = child, BorderThickness = new(_style.LineThickness, 0, 0, 0), BorderBrush = _lineBrush };
}
