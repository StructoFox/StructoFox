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

    // Loads (or starts) the structogram for one function/method and builds the editor surface.
    public StructogramWindow(string projFolder, string key, string title, string? themePath)
    {
        _projFolder = projFolder;
        _key        = key;
        _themePath  = themePath;
        _data       = StructogramService.Load(projFolder, key);
        if (string.IsNullOrEmpty(_data.Title)) _data.Title = title;

        Title                 = string.Format(Loc.S("Struct_Title"),
                                    string.IsNullOrEmpty(title) ? Loc.S("Common_Untitled") : title);
        Width                 = 760;
        Height                = 620;
        MinWidth              = 420;
        MinHeight             = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        if (!string.IsNullOrWhiteSpace(themePath))
            try { Resources.MergedDictionaries.Add(OxsuitLoader.Load(themePath)); } catch { /* unthemed is fine */ }
        Ui.Theme(this, BackgroundProperty, "ContentBgBrush");

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

        // Toolbar with a usage hint.
        var bar = new Border { Padding = new(12, 8, 12, 8) };
        Ui.Theme(bar, Border.BackgroundProperty, "SidebarBgBrush");
        Grid.SetRow(bar, 0); root.Children.Add(bar);

        var hint = new TextBlock
        {
            Text = Loc.S("Struct_Hint"),
            VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Opacity = 0.8,
        };
        Ui.Theme(hint, TextBlock.ForegroundProperty, "SidebarTextBrush");
        bar.Child = hint;

        // Scrollable diagram host — the structogram can grow past the window.
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new(20),
        };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);

        _hostBorder = new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, MinWidth = 360 };
        scroll.Content = _hostBorder;

        Rebuild();
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
            _                     => StatementBox(b),
        };

        var cell = new Border
        {
            BorderThickness = new(b.Flagged ? 2 : 1),
            Child           = inner,
        };
        if (b.Flagged)
        {
            // Region the converter could not structure — pulsing amber↔white so it stays visible
            // on ANY background (including amber themes). See ApplyFlaggedPulse.
            cell.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xF5, 0x7F, 0x17));
            ToolTip.SetTip(cell, Loc.S("Struct_FlaggedTip"));
            ApplyFlaggedPulse(cell);
        }
        else
        {
            Ui.Theme(cell, Border.BorderBrushProperty, "ControlBorderBrush");
            Ui.Theme(cell, Border.BackgroundProperty,  "CardBgBrush");
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
        var t = LabelText(text);
        t.Margin = new(8, 6, 8, 6);
        if (b.Flagged)
        {
            t.FontWeight = FontWeight.SemiBold;
            t.Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0x51, 0x00));
        }
        t.DoubleTapped += (_, _) => EditText(b);
        return t;
    }

    // The classic if/else box: centered condition, T/F labels, then two side-by-side branches.
    Control IfBox(NsBlock b)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // condition header
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // branch labels
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star)); // branches

        var cond = LabelText(string.IsNullOrWhiteSpace(b.Text) ? Loc.S("Struct_PhCondition") : b.Text);
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
        var cond = LabelText(string.IsNullOrWhiteSpace(b.Text)
            ? (preTest ? Loc.S("Struct_PhWhile") : Loc.S("Struct_PhDoWhile"))
            : b.Text);
        cond.Margin = new(8, 5, 8, 5);
        cond.FontStyle = FontStyle.Italic;
        cond.DoubleTapped += (_, _) => EditText(b);

        var bodyWrap = new Border
        {
            Child           = RenderSequence(b.Body),
            Margin          = new(14, 0, 0, 0),       // inset = loop bracket
            BorderThickness = new(1, 1, 0, 1),
        };
        Ui.Theme(bodyWrap, Border.BorderBrushProperty, "ControlBorderBrush");

        if (preTest) { outer.Children.Add(cond); outer.Children.Add(bodyWrap); }
        else         { outer.Children.Add(bodyWrap); outer.Children.Add(TopBorder(cond)); }
        return outer;
    }

    // A multi-way case box: a selector header over N equal columns, each with a label and a body.
    Control CaseBox(NsBlock b)
    {
        var outer = new StackPanel();
        var head = LabelText(string.IsNullOrWhiteSpace(b.Text) ? Loc.S("Struct_PhSelector") : b.Text);
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
        var del = new MenuItem { Header = Loc.S("Struct_DeleteBlock") };
        del.Click += (_, _) => { parent.Remove(b); Save(); Rebuild(); };
        cm.Items.Add(del);

        cm.Open(anchor);
    }

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
            cm.Open(b);
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
        _                   => Loc.S("Struct_DefStatement"),
    };

    // ── Visual helpers ───────────────────────────────────────────────────────

    // A themed, wrapping label — the workhorse text element of every box.
    TextBlock LabelText(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Ui.Theme(t, TextBlock.ForegroundProperty, "SidebarTextBrush");
        return t;
    }

    // Draws a top divider line above the child — how NS boxes separate stacked regions.
    Border TopBorder(Control child)
    {
        var b = new Border { Child = child, BorderThickness = new(0, 1, 0, 0) };
        Ui.Theme(b, Border.BorderBrushProperty, "ControlBorderBrush");
        return b;
    }

    // Draws a left divider line beside the child — how NS boxes separate side-by-side columns.
    Border LeftBorder(Control child)
    {
        var b = new Border { Child = child, BorderThickness = new(1, 0, 0, 0) };
        Ui.Theme(b, Border.BorderBrushProperty, "ControlBorderBrush");
        return b;
    }
}
