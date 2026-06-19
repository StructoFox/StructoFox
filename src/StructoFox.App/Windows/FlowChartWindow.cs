using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using OXSUIT.Loaders.Avalonia;
using StructoFox.Core;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// Flowchart (Programmablaufplan) canvas for sketching a function's logic. Classic shapes
/// (start/end, process, decision, I/O, subroutine, note) joined by labelled arrows. Avalonia port.
/// </summary>
public class FlowChartWindow : Window
{
    readonly string  _projFolder;
    readonly string  _key;         // entityId or entityId#methodId
    readonly string? _themePath;
    FlowChartData    _data;

    Canvas?       _canvas;
    ScrollViewer? _scroll;

    readonly Dictionary<string, Border>        _nodeViews = new(); // node id → container
    readonly Dictionary<string, List<Control>> _connViews = new(); // conn id → visuals

    enum EditMode { Select, Connect, Remove }
    EditMode _mode = EditMode.Select;
    bool     ConnectMode => _mode == EditMode.Connect;
    string?  _connectFromId;
    Line?    _rubberBand;
    readonly HashSet<string> _selected = new();
    double   _zoom = 1.0;

    Button? _selectBtn, _connectBtn, _removeBtn;
    ContextMenu? _menu;   // the one open context menu, so a new one closes the old (no stacking)

    // Opens a context menu over an anchor, first closing any menu still showing.
    void OpenMenu(ContextMenu cm, Control anchor) { _menu?.Close(); _menu = cm; cm.Open(anchor); }

    // The diagram surface look (theme-independent), persisted with the diagram.
    readonly DiagramStyle _style;

    // Loads (or starts) the flowchart for one function/method and builds the editor.
    public FlowChartWindow(string projFolder, string key, string title, string? themePath)
    {
        _projFolder = projFolder;
        _key        = key;
        _themePath  = themePath;
        _data       = FlowChartService.Load(projFolder, key);
        if (string.IsNullOrEmpty(_data.Title)) _data.Title = title;
        _style      = _data.Style;   // persisted with the diagram

        Title                 = string.Format(Loc.S("Flow_Title"),
                                    string.IsNullOrEmpty(title) ? Loc.S("Common_Untitled") : title);
        Width                 = 1100;
        Height                = 760;
        MinWidth              = 560;
        MinHeight             = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        if (!string.IsNullOrWhiteSpace(themePath))
            try { Resources.MergedDictionaries.Add(OxsuitLoader.Load(themePath)); } catch { /* unthemed is fine */ }
        Ui.ThemeWindow(this);

        Build();
    }

    // Persists the flowchart after each change.
    void Save() => FlowChartService.Save(_projFolder, _key, _data);

    // ── Build ──────────────────────────────────────────────────────────────

    // Assembles the toolbar + scrollable canvas, wires global interactions, renders the saved graph.
    void Build()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        Content = root;
        _root = root;

        var toolbar = BuildToolbar();
        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(_scroll, 1);
        root.Children.Add(_scroll);

        _canvas = new Canvas
        {
            Width = 3000, Height = 2000, ClipToBounds = false,
            Background = new SolidColorBrush(Color.Parse(_style.BackgroundColor)),  // diagram surface, not app theme
        };
        _scroll.Content = _canvas;

        // Click on empty canvas clears selection (node clicks are handled and don't bubble here).
        _canvas.PointerPressed += (_, _) =>
        {
            _menu?.Close();
            if (ConnectMode) return;
            _selected.Clear(); RefreshSelection();
        };
        // While connecting, the rubber-band follows the pointer.
        _canvas.PointerMoved += (_, e) =>
        {
            if (ConnectMode && _connectFromId is not null && _rubberBand is not null)
            {
                if (NodeCenter(_connectFromId) is { } c) _rubberBand.StartPoint = c;
                _rubberBand.EndPoint = e.GetPosition(_canvas);
            }
        };

        KeyDown += (_, e) => { if (e.Key == Key.Delete) RemoveSelected(); };

        // Ctrl + wheel zooms the canvas (otherwise the scroll viewer scrolls normally).
        _scroll.PointerWheelChanged += (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            _zoom = Math.Clamp(_zoom + (e.Delta.Y > 0 ? 0.1 : -0.1), 0.3, 2.5);
            _canvas!.RenderTransform = new ScaleTransform(_zoom, _zoom);
            e.Handled = true;
        };

        foreach (var n in _data.Nodes) RenderNode(n);
        RenderAllConnections();
        RefreshDecor();
    }

    Grid? _root;
    Control? _decor;

    // Rebuilds the title/watermark/logo overlay over the canvas from the current diagram style.
    void RefreshDecor()
    {
        if (_root is null) return;
        if (_decor is not null) _root.Children.Remove(_decor);
        _decor = DiagramDecor.Build(_data.Title, _style);
        Grid.SetRow(_decor, 1);
        _root.Children.Add(_decor);
    }

    // Builds the toolbar: shape-add buttons, the three modes, the → structogram action and zoom reset.
    Border BuildToolbar()
    {
        var bar = new Border { Padding = new(12, 8, 12, 8) };
        Ui.Theme(bar, Border.BackgroundProperty, "SidebarBgBrush");

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        bar.Child = row;

        void AddShapeBtn(string label, FlowNodeKind kind)
        {
            var b = TBtn(label, string.Format(Loc.S("Flow_AddNodeTip"), kind));
            b.Click += (_, _) => AddNode(kind);
            row.Children.Add(b);
        }

        AddShapeBtn(Loc.S("Flow_Start"), FlowNodeKind.Start);
        AddShapeBtn(Loc.S("Flow_Process"), FlowNodeKind.Process);
        AddShapeBtn(Loc.S("Flow_Decision"), FlowNodeKind.Decision);
        AddShapeBtn(Loc.S("Flow_IO"), FlowNodeKind.InputOutput);
        AddShapeBtn(Loc.S("Flow_Subroutine"), FlowNodeKind.Subroutine);
        AddShapeBtn(Loc.S("Flow_End"), FlowNodeKind.End);
        AddShapeBtn(Loc.S("Flow_Note"), FlowNodeKind.Comment);

        row.Children.Add(new Border { Width = 12 });

        _selectBtn  = TBtn(Loc.S("Flow_Select"), Loc.S("Flow_SelectTip"));
        _connectBtn = TBtn(Loc.S("Flow_Connect"), Loc.S("Flow_ConnectTip"));
        _removeBtn  = TBtn(Loc.S("Flow_Remove"), Loc.S("Flow_RemoveTip"));
        _selectBtn.Click  += (_, _) => SetMode(EditMode.Select);
        _connectBtn.Click += (_, _) => SetMode(EditMode.Connect);
        _removeBtn.Click  += (_, _) => SetMode(EditMode.Remove);
        row.Children.Add(_selectBtn);
        row.Children.Add(_connectBtn);
        row.Children.Add(_removeBtn);
        UpdateModeButtons();

        row.Children.Add(new Border { Width = 12 });
        var toNsBtn = TBtn("▦ → Struktogramm", Loc.S("Flow_ToStructogramTip"));
        toNsBtn.Click += (_, _) => ConvertToStructogram();
        row.Children.Add(toNsBtn);

        row.Children.Add(new Border { Width = 12 });
        var bgBtn = TBtn("🎨", Loc.S("Flow_Background"));
        bgBtn.Click += async (_, _) =>
        {
            var hex = await ColorPickDialog.Pick(this, Loc.S("Flow_Background"), _style.BackgroundColor);
            if (hex is null) return;
            _style.BackgroundColor = hex;
            if (_canvas is not null) _canvas.Background = new SolidColorBrush(Color.Parse(hex));
            Save();
        };
        row.Children.Add(bgBtn);

        var decorBtn = TBtn(Loc.S("Decor_Open"), Loc.S("Decor_OpenTip"));
        decorBtn.Click += async (_, _) =>
        {
            var newTitle = await DiagramDecorDialog.Show(this, _data.Title, _style);
            if (newTitle is null) return;
            _data.Title = newTitle;
            Save();
            RefreshDecor();
            Title = string.Format(Loc.S("Flow_Title"), string.IsNullOrEmpty(newTitle) ? Loc.S("Common_Untitled") : newTitle);
        };
        row.Children.Add(decorBtn);

        var zoomBtn = TBtn("1:1", Loc.S("Common_ResetZoomTip"));
        zoomBtn.Click += (_, _) => { _zoom = 1.0; if (_canvas is not null) _canvas.RenderTransform = null; };
        row.Children.Add(zoomBtn);

        return bar;
    }

    // Converts the flowchart to a structogram (deterministically) and opens it, warning if partial.
    async void ConvertToStructogram()
    {
        if (StructogramService.Exists(_projFolder, _key))
        {
            var res = await MessageDialog.Show(this,
                Loc.S("Flow_ToStructogramOverwrite"), Loc.S("Flow_ToStructogramTitle"), DialogButtons.YesNo);
            if (res != DialogResult.Yes) return;
        }

        var title = string.IsNullOrEmpty(_data.Title) ? Loc.S("Common_Untitled") : _data.Title;
        var sd = StructogramConverter.Convert(_data, title);
        StructogramService.Save(_projFolder, _key, sd);

        if (AnyFlagged(sd.Root))
            await MessageDialog.Show(this, Loc.S("Flow_ToStructogramPartial"), Loc.S("Flow_ToStructogramTitle"));

        new StructogramWindow(_projFolder, _key, title, _themePath).Show();
    }

    // Recursively reports whether any block in the tree was flagged as unstructurable.
    static bool AnyFlagged(List<NsBlock> blocks)
    {
        foreach (var b in blocks)
        {
            if (b.Flagged) return true;
            if (AnyFlagged(b.Body) || AnyFlagged(b.Else)) return true;
            foreach (var arm in b.Arms) if (AnyFlagged(arm.Body)) return true;
        }
        return false;
    }

    // Switches edit mode, cancelling any in-progress connection and clearing selection outside Select.
    void SetMode(EditMode mode)
    {
        _mode = mode;
        _connectFromId = null;
        RemoveRubberBand();
        if (mode != EditMode.Select) { _selected.Clear(); RefreshSelection(); }
        UpdateModeButtons();
    }

    // Highlights the active mode button and sets the canvas cursor (a "no" cursor in remove mode).
    void UpdateModeButtons()
    {
        void Style(Button? b, bool active)
        {
            if (b is null) return;
            b.FontWeight = active ? FontWeight.Bold : FontWeight.Normal;
            // Pair each background with its intended text colour: AccentText sits on AccentBg,
            // SidebarText on ControlBg. (Mismatching them made the active button unreadable on dark themes.)
            Ui.Theme(b, TemplatedControl.BackgroundProperty, active ? "AccentBgBrush"  : "ControlBgBrush");
            Ui.Theme(b, TemplatedControl.ForegroundProperty, active ? "AccentTextBrush" : "SidebarTextBrush");
        }
        Style(_selectBtn,  _mode == EditMode.Select);
        Style(_connectBtn, _mode == EditMode.Connect);
        Style(_removeBtn,  _mode == EditMode.Remove);
        if (_canvas is not null)
            _canvas.Cursor = new Cursor(_mode == EditMode.Remove ? StandardCursorType.No : StandardCursorType.Arrow);
    }

    // ── Node creation / rendering ──────────────────────────────────────────

    // Appends a new node of the given kind at a cascading offset, saves and renders it.
    void AddNode(FlowNodeKind kind)
    {
        var node = new FlowNode
        {
            Kind   = kind,
            Text   = DefaultText(kind),
            X      = 80 + _data.Nodes.Count % 6 * 30,
            Y      = 80 + _data.Nodes.Count % 6 * 30,
            Width  = kind == FlowNodeKind.Decision ? 150 : 140,
            Height = kind is FlowNodeKind.Start or FlowNodeKind.End ? 46 : 56,
        };
        _data.Nodes.Add(node);
        Save();
        RenderNode(node);
    }

    // The starter text a freshly-added node of each kind gets.
    static string DefaultText(FlowNodeKind k) => k switch
    {
        FlowNodeKind.Start       => Loc.S("Flow_DefStart"),
        FlowNodeKind.End         => Loc.S("Flow_DefEnd"),
        FlowNodeKind.Decision    => Loc.S("Flow_DefDecision"),
        FlowNodeKind.InputOutput => Loc.S("Flow_DefIO"),
        FlowNodeKind.Subroutine  => Loc.S("Flow_DefCall"),
        FlowNodeKind.Comment     => Loc.S("Flow_DefNote"),
        _                        => Loc.S("Flow_DefStep"),
    };

    // Builds one node's shape + label inside a draggable, selectable container on the canvas.
    void RenderNode(FlowNode node)
    {
        if (_nodeViews.ContainsKey(node.Id)) return;

        var (fill, stroke) = NodeColors(node.Kind);
        // Per-node overrides are opt-in; the kind's standard colours stay the default when unset.
        if (ParseOpt(node.Style?.FillColor) is { } fc) fill = fc;
        if (ParseOpt(node.Style?.LineColor) is { } lc) stroke = lc;
        var textColor = ParseOpt(node.Style?.TextColor) ?? stroke;
        var inner = new Grid();

        Control shape = node.Kind switch
        {
            FlowNodeKind.Start or FlowNodeKind.End => RoundedBox(node.Height / 2, fill, stroke),
            FlowNodeKind.Decision    => DiamondShape(node.Width, node.Height, fill, stroke),
            FlowNodeKind.InputOutput => ParallelogramShape(node.Width, node.Height, fill, stroke),
            FlowNodeKind.Subroutine  => SubroutineShape(fill, stroke),
            FlowNodeKind.Comment     => CommentShape(fill, stroke),
            _                        => RoundedBox(4, fill, stroke),
        };
        inner.Children.Add(shape);

        var label = new TextBlock
        {
            TextWrapping        = TextWrapping.Wrap,
            TextAlignment       = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Margin              = new(6, 2, 6, 2),
            Foreground          = new SolidColorBrush(textColor),
        };
        ApplyTextFormat(label, node);   // text + font/size/style/decorations (multiline-aware)
        inner.Children.Add(label);

        // Transparent container = the draggable/selectable hit area; carries the selection glow.
        var container = new Border
        {
            Width = node.Width, Height = node.Height,
            Background = Brushes.Transparent,
            Child = inner,
            Cursor = new Cursor(StandardCursorType.SizeAll),
        };

        Canvas.SetLeft(container, node.X);
        Canvas.SetTop(container, node.Y);
        container.ZIndex = 2;
        _canvas!.Children.Add(container);
        _nodeViews[node.Id] = container;
        GrowCanvasFor(node.X, node.Y, node.Width, node.Height);

        WireNode(container, node, label);
    }

    // Wires a node container's pointer interactions: drag-move, mode actions, double-click + right-click.
    void WireNode(Border container, FlowNode node, TextBlock label)
    {
        var dragging = false;
        var offset   = default(Point);

        container.PointerPressed += (_, e) =>
        {
            var props = e.GetCurrentPoint(container).Properties;
            if (props.IsRightButtonPressed) { ShowNodeMenu(node, label, container); e.Handled = true; return; }
            if (_mode == EditMode.Remove)   { DeleteNode(node.Id); e.Handled = true; return; }
            if (ConnectMode)                { HandleConnectClick(node.Id); e.Handled = true; return; }
            if (e.ClickCount >= 2)          { _ = EditNodeText(node, label); e.Handled = true; return; }

            _selected.Clear(); _selected.Add(node.Id); RefreshSelection();
            dragging = true; offset = e.GetPosition(container);
            e.Pointer.Capture(container);
            e.Handled = true;
        };
        container.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var pt = e.GetPosition(_canvas);
            var nx = Snap(Math.Max(0, pt.X - offset.X));
            var ny = Snap(Math.Max(0, pt.Y - offset.Y));
            Canvas.SetLeft(container, nx); Canvas.SetTop(container, ny);
            node.X = nx; node.Y = ny;
            GrowCanvasFor(nx, ny, node.Width, node.Height);
            UpdateConnectionsFor(node.Id);
            e.Handled = true;
        };
        container.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false; e.Pointer.Capture(null); Save();
            e.Handled = true;
        };
    }

    // The node right-click menu: edit text or delete.
    void ShowNodeMenu(FlowNode node, TextBlock label, Control anchor)
    {
        var cm = new ContextMenu();
        var edit = new MenuItem { Header = Loc.S("Flow_EditText") };
        edit.Click += (_, _) => _ = EditNodeText(node, label);
        cm.Items.Add(edit);
        var style = new MenuItem { Header = Loc.S("Style_Open") };
        style.Click += (_, _) => _ = EditNodeStyle(node);
        cm.Items.Add(style);
        cm.Items.Add(new Separator());
        var del = new MenuItem { Header = Loc.S("Flow_DeleteNode") };
        del.Click += (_, _) => { _selected.Clear(); _selected.Add(node.Id); RemoveSelected(); };
        cm.Items.Add(del);
        OpenMenu(cm, anchor);
    }

    // Opens the element-style editor for a node's colour overrides; an all-inherit result clears them
    // (so the node falls back to its standard kind colours). Re-renders the node and saves.
    async Task EditNodeStyle(FlowNode node)
    {
        var edited = await StyleEditorWindow.Edit(this, node.Style ?? new ElementStyle());
        if (edited is null) return;
        node.Style = edited.LineColor is null && edited.FillColor is null && edited.TextColor is null && edited.LineThickness is null ? null : edited;
        if (_nodeViews.TryGetValue(node.Id, out var v)) { _canvas!.Children.Remove(v); _nodeViews.Remove(node.Id); }
        RenderNode(node);
        UpdateConnectionsFor(node.Id);
        Save();
    }

    // Opens the rich node-text editor (text + font/size/style, multiline); re-applies and saves on OK.
    async Task EditNodeText(FlowNode node, TextBlock label)
    {
        if (!await NodeTextDialog.Edit(this, node)) return;
        ApplyTextFormat(label, node);
        Save();
    }

    // Applies a node's text and formatting (font, size, weight, style, decorations) to its label.
    static void ApplyTextFormat(TextBlock label, FlowNode node)
    {
        label.Text       = node.Text;
        label.FontFamily  = node.FontFamily is { } ff ? new FontFamily(ff) : FontFamily.Default;
        label.FontSize   = node.FontSize ?? 11;
        label.FontWeight = node.Bold ? FontWeight.Bold : FontWeight.Normal;
        label.FontStyle  = node.Italic ? FontStyle.Italic : FontStyle.Normal;

        var dec = new TextDecorationCollection();
        if (node.Underline)     dec.Add(new TextDecoration { Location = TextDecorationLocation.Underline });
        if (node.Strikethrough) dec.Add(new TextDecoration { Location = TextDecorationLocation.Strikethrough });
        label.TextDecorations = dec.Count > 0 ? dec : null;
    }

    // ── Connections ────────────────────────────────────────────────────────

    // Two-click connect: first node arms the rubber-band, second node creates the arrow (with optional label).
    async void HandleConnectClick(string nodeId)
    {
        if (_connectFromId is null)
        {
            _connectFromId = nodeId;
            EnsureRubberBand();
            return;
        }
        if (nodeId == _connectFromId) { _connectFromId = null; RemoveRubberBand(); return; }

        var conn = new FlowConnection { FromId = _connectFromId, ToId = nodeId };
        _connectFromId = null;
        RemoveRubberBand();

        // Decisions get a branch label (yes/no, etc.).
        if (_data.Nodes.FirstOrDefault(n => n.Id == conn.FromId)?.Kind == FlowNodeKind.Decision)
            conn.Label = await PromptDialog.Show(this, Loc.S("Flow_BranchPrompt"), "") ?? "";

        _data.Connections.Add(conn);
        Save();
        RenderConnection(conn);
    }

    // Renders every saved connection (used once after nodes are laid out).
    void RenderAllConnections()
    {
        foreach (var c in _data.Connections) RenderConnection(c);
    }

    // Draws one connection: line, arrowhead, a transparent hit-zone for editing, and an optional label.
    void RenderConnection(FlowConnection conn)
    {
        if (_connViews.TryGetValue(conn.Id, out var old))
            foreach (var v in old) _canvas!.Children.Remove(v);
        _connViews.Remove(conn.Id);

        var a = NodeRect(conn.FromId);
        var b = NodeRect(conn.ToId);
        if (a is null || b is null) return;

        var ca = new Point(a.Value.X + a.Value.Width / 2, a.Value.Y + a.Value.Height / 2);
        var cb = new Point(b.Value.X + b.Value.Width / 2, b.Value.Y + b.Value.Height / 2);
        var p1 = RectBorderPoint(a.Value, cb);
        var p2 = RectBorderPoint(b.Value, ca);

        var brush   = new SolidColorBrush(ParseColor(conn.LineColor));
        var visuals = new List<Control>();

        var line = new Line
        {
            StartPoint = p1, EndPoint = p2,
            Stroke = brush, StrokeThickness = conn.Thickness, IsHitTestVisible = false,
        };
        line.ZIndex = 1;
        _canvas!.Children.Add(line); visuals.Add(line);

        var arrow = BuildArrow(p1, p2, brush);
        arrow.ZIndex = 1;
        _canvas.Children.Add(arrow); visuals.Add(arrow);

        // Fat transparent overlay so the thin arrow is easy to right-click / remove.
        var hit = new Line
        {
            StartPoint = p1, EndPoint = p2,
            Stroke = Brushes.Transparent, StrokeThickness = 12, Cursor = new Cursor(StandardCursorType.Hand),
        };
        hit.ZIndex = 3;
        var capConn = conn;
        hit.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(hit).Properties.IsRightButtonPressed) { ShowConnMenu(capConn, hit); e.Handled = true; }
            else if (_mode == EditMode.Remove) { DeleteConnection(capConn); e.Handled = true; }
        };
        _canvas.Children.Add(hit); visuals.Add(hit);

        if (!string.IsNullOrWhiteSpace(conn.Label))
        {
            var mid = new Point((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);
            var badge = new Border { CornerRadius = new(3), Padding = new(4, 1, 4, 1) };
            Ui.Theme(badge, Border.BackgroundProperty, "SidebarBgBrush");
            var t = new TextBlock { Text = conn.Label, FontSize = 10 };
            Ui.Theme(t, TextBlock.ForegroundProperty, "SidebarTextBrush");
            badge.Child = t;
            Canvas.SetLeft(badge, mid.X - 10);
            Canvas.SetTop(badge, mid.Y - 9);
            badge.ZIndex = 4;
            _canvas.Children.Add(badge); visuals.Add(badge);
        }

        _connViews[conn.Id] = visuals;
    }

    // The arrow right-click menu: relabel or delete.
    void ShowConnMenu(FlowConnection conn, Control anchor)
    {
        var cm = new ContextMenu();
        var relabel = new MenuItem { Header = Loc.S("Flow_EditLabel") };
        relabel.Click += async (_, _) =>
        {
            var t = await PromptDialog.Show(this, Loc.S("Flow_ArrowPrompt"), conn.Label);
            if (t is null) return;
            conn.Label = t; Save(); RenderConnection(conn);
        };
        cm.Items.Add(relabel);
        var flip = new MenuItem { Header = Loc.S("Flow_FlipArrow") };
        flip.Click += (_, _) => { (conn.FromId, conn.ToId) = (conn.ToId, conn.FromId); Save(); RenderConnection(conn); };
        cm.Items.Add(flip);
        var del = new MenuItem { Header = Loc.S("Flow_DeleteArrow") };
        del.Click += (_, _) => DeleteConnection(conn);
        cm.Items.Add(del);
        OpenMenu(cm, anchor);
    }

    // Re-draws all arrows touching a node (called while it is being dragged).
    void UpdateConnectionsFor(string nodeId)
    {
        foreach (var c in _data.Connections)
            if (c.FromId == nodeId || c.ToId == nodeId) RenderConnection(c);
    }

    // Expands the canvas when content nears its right/bottom edge, so there's always room to grow.
    void GrowCanvasFor(double x, double y, double w, double h)
    {
        if (_canvas is null) return;
        const double margin = 400;
        if (x + w + margin > _canvas.Width)  _canvas.Width  = x + w + margin;
        if (y + h + margin > _canvas.Height) _canvas.Height = y + h + margin;
    }

    // ── Remove ─────────────────────────────────────────────────────────────

    // Deletes every selected node (and its arrows), then persists once.
    void RemoveSelected()
    {
        foreach (var id in _selected.ToList()) DeleteNode(id, persist: false);
        _selected.Clear();
        Save();
    }

    // Removes a node and any connections attached to it, optionally saving.
    void DeleteNode(string id, bool persist = true)
    {
        _data.Nodes.RemoveAll(n => n.Id == id);
        if (_nodeViews.TryGetValue(id, out var v)) { _canvas!.Children.Remove(v); _nodeViews.Remove(id); }
        foreach (var c in _data.Connections.Where(c => c.FromId == id || c.ToId == id).ToList())
        {
            _data.Connections.Remove(c);
            if (_connViews.TryGetValue(c.Id, out var vs)) foreach (var vv in vs) _canvas!.Children.Remove(vv);
            _connViews.Remove(c.Id);
        }
        if (persist) Save();
    }

    // Removes a single connection and its visuals.
    void DeleteConnection(FlowConnection conn)
    {
        _data.Connections.Remove(conn);
        if (_connViews.TryGetValue(conn.Id, out var vs)) foreach (var v in vs) _canvas!.Children.Remove(v);
        _connViews.Remove(conn.Id);
        Save();
    }

    // Adds a glow to selected nodes and clears it from the rest.
    void RefreshSelection()
    {
        var glow = BoxShadows.Parse("0 0 12 0 #CC2196F3");
        foreach (var (id, v) in _nodeViews)
            v.BoxShadow = _selected.Contains(id) ? glow : default;
    }

    // ── Shape builders ─────────────────────────────────────────────────────

    // A filled, stroked rounded box — used for start/end (pill) and process (slightly rounded) nodes.
    static Border RoundedBox(double radius, Color fill, Color stroke) => new()
    {
        CornerRadius    = new(radius),
        Background      = new SolidColorBrush(fill),
        BorderBrush     = new SolidColorBrush(stroke),
        BorderThickness = new(1.5),
    };

    // The decision diamond, stretched to fill the node bounds.
    static Polygon DiamondShape(double w, double h, Color fill, Color stroke)
    {
        var p = new Polygon { Fill = new SolidColorBrush(fill), Stroke = new SolidColorBrush(stroke), StrokeThickness = 1.5, Stretch = Stretch.Fill };
        p.Points.Add(new(w / 2, 0)); p.Points.Add(new(w, h / 2)); p.Points.Add(new(w / 2, h)); p.Points.Add(new(0, h / 2));
        return p;
    }

    // The input/output parallelogram, stretched to fill the node bounds.
    static Polygon ParallelogramShape(double w, double h, Color fill, Color stroke)
    {
        var p = new Polygon { Fill = new SolidColorBrush(fill), Stroke = new SolidColorBrush(stroke), StrokeThickness = 1.5, Stretch = Stretch.Fill };
        p.Points.Add(new(w * 0.22, 0)); p.Points.Add(new(w, 0)); p.Points.Add(new(w * 0.78, h)); p.Points.Add(new(0, h));
        return p;
    }

    // A dashed-border note box for free comments.
    static Grid CommentShape(Color fill, Color stroke)
    {
        var g = new Grid();
        g.Children.Add(new Rectangle
        {
            RadiusX = 3, RadiusY = 3,
            Fill = new SolidColorBrush(fill), Stroke = new SolidColorBrush(stroke),
            StrokeThickness = 1, StrokeDashArray = new AvaloniaList<double> { 3, 2 },
        });
        return g;
    }

    // The subroutine box: a rounded rectangle with the two vertical "predefined-process" bars.
    static Grid SubroutineShape(Color fill, Color stroke)
    {
        var g = new Grid();
        g.Children.Add(new Border
        {
            CornerRadius = new(3), Background = new SolidColorBrush(fill),
            BorderBrush = new SolidColorBrush(stroke), BorderThickness = new(1.5),
        });
        g.Children.Add(new Border { BorderBrush = new SolidColorBrush(stroke), BorderThickness = new(1, 0, 0, 0), Margin = new(7, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left });
        g.Children.Add(new Border { BorderBrush = new SolidColorBrush(stroke), BorderThickness = new(1, 0, 0, 0), Margin = new(0, 0, 7, 0), HorizontalAlignment = HorizontalAlignment.Right });
        return g;
    }

    // The intrinsic fill/stroke colours per node kind (semantic defaults, independent of the app theme).
    static (Color fill, Color stroke) NodeColors(FlowNodeKind k) => k switch
    {
        FlowNodeKind.Start       => (Color.FromRgb(0xC8, 0xE6, 0xC9), Color.FromRgb(0x2E, 0x7D, 0x32)),
        FlowNodeKind.End         => (Color.FromRgb(0xFF, 0xCD, 0xD2), Color.FromRgb(0xC6, 0x28, 0x28)),
        FlowNodeKind.Decision    => (Color.FromRgb(0xFF, 0xF1, 0xC4), Color.FromRgb(0xF5, 0x7F, 0x17)),
        FlowNodeKind.InputOutput => (Color.FromRgb(0xBB, 0xDE, 0xFB), Color.FromRgb(0x15, 0x65, 0xC0)),
        FlowNodeKind.Subroutine  => (Color.FromRgb(0xD1, 0xC4, 0xE9), Color.FromRgb(0x51, 0x2D, 0xA8)),
        FlowNodeKind.Comment     => (Color.FromRgb(0xEC, 0xEF, 0xF1), Color.FromRgb(0x45, 0x5A, 0x64)),
        _                        => (Color.FromRgb(0xE3, 0xF2, 0xFD), Color.FromRgb(0x15, 0x65, 0xC0)),
    };

    // ── Geometry helpers ───────────────────────────────────────────────────

    // The model rectangle for a node id, or null if it's gone.
    Rect? NodeRect(string id)
    {
        var n = _data.Nodes.FirstOrDefault(x => x.Id == id);
        return n is null ? null : new Rect(n.X, n.Y, n.Width, n.Height);
    }

    // The centre point of a node, or null if it's gone.
    Point? NodeCenter(string id)
    {
        var r = NodeRect(id);
        return r is null ? null : new Point(r.Value.X + r.Value.Width / 2, r.Value.Y + r.Value.Height / 2);
    }

    // The point on a rectangle's border in the direction of a target — where a connector should meet it.
    static Point RectBorderPoint(Rect rect, Point toward)
    {
        var c = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        double dx = toward.X - c.X, dy = toward.Y - c.Y;
        if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001) return c;
        double hw = rect.Width / 2, hh = rect.Height / 2;
        double scale = 1.0 / Math.Max(Math.Abs(dx) / hw, Math.Abs(dy) / hh);
        return new Point(c.X + dx * scale, c.Y + dy * scale);
    }

    // Builds a small triangular arrowhead pointing from one point to another.
    static Polygon BuildArrow(Point from, Point to, IBrush brush)
    {
        var poly = new Polygon { Fill = brush, IsHitTestVisible = false };
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.001) return poly;
        double ux = dx / len, uy = dy / len, px = -uy, py = ux;
        const double aw = 5, al = 11;
        poly.Points.Add(to);
        poly.Points.Add(new(to.X - ux * al + px * aw, to.Y - uy * al + py * aw));
        poly.Points.Add(new(to.X - ux * al - px * aw, to.Y - uy * al - py * aw));
        return poly;
    }

    // Parses a connection's hex colour, falling back to grey on anything unparseable.
    static Color ParseColor(string hex)
    {
        try { return Color.Parse(hex); } catch { return Colors.Gray; }
    }

    // Parses an optional override hex colour: null/blank/invalid → null (inherit the standard colour).
    static Color? ParseOpt(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return Color.Parse(hex); } catch { return null; }
    }

    // Snaps a coordinate to the grid when snapping is enabled, else returns it unchanged.
    double Snap(double v) =>
        !_data.SnapToGrid || _data.GridSize < 1 ? v : Math.Round(v / _data.GridSize) * _data.GridSize;

    // ── Rubber band ────────────────────────────────────────────────────────

    // Adds the dashed rubber-band line shown while drawing a connection.
    void EnsureRubberBand()
    {
        if (_rubberBand is not null) return;
        _rubberBand = new Line
        {
            Stroke = Brushes.DodgerBlue, StrokeThickness = 1.5,
            StrokeDashArray = new AvaloniaList<double> { 4, 4 }, IsHitTestVisible = false,
        };
        _rubberBand.ZIndex = 20;
        _canvas!.Children.Add(_rubberBand);
    }

    // Removes the rubber-band line.
    void RemoveRubberBand()
    {
        if (_rubberBand is null) return;
        _canvas?.Children.Remove(_rubberBand);
        _rubberBand = null;
    }

    // ── Small UI helpers ───────────────────────────────────────────────────

    // A compact toolbar button, themed from the app theme like the rest of the window chrome.
    Button TBtn(string label, string? tooltip)
    {
        var b = Ui.Btn(label, tooltip);
        b.Padding  = new(9, 5);
        b.FontSize = 12;
        Ui.Theme(b, TemplatedControl.BackgroundProperty, "ControlBgBrush");
        Ui.Theme(b, TemplatedControl.ForegroundProperty, "SidebarTextBrush");
        return b;
    }
}
