using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
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
    LayoutTransformControl? _zoomHost;   // wraps the canvas so zoom scales the scrollable extent
    ScrollViewer? _scroll;

    readonly Dictionary<string, Border>        _nodeViews = new(); // node id → container
    readonly Dictionary<string, List<Control>> _connViews = new(); // conn id → visuals

    enum EditMode { Select, Connect, Remove, Scale }
    EditMode _mode = EditMode.Select;
    bool     ConnectMode => _mode == EditMode.Connect;
    string?  _connectFromId;
    Line?    _rubberBand;
    readonly HashSet<string> _selected = new();
    double   _zoom = 1.0;
    bool     _liveDrag;    // a node is being dragged → route attached arrows cheaply (no A*) until release
    bool     _culling;     // re-entrancy guard for viewport culling
    bool     _crossoverHops;   // temporary, non-DIN crossover "bridges" at line crossings (per-window, not saved)
    readonly List<Control> _hopVisuals = new();
    readonly Dictionary<string, List<Point>> _connPts = new();   // last rendered polyline per connection
    Point?   _mousePos;    // last pointer position over the canvas (null when outside) — for paste-at-cursor
    Dictionary<string, Point>? _dragStart;   // start positions of all selected nodes during a multi-drag

    Avalonia.Controls.Shapes.Rectangle? _gridRect;   // the tiled grid behind the diagram
    Avalonia.Controls.Shapes.Rectangle? _selRect;    // rubber-band multi-select rectangle
    bool   _selecting;  Point _selStart;             // left-drag selection on empty canvas
    bool   _panning;    Point _panStart;  Vector _panOrigin;   // right-drag canvas pan
    bool   _panMoved;   bool _rightCancelConnect;    // distinguish right-drag (pan) from right-click (cancel)

    FlowConnection? _segConn;  int _segIdx;  List<Point>? _segBasePts;  bool _segHoriz;  Point _segStart;  // segment drag
    string? _segJunctionId;   // if the dragged segment ends at a junction, the junction moves with it
    FlowConnection? _tapDrag;  // a tap line being slid along its target

    Button? _selectBtn, _removeBtn, _scaleBtn;
    readonly List<Control> _scaleHandles = new();   // resize grips shown on nodes while in Scale mode
    // Identifies a resize grip: which node it belongs to and which edges it moves.
    sealed record HandleInfo(string NodeId, bool Left, bool Right, bool Top, bool Bottom);
    const double HandleSize = 9;
    MenuItem? _connMenu;   // the merged "Connect" menu; its header reflects the active line style + mode
    ContextMenu? _menu;       // the one open context menu, so a new one closes the old (no stacking)

    // Opens a context menu over an anchor, first closing any menu still showing.
    void OpenMenu(ContextMenu cm, Control anchor) { _menu?.Close(); _menu = cm; cm.Open(anchor); }

    // The diagram surface look (theme-independent), persisted with the diagram.
    DiagramStyle _style;   // not readonly: undo/redo swaps _data (and thus its Style)

    // Snapshot-based undo/redo of the diagram (JSON of _data), recorded at each Save() boundary.
    readonly List<string> _undo = new();
    readonly List<string> _redo = new();
    string _snapshot = "";

    // Loads (or starts) the flowchart for one function/method and builds the editor.
    public FlowChartWindow(string projFolder, string key, string title, string? themePath)
    {
        _projFolder = projFolder;
        _key        = key;
        _themePath  = themePath;
        _data       = FlowChartService.Load(projFolder, key);
        if (string.IsNullOrEmpty(_data.Title)) _data.Title = title;
        _style      = _data.Style;   // persisted with the diagram
        _snapshot   = Serialize(_data);   // baseline for undo

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
        ThemeManager.FixFluentBrushes(this);   // theme popups (context menus) at window scope

        Build();
    }

    // Persists the flowchart after each change, recording the previous state for undo (one step per
    // discrete action — drags Save only on release, so a whole drag is a single undo step).
    void Save()
    {
        var cur = Serialize(_data);
        if (cur != _snapshot)
        {
            _undo.Add(_snapshot);
            if (_undo.Count > 100) _undo.RemoveAt(0);
            _redo.Clear();
            _snapshot = cur;
        }
        FlowChartService.Save(_projFolder, _key, _data);
    }

    static readonly System.Text.Json.JsonSerializerOptions _undoJson = new() { PropertyNameCaseInsensitive = true };
    static string Serialize(FlowChartData d) => System.Text.Json.JsonSerializer.Serialize(d);

    void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(_snapshot);
        _snapshot = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        ApplySnapshot(_snapshot);
    }

    void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(_snapshot);
        _snapshot = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        ApplySnapshot(_snapshot);
    }

    // Restores a serialized diagram state, persists it and rebuilds the canvas.
    void ApplySnapshot(string json)
    {
        FlowChartData? d;
        try { d = System.Text.Json.JsonSerializer.Deserialize<FlowChartData>(json, _undoJson); } catch { return; }
        if (d is null) return;
        _data = d;
        _style = _data.Style;
        FlowChartService.Save(_projFolder, _key, _data);
        RebuildAll();
    }

    // Clears and re-renders the whole canvas from _data (after an undo/redo).
    void RebuildAll()
    {
        if (_canvas is null) return;
        _canvas.Children.Clear();
        _nodeViews.Clear(); _connViews.Clear();
        _gridRect = null; _selRect = null; _rubberBand = null;
        _selected.Clear(); _connectFromId = null; _segConn = null;
        _canvas.Background = new SolidColorBrush(Color.Parse(_style.BackgroundColor));
        RenderGrid();
        CullToViewport();   // realize only what's visible
        RefreshDecor();
        RefreshConnHeader();
        if (Title is not null) Title = string.Format(Loc.S("Flow_Title"), string.IsNullOrEmpty(_data.Title) ? Loc.S("Common_Untitled") : _data.Title);
    }

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
        // Wrap the canvas so zoom can use a LayoutTransform (scales the scrollable extent too). Pin it
        // top-left so that when it's zoomed smaller than the viewport it stays at the top-left corner
        // instead of drifting to the right/bottom.
        _zoomHost = new LayoutTransformControl
        {
            Child = _canvas,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Top,
        };
        _scroll.Content = _zoomHost;

        // Empty-canvas interactions (node clicks are handled and don't bubble here):
        //  • right-drag      → pan the canvas (also cancels an in-progress connection on right-click)
        //  • left-drag       → rubber-band multi-select
        //  • plain left-click → clear selection
        _canvas.PointerPressed += (_, e) =>
        {
            _menu?.Close();
            var p = e.GetCurrentPoint(_canvas);
            if (p.Properties.IsRightButtonPressed)
            {
                // Right-drag pans (also while connecting); a plain right-click in connect mode cancels.
                _panning = true; _panMoved = false; _rightCancelConnect = ConnectMode;
                _panStart = e.GetPosition(_scroll); _panOrigin = _scroll!.Offset;
                e.Pointer.Capture(_canvas); e.Handled = true;
                return;
            }
            if (ConnectMode) return;
            _selected.Clear(); RefreshSelection();
            if (_mode == EditMode.Select)   // begin a rubber-band selection
            {
                _selecting = true; _selStart = e.GetPosition(_canvas);
                e.Pointer.Capture(_canvas); e.Handled = true;
            }
        };
        _canvas.PointerExited += (_, _) => _mousePos = null;
        _canvas.PointerMoved += (_, e) =>
        {
            _mousePos = e.GetPosition(_canvas);   // remembered for paste-at-cursor
            // While connecting, the rubber-band follows the pointer.
            if (ConnectMode && _connectFromId is not null && _rubberBand is not null)
            {
                if (NodeCenter(_connectFromId) is { } c) _rubberBand.StartPoint = c;
                _rubberBand.EndPoint = e.GetPosition(_canvas);
                return;
            }
            if (_segConn is not null && _segBasePts is not null) { DragSegment(e.GetPosition(_canvas)); return; }
            if (_tapDrag is not null) { SlideTap(e.GetPosition(_canvas)); return; }
            if (_panning)
            {
                var d = e.GetPosition(_scroll) - _panStart;
                if (!_panMoved && (Math.Abs(d.X) > 4 || Math.Abs(d.Y) > 4)) { _panMoved = true; _canvas!.Cursor = new Cursor(StandardCursorType.SizeAll); }
                _scroll!.Offset = new Vector(_panOrigin.X - d.X, _panOrigin.Y - d.Y);
                return;
            }
            if (_selecting) UpdateSelectRect(e.GetPosition(_canvas));
        };
        _canvas.PointerReleased += (_, e) =>
        {
            if (_tapDrag is not null) { Save(); _tapDrag = null; e.Pointer.Capture(null); return; }
            if (_segConn is not null)
            {
                NormalizeWaypoints(_segConn); RenderConnection(_segConn);
                RenderTapsOnto(_segConn.Id);   // T-pieces on this line settle with it
                RenderCrossovers(); Save();
                _segConn = null; _segBasePts = null; _segJunctionId = null; e.Pointer.Capture(null); return;
            }
            if (_panning)
            {
                _panning = false; _canvas!.Cursor = new Cursor(StandardCursorType.Arrow); e.Pointer.Capture(null);
                if (!_panMoved && _rightCancelConnect) CancelConnect();   // plain right-click cancels the arrow
                return;
            }
            if (_selecting) { _selecting = false; CommitSelectRect(); e.Pointer.Capture(null); }
        };

        KeyDown += (_, e) => HandleKey(e);
        Focusable = true;

        // Accept functions dragged in from the cockpit → they land as subroutine nodes.
        DragDrop.SetAllowDrop(_canvas, true);
        _canvas.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        _canvas.AddHandler(DragDrop.DropEvent, OnDrop);

        // Ctrl + wheel zooms toward the pointer. Handled on the scroll viewer in the TUNNEL phase so it
        // fires before anything under the cursor (nodes, lines) and in every edit mode, and before the
        // viewer's own scroll — while Ctrl is held the wheel always zooms toward the cursor, never scrolls.
        _scroll.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            e.Handled = true;
            ZoomAt(_zoom + (e.Delta.Y > 0 ? 0.1 : -0.1), e.GetPosition(_scroll));
        }, RoutingStrategies.Tunnel);

        RenderGrid();
        RefreshDecor();
        // Realize only what's in (or near) the viewport, and re-cull on scroll/zoom — so a canvas with
        // hundreds/thousands of symbols keeps few live controls.
        _scroll!.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.OffsetProperty || e.Property == ScrollViewer.ViewportProperty) CullToViewport();
        };
        Dispatcher.UIThread.Post(CullToViewport, DispatcherPriority.Loaded);
    }

    // The visible canvas region in content coordinates (accounts for scroll offset and zoom). A huge
    // fallback rect (everything visible) until the scroll viewer has measured.
    Rect VisibleRect()
    {
        if (_scroll is null) return new Rect(0, 0, 1e6, 1e6);
        double z = _zoom <= 0 ? 1 : _zoom;
        var o = _scroll.Offset;
        double w = _scroll.Viewport.Width  > 0 ? _scroll.Viewport.Width  : _scroll.Bounds.Width;
        double h = _scroll.Viewport.Height > 0 ? _scroll.Viewport.Height : _scroll.Bounds.Height;
        if (w <= 0 || h <= 0) return new Rect(0, 0, 1e6, 1e6);
        return new Rect(o.X / z, o.Y / z, w / z, h / z);
    }

    // Realizes the nodes/connections that intersect the viewport (plus a margin) and drops those well
    // outside — keeping the live control count proportional to what's on screen, not to the whole diagram.
    void CullToViewport()
    {
        if (_canvas is null || _culling) return;
        _culling = true;
        try
        {
            RecomputeJunctions();   // derive junction positions from their lines before realizing them
            var vis = VisibleRect().Inflate(400);

            foreach (var n in _data.Nodes)
            {
                bool show = vis.Intersects(new Rect(n.X, n.Y, n.Width, n.Height)) || _selected.Contains(n.Id);
                bool realized = _nodeViews.ContainsKey(n.Id);
                if (show && !realized) RenderNode(n);
                else if (!show && realized) { _canvas.Children.Remove(_nodeViews[n.Id]); _nodeViews.Remove(n.Id); }
            }

            foreach (var c in _data.Connections)
            {
                var a = NodeRect(c.FromId); var b = NodeRect(c.ToId);
                bool realized = _connViews.ContainsKey(c.Id);
                bool show = false;
                if (a is not null && b is not null)
                {
                    var bbox = new Rect(
                        Math.Min(a.Value.X, b.Value.X), Math.Min(a.Value.Y, b.Value.Y),
                        Math.Abs(a.Value.X - b.Value.X) + Math.Max(a.Value.Width, b.Value.Width),
                        Math.Abs(a.Value.Y - b.Value.Y) + Math.Max(a.Value.Height, b.Value.Height));
                    foreach (var w in c.Waypoints) bbox = bbox.Union(new Rect(w.X - 1, w.Y - 1, 2, 2));
                    show = vis.Intersects(bbox);
                }
                if (show && !realized) RenderConnection(c);
                else if (!show && realized) { foreach (var v in _connViews[c.Id]) _canvas.Children.Remove(v); _connViews.Remove(c.Id); }
            }
            if (_crossoverHops) RenderCrossovers();
            RenderTapDots();         // dots where two T-pieces coincide
            RefreshScaleHandles();   // (re)attach grips to whatever nodes are now realized, in Scale mode
        }
        finally { _culling = false; }
    }

    // Keyboard: Delete removes the selection; Ctrl+Z/Y undo/redo; Ctrl+0 resets zoom, Ctrl +/- and
    // Ctrl+Up/Down zoom.
    void HandleKey(KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { RemoveSelected(); return; }
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.C:                                 CopySelection(); e.Handled = true; break;
            case Key.V:                                 PasteClipboard(); e.Handled = true; break;
            case Key.Z when !shift:                     Undo(); e.Handled = true; break;
            case Key.Y or Key.Z:                        Redo(); e.Handled = true; break;   // Ctrl+Y or Ctrl+Shift+Z
            case Key.D0 or Key.NumPad0:                 SetZoom(1.0); e.Handled = true; break;
            case Key.OemPlus or Key.Add or Key.Up:      SetZoom(_zoom + 0.1); e.Handled = true; break;
            case Key.OemMinus or Key.Subtract or Key.Down: SetZoom(_zoom - 0.1); e.Handled = true; break;
        }
    }

    // ── Copy / paste (selected nodes + the connections wholly within the selection) ──────────────────
    sealed class ClipPayload { public List<FlowNode> Nodes { get; set; } = new(); public List<FlowConnection> Connections { get; set; } = new(); }
    static string? _clip;   // shared across flowchart windows (in-process)

    void CopySelection()
    {
        if (_selected.Count == 0) return;
        var payload = new ClipPayload
        {
            Nodes       = _data.Nodes.Where(n => _selected.Contains(n.Id)).ToList(),
            Connections = _data.Connections.Where(c => _selected.Contains(c.FromId) && _selected.Contains(c.ToId)).ToList(),
        };
        _clip = System.Text.Json.JsonSerializer.Serialize(payload);
    }

    void PasteClipboard()
    {
        if (_clip is null) return;
        ClipPayload? p;
        try { p = System.Text.Json.JsonSerializer.Deserialize<ClipPayload>(_clip, _undoJson); } catch { return; }
        if (p is null || p.Nodes.Count == 0) return;

        // Offset the whole group so its top-left lands at the cursor (if inside the canvas), else cascade.
        // The offset is snapped ONCE (a grid-multiple delta), then applied to every node without further
        // per-node snapping — so the group's relative layout is preserved exactly (no one-cell drift).
        double minX = p.Nodes.Min(n => n.X), minY = p.Nodes.Min(n => n.Y);
        double offX, offY;
        if (_mousePos is { } m) { offX = Snap(m.X - minX); offY = Snap(m.Y - minY); }
        else                    { offX = Snap(24); offY = Snap(24); }

        var idMap = new Dictionary<string, string>();
        _selected.Clear();
        foreach (var n in p.Nodes)
        {
            // Only the node gets a fresh id. RefId is deliberately preserved: a copied subroutine node
            // still references the SAME library function (a subroutine is itself just a reference).
            var old = n.Id;
            n.Id = Guid.NewGuid().ToString("N")[..8];
            idMap[old] = n.Id;
            n.X += offX; n.Y += offY;
            _data.Nodes.Add(n);
            _selected.Add(n.Id);
            RenderNode(n);
        }
        foreach (var c in p.Connections)
        {
            if (!idMap.TryGetValue(c.FromId, out var f) || !idMap.TryGetValue(c.ToId, out var t)) continue;
            c.Id = Guid.NewGuid().ToString("N")[..8];
            c.FromId = f; c.ToId = t;
            foreach (var w in c.Waypoints) { w.X += offX; w.Y += offY; }
            _data.Connections.Add(c);
            RenderConnection(c);
        }
        RefreshSelection();
        Save();
    }

    // Applies the current zoom as a LayoutTransform, so the scroll viewer sees the scaled size and can
    // scroll the whole zoomed canvas (a RenderTransform would leave the extent unscaled).
    void ApplyZoom()
    {
        if (_zoomHost is not null)
            _zoomHost.LayoutTransform = Math.Abs(_zoom - 1.0) < 0.001 ? null : new ScaleTransform(_zoom, _zoom);
        UpdateZoomLabel();
        CullToViewport();
    }

    // Keyboard / button zoom: anchor on the viewport centre.
    void SetZoom(double z)
    {
        if (_scroll is null) { _zoom = Math.Clamp(z, 0.3, 2.5); ApplyZoom(); return; }
        ZoomAt(z, new Point(_scroll.Viewport.Width / 2, _scroll.Viewport.Height / 2));
    }

    // Zooms to a clamped level while keeping the content point under the given viewport position fixed.
    void ZoomAt(double z, Point viewportPos)
    {
        z = Math.Clamp(z, 0.3, 2.5);
        if (_scroll is null || _canvas is null || Math.Abs(z - _zoom) < 0.0001) { _zoom = z; ApplyZoom(); return; }

        var off = _scroll.Offset;
        // The unscaled content point currently under the pointer.
        double cx = (off.X + viewportPos.X) / _zoom;
        double cy = (off.Y + viewportPos.Y) / _zoom;

        _zoom = z;
        ApplyZoom();

        // After re-layout, scroll so that same content point sits back under the pointer.
        Dispatcher.UIThread.Post(() =>
        {
            if (_scroll is null) return;
            _scroll.Offset = new Vector(Math.Max(0, cx * _zoom - viewportPos.X), Math.Max(0, cy * _zoom - viewportPos.Y));
        }, DispatcherPriority.Render);
    }

    static string Inv(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    // (Re)draws the alignment grid behind the diagram as a single tiled brush — cheap regardless of size.
    void RenderGrid()
    {
        if (_canvas is null) return;
        if (_gridRect is not null) { _canvas.Children.Remove(_gridRect); _gridRect = null; }
        if (!_style.GridVisible) return;

        double g = Math.Max(4, _data.GridSize);
        var brush = new SolidColorBrush(ParseColor(_style.GridColor), Math.Clamp(_style.GridOpacity, 0, 1));

        Drawing drawing;
        if (_style.GridStyle == GridLineStyle.Dots)
            drawing = new GeometryDrawing { Brush = brush, Geometry = new EllipseGeometry(new Rect(0, 0, 1.6, 1.6)) };
        else
        {
            var pen = new Pen(brush, 1);
            if (_style.GridStyle == GridLineStyle.Dashed) pen.DashStyle = new DashStyle(new double[] { 2, 2 }, 0);
            drawing = new GeometryDrawing { Pen = pen, Geometry = Geometry.Parse($"M0,0 L{Inv(g)},0 M0,0 L0,{Inv(g)}") };
        }

        _gridRect = new Avalonia.Controls.Shapes.Rectangle
        {
            Width = _canvas.Width, Height = _canvas.Height, IsHitTestVisible = false, ZIndex = 0,
            Fill = new DrawingBrush(drawing)
            {
                TileMode = TileMode.Tile, Stretch = Stretch.None,
                DestinationRect = new RelativeRect(0, 0, g, g, RelativeUnit.Absolute),
            },
        };
        Canvas.SetLeft(_gridRect, 0); Canvas.SetTop(_gridRect, 0);
        _canvas.Children.Add(_gridRect);
    }

    // Grows/redraws the rubber-band selection rectangle to the current pointer.
    void UpdateSelectRect(Point cur)
    {
        if (_selRect is null)
        {
            _selRect = new Avalonia.Controls.Shapes.Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(40, 33, 150, 243)),
                Stroke = new SolidColorBrush(Color.FromArgb(160, 33, 150, 243)), StrokeThickness = 1,
                IsHitTestVisible = false, ZIndex = 30,
            };
            _canvas!.Children.Add(_selRect);
        }
        Canvas.SetLeft(_selRect, Math.Min(_selStart.X, cur.X));
        Canvas.SetTop(_selRect,  Math.Min(_selStart.Y, cur.Y));
        _selRect.Width  = Math.Abs(cur.X - _selStart.X);
        _selRect.Height = Math.Abs(cur.Y - _selStart.Y);
    }

    // Selects every node intersecting the rubber-band rectangle, then removes it.
    void CommitSelectRect()
    {
        if (_selRect is null) return;
        var r = new Rect(Canvas.GetLeft(_selRect), Canvas.GetTop(_selRect), _selRect.Width, _selRect.Height);
        _canvas!.Children.Remove(_selRect); _selRect = null;
        if (r.Width < 3 && r.Height < 3) return;   // a click, not a drag
        _selected.Clear();
        foreach (var n in _data.Nodes)
            if (r.Intersects(new Rect(n.X, n.Y, n.Width, n.Height))) _selected.Add(n.Id);
        RefreshSelection();
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
        Title = string.Format(Loc.S("Flow_Title"), string.IsNullOrEmpty(newTitle) ? Loc.S("Common_Untitled") : newTitle);
    }

    // Builds the toolbar: shape-add buttons, the three modes, the → structogram action and zoom reset.
    Border BuildToolbar()
    {
        var bar = new Border { Padding = new(12, 8, 12, 8) };
        Ui.Theme(bar, Border.BackgroundProperty, "SidebarBgBrush");

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        bar.Child = row;

        // Shape categories live in one native Menu: top-level items open their variant list on hover,
        // and hovering a sibling closes the open one and opens it instead (native menu-bar behaviour —
        // unlike a light-dismiss ContextMenu, whose overlay swallowed hover on the other buttons).
        var shapeMenu = new Menu { Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };

        // A top-level category that reveals its variants; hovering opens it (no click needed).
        MenuItem Cat(string label)
        {
            var top = new MenuItem { Header = label + "  ▾" };
            Ui.Theme(top, MenuItem.ForegroundProperty, "SidebarTextBrush");
            top.PointerEntered += (_, _) => top.IsSubMenuOpen = true;
            return top;
        }
        // A single-variant top-level item that just adds its node when chosen.
        MenuItem Act(string label, FlowNodeKind kind)
        {
            var top = new MenuItem { Header = label };
            Ui.Theme(top, MenuItem.ForegroundProperty, "SidebarTextBrush");
            top.Click += (_, _) => AddNode(kind);
            return top;
        }

        var startEnd = Cat(Loc.S("Flow_CatStartEnd"));
        startEnd.Items.Add(MI(Loc.S("Flow_Start"), () => AddNode(FlowNodeKind.Start)));
        startEnd.Items.Add(MI(Loc.S("Flow_End"),   () => AddNode(FlowNodeKind.End)));
        shapeMenu.Items.Add(startEnd);

        var proc = Cat(Loc.S("Flow_CatProcess"));
        proc.Items.Add(MI(Loc.S("Flow_Process"),    () => AddNode(FlowNodeKind.Process)));
        proc.Items.Add(MI(Loc.S("Flow_Subroutine"), () => AddNode(FlowNodeKind.Subroutine)));
        proc.Items.Add(new Separator());
        proc.Items.Add(MI(Loc.S("Flow_SymPreparation"),     () => AddNode(FlowNodeKind.Process, FlowSymbol.Preparation)));
        proc.Items.Add(MI(Loc.S("Flow_SymManualOperation"), () => AddNode(FlowNodeKind.Process, FlowSymbol.ManualOperation)));
        proc.Items.Add(MI(Loc.S("Flow_SymLoopLimit"),       () => AddNode(FlowNodeKind.Process, FlowSymbol.LoopLimit)));
        proc.Items.Add(MI(Loc.S("Flow_SymDelay"),           () => AddNode(FlowNodeKind.Process, FlowSymbol.Delay)));
        proc.Items.Add(MI(Loc.S("Flow_SymParallel"),        () => AddNode(FlowNodeKind.Process, FlowSymbol.Parallel)));
        shapeMenu.Items.Add(proc);

        shapeMenu.Items.Add(Act(Loc.S("Flow_Decision"), FlowNodeKind.Decision));

        var io = Cat(Loc.S("Flow_CatIO"));
        io.Items.Add(MI(Loc.S("Flow_SymAuto"),         () => AddNode(FlowNodeKind.InputOutput)));
        io.Items.Add(new Separator());
        io.Items.Add(MI(Loc.S("Flow_SymDocument"),     () => AddNode(FlowNodeKind.InputOutput, FlowSymbol.Document)));
        io.Items.Add(MI(Loc.S("Flow_SymDisplay"),      () => AddNode(FlowNodeKind.InputOutput, FlowSymbol.Display)));
        io.Items.Add(MI(Loc.S("Flow_SymManualInput"),  () => AddNode(FlowNodeKind.InputOutput, FlowSymbol.ManualInput)));
        io.Items.Add(MI(Loc.S("Flow_SymPunchedCard"),  () => AddNode(FlowNodeKind.InputOutput, FlowSymbol.PunchedCard)));
        io.Items.Add(MI(Loc.S("Flow_SymMagneticTape"), () => AddNode(FlowNodeKind.InputOutput, FlowSymbol.MagneticTape)));
        io.Items.Add(MI(Loc.S("Flow_SymMagneticDisk"), () => AddNode(FlowNodeKind.InputOutput, FlowSymbol.MagneticDisk)));
        io.Items.Add(MI(Loc.S("Flow_SymStoredData"),   () => AddNode(FlowNodeKind.InputOutput, FlowSymbol.StoredData)));
        shapeMenu.Items.Add(io);

        // Extended ISO data/system symbols + cloud — rebuilt on open so it reflects the "extended ISO"
        // option (the whole category is hidden when that's off).
        var data = Cat(Loc.S("Flow_CatData"));
        void FillData()
        {
            data.Items.Clear();
            if (!_data.ExtendedIso) { data.Items.Add(new MenuItem { Header = Loc.S("Flow_ExtOff"), IsEnabled = false }); return; }
            data.Items.Add(MI(Loc.S("Flow_SymSort"),    () => AddNode(FlowNodeKind.InputOutput, FlowSymbol.Sort)));
            data.Items.Add(MI(Loc.S("Flow_SymMerge"),   () => AddNode(FlowNodeKind.InputOutput, FlowSymbol.Merge)));
            data.Items.Add(MI(Loc.S("Flow_SymExtract"), () => AddNode(FlowNodeKind.InputOutput, FlowSymbol.Extract)));
            data.Items.Add(MI(Loc.S("Flow_SymCollate"), () => AddNode(FlowNodeKind.InputOutput, FlowSymbol.Collate)));
            data.Items.Add(MI(Loc.S("Flow_SymCloud"),   () => AddNode(FlowNodeKind.InputOutput, FlowSymbol.CloudStorage)));
        }
        FillData();
        data.SubmenuOpened += (_, _) => FillData();
        shapeMenu.Items.Add(data);

        // The merged Connect menu: drawing mode + connector/junction/off-page + the line style. Its header
        // shows the active arrow style (and a ● when drawing), so "Verbinder"/"Verbinden" are one button.
        // Rebuilt on open so the line style's ✓ stays current.
        _connMenu = new MenuItem();
        Ui.Theme(_connMenu, MenuItem.ForegroundProperty, "SidebarTextBrush");
        _connMenu.PointerEntered += (_, _) => _connMenu!.IsSubMenuOpen = true;
        void FillConn()
        {
            _connMenu!.Items.Clear();
            _connMenu.Items.Add(MI(Loc.S("Flow_Connect"), () => SetMode(EditMode.Connect)));   // start drawing arrows
            _connMenu.Items.Add(new Separator());
            _connMenu.Items.Add(MI(Loc.S("Flow_Connector"),  () => AddNode(FlowNodeKind.Connector)));
            _connMenu.Items.Add(MI(Loc.S("Flow_SymOffPage"), () => AddNode(FlowNodeKind.Connector, FlowSymbol.OffPageConnector)));
            // (No standalone "Junction" item: junctions now form automatically where a line meets another.)
            _connMenu.Items.Add(new Separator());
            // Flow-line routing style (global): DIN orthogonal vs. free diagonal arrows.
            _connMenu.Items.Add(MI((_data.DiagonalLines ? "" : "✓ ") + Loc.S("Flow_ArrowDin"),  () => SetDiagonal(false)));
            _connMenu.Items.Add(MI((_data.DiagonalLines ? "✓ " : "") + Loc.S("Flow_ArrowFree"), () => SetDiagonal(true)));
        }
        FillConn();
        _connMenu.SubmenuOpened += (_, _) => FillConn();
        shapeMenu.Items.Add(_connMenu);
        RefreshConnHeader();

        shapeMenu.Items.Add(Act(Loc.S("Flow_Note"), FlowNodeKind.Comment));

        row.Children.Add(shapeMenu);

        row.Children.Add(new Border { Width = 12 });

        _selectBtn  = TBtn(Loc.S("Flow_Select"), Loc.S("Flow_SelectTip"));
        _scaleBtn   = TBtn(Loc.S("Flow_Scale"),  Loc.S("Flow_ScaleTip"));
        _removeBtn  = TBtn(Loc.S("Flow_Remove"), Loc.S("Flow_RemoveTip"));
        _selectBtn.Click  += (_, _) => SetMode(EditMode.Select);
        _scaleBtn.Click   += (_, _) => SetMode(EditMode.Scale);
        _removeBtn.Click  += (_, _) => SetMode(EditMode.Remove);
        row.Children.Add(_selectBtn);
        row.Children.Add(_scaleBtn);
        row.Children.Add(_removeBtn);
        UpdateModeButtons();

        row.Children.Add(new Border { Width = 12 });
        var toNsBtn = TBtn("▦ → Struktogramm", Loc.S("Flow_ToStructogramTip"));
        toNsBtn.Click += (_, _) => ConvertToStructogram();
        row.Children.Add(toNsBtn);

        row.Children.Add(new Border { Width = 12 });
        // Quick zoom: shows the current level; click for presets + a manual entry.
        _zoomBtn = TBtn("🔍 100%", Loc.S("Flow_ZoomTip"));
        _zoomBtn.Flyout = BuildZoomFlyout();
        row.Children.Add(_zoomBtn);
        UpdateZoomLabel();

        // "Options" gathers the set-once-and-forget surface settings: colours, decoration, zoom reset and
        // the grid (visibility / colour / style / opacity / snap) — rarely touched while actually drawing.
        var viewBtn = TBtn(Loc.S("Flow_View"), Loc.S("Flow_ViewTip"));
        viewBtn.Flyout = BuildViewFlyout();
        row.Children.Add(viewBtn);

        return bar;
    }

    Button? _zoomBtn;

    // The quick-zoom flyout: preset percentages plus a manual entry box.
    Flyout BuildZoomFlyout()
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = 232, Margin = new(4) };
        Ui.Theme(panel, TextElement.ForegroundProperty, "ContentTextBrush");

        var grid = new WrapPanel { MaxWidth = 232 };
        foreach (var p in new[] { 25, 50, 75, 100, 125, 150, 175, 200 })
        {
            var bb = Ui.Btn(p + "%"); bb.Width = 52; bb.Margin = new(0, 0, 4, 4);
            int pp = p; bb.Click += (_, _) => SetZoom(pp / 100.0);
            grid.Children.Add(bb);
        }
        panel.Children.Add(grid);

        var box = new TextBox { PlaceholderText = Loc.S("Flow_ZoomManual"), MinWidth = 120 };
        Ui.Theme(box, TextBox.BackgroundProperty,  "InputBgBrush");
        Ui.Theme(box, TextBox.ForegroundProperty,  "SidebarTextBrush");
        Ui.Theme(box, TextBox.BorderBrushProperty, "ControlBorderBrush");
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            var s = (box.Text ?? "").Replace("%", "").Trim();
            if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pct)
                || double.TryParse(s, out pct))
                SetZoom(pct / 100.0);
        };
        panel.Children.Add(box);

        return new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
    }

    // Reflects the current zoom level on the toolbar button.
    void UpdateZoomLabel()
    {
        if (_zoomBtn is not null) _zoomBtn.Content = "🔍 " + Math.Round(_zoom * 100) + "%";
    }

    // The "View" flyout panel: background colour, decoration, zoom reset and grid controls.
    Flyout BuildViewFlyout()
    {
        var panel = new StackPanel { Spacing = 10, MinWidth = 230, Margin = new(4) };
        Ui.Theme(panel, TextElement.ForegroundProperty, "ContentTextBrush");   // popup is its own root; theme labels

        // Colours — grouped: canvas background + arrow colour.
        panel.Children.Add(new TextBlock { Text = Loc.S("Flow_Colors"), FontWeight = FontWeight.Bold });

        var bg = Ui.Btn(Loc.S("Flow_Background"));
        bg.Click += async (_, _) =>
        {
            var hex = await ColorPickDialog.Pick(this, Loc.S("Flow_Background"), _style.BackgroundColor);
            if (hex is null) return;
            _style.BackgroundColor = hex;
            if (_canvas is not null) _canvas.Background = new SolidColorBrush(Color.Parse(hex));
            Save();
        };
        panel.Children.Add(bg);

        var arrow = Ui.Btn(Loc.S("Flow_ArrowColor"));
        arrow.Click += async (_, _) =>
        {
            var hex = await ColorPickDialog.Pick(this, Loc.S("Flow_ArrowColor"), _style.LineColor);
            if (hex is null) return;
            _style.LineColor = hex;
            foreach (var c in _data.Connections) RenderConnection(c);   // all arrows share this colour
            Save();
        };
        panel.Children.Add(arrow);

        // Decoration sits with the colours (it's about the diagram's look, not zoom).
        var decor = Ui.Btn(Loc.S("Decor_Open"));
        decor.Click += (_, _) => _ = OpenDecor();
        panel.Children.Add(decor);

        panel.Children.Add(new Separator());

        // Zoom reset alone between separators.
        var zoom = Ui.Btn(Loc.S("Common_ResetZoomTip"));
        zoom.Click += (_, _) => SetZoom(1.0);
        panel.Children.Add(zoom);

        // Crop: tighten the canvas to the content, also trimming top/left whitespace.
        var crop = Ui.Btn(Loc.S("View_Crop"));
        ToolTip.SetTip(crop, Loc.S("View_CropTip"));
        crop.Click += (_, _) => { FitCanvas(trim: true); Save(); };
        panel.Children.Add(crop);

        panel.Children.Add(new Separator());
        panel.Children.Add(new TextBlock { Text = Loc.S("Grid_Header"), FontWeight = FontWeight.Bold });

        var show = new CheckBox { Content = Loc.S("Grid_Show"), IsChecked = _style.GridVisible };
        show.IsCheckedChanged += (_, _) => { _style.GridVisible = show.IsChecked == true; RenderGrid(); Save(); };
        panel.Children.Add(show);

        var snap = new CheckBox { Content = Loc.S("Grid_Snap"), IsChecked = _data.SnapToGrid };
        snap.IsCheckedChanged += (_, _) => { _data.SnapToGrid = snap.IsChecked == true; Save(); };
        panel.Children.Add(snap);

        var styleCombo = Ui.Combo();
        styleCombo.Items.Add(new ComboItem(Loc.S("Grid_Lines"),  nameof(GridLineStyle.Lines)));
        styleCombo.Items.Add(new ComboItem(Loc.S("Grid_Dashed"), nameof(GridLineStyle.Dashed)));
        styleCombo.Items.Add(new ComboItem(Loc.S("Grid_Dots"),   nameof(GridLineStyle.Dots)));
        styleCombo.SelectedItem = styleCombo.Items.OfType<ComboItem>().FirstOrDefault(c => c.Id == _style.GridStyle.ToString()) ?? styleCombo.Items[0];
        styleCombo.SelectionChanged += (_, _) =>
        {
            if ((styleCombo.SelectedItem as ComboItem)?.Id is { } id && Enum.TryParse<GridLineStyle>(id, out var gs))
            { _style.GridStyle = gs; RenderGrid(); Save(); }
        };
        panel.Children.Add(styleCombo);

        var color = Ui.Btn(Loc.S("Grid_Color"));
        color.Click += async (_, _) =>
        {
            var hex = await ColorPickDialog.Pick(this, Loc.S("Grid_Color"), _style.GridColor);
            if (hex is null) return;
            _style.GridColor = hex; RenderGrid(); Save();
        };
        panel.Children.Add(color);

        panel.Children.Add(new TextBlock { Text = Loc.S("Grid_Opacity"), FontSize = 11, Opacity = 0.8 });
        var op = new Slider { Minimum = 0, Maximum = 1, Value = _style.GridOpacity, SmallChange = 0.05, LargeChange = 0.1 };
        op.PropertyChanged += (_, ev) => { if (ev.Property == Slider.ValueProperty) { _style.GridOpacity = op.Value; RenderGrid(); } };
        op.PointerCaptureLost += (_, _) => Save();
        panel.Children.Add(op);

        // Extended ISO symbols (data/system + cloud). Off = core program-flowchart symbols only; placed
        // extended symbols get flagged. Saved with the diagram.
        panel.Children.Add(new Separator());
        var ext = new CheckBox { Content = Loc.S("Flow_ExtIso"), IsChecked = _data.ExtendedIso };
        ext.IsCheckedChanged += (_, _) => { _data.ExtendedIso = ext.IsChecked == true; Save(); RebuildAll(); };
        panel.Children.Add(ext);

        // Crossover bridges — non-DIN, temporary marker (not saved, gone on next load).
        panel.Children.Add(new Separator());
        var hops = new CheckBox { Content = Loc.S("Flow_Crossover"), IsChecked = _crossoverHops };
        hops.IsCheckedChanged += async (_, _) =>
        {
            _crossoverHops = hops.IsChecked == true;
            RenderCrossovers();
            if (_crossoverHops) await MessageDialog.Show(this, Loc.S("Flow_CrossoverHint"), Loc.S("Flow_Crossover"));
        };
        panel.Children.Add(hops);

        return new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
    }

    // Switches the global flow-line routing style and re-draws every arrow. Diagonal centre-to-centre
    // arrows are non-DIN, so warn (unless turned off in Options) when switching to them.
    void SetDiagonal(bool diagonal)
    {
        if (_data.DiagonalLines == diagonal) return;
        _data.DiagonalLines = diagonal;
        foreach (var c in _data.Connections) RenderConnection(c);
        RefreshConnHeader();
        Save();
        // No popup: diagonal lines are allowed but discouraged — the on-line "avoid" marker says enough.
    }

    // A dropdown menu item that runs an action when chosen.
    static MenuItem MI(string header, Action onClick)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => onClick();
        return mi;
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

        DiagramWindows.OpenOrActivate(DiagramWindows.StructId(_projFolder, _key), () => new StructogramWindow(_projFolder, _key, title, _themePath));
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
        RefreshJunctions();   // T-junctions are click-through in Select, clickable in Connect mode
        RefreshScaleHandles();   // show resize grips only in Scale mode
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
        Style(_scaleBtn,   _mode == EditMode.Scale);
        Style(_removeBtn,  _mode == EditMode.Remove);
        RefreshConnHeader();   // connect mode is shown in the merged menu's header, not a button
        if (_canvas is not null)
            _canvas.Cursor = new Cursor(_mode == EditMode.Remove ? StandardCursorType.No : StandardCursorType.Arrow);
    }

    // Updates the merged Connect menu header: 🔗 + the active arrow style icon, with a ● when drawing.
    void RefreshConnHeader()
    {
        if (_connMenu is null) return;
        var arrow = _data.DiagonalLines ? "⟍" : "➜";
        _connMenu.Header = (ConnectMode ? "● " : "") + "🔗 " + arrow + "  ▾";
    }

    // ── Node creation / rendering ──────────────────────────────────────────

    // Appends a new node of the given kind (with an optional DIN symbol variant) at a cascading offset.
    void AddNode(FlowNodeKind kind, FlowSymbol sym = FlowSymbol.Auto)
    {
        var at = SpawnPoint();
        var (dw, dh) = DefaultNodeSize(kind, sym);
        var node = new FlowNode
        {
            Kind   = kind,
            Symbol = sym,
            Text   = DefaultText(kind),
            X      = at.X,
            Y      = at.Y,
            Width  = dw,
            Height = dh,
        };
        _data.Nodes.Add(node);
        Save();
        RenderNode(node);
        if (_mode != EditMode.Select) SetMode(EditMode.Select);   // a fresh node is ready to place, not delete
    }

    // The standard (and minimum) size per node kind/symbol — shared by AddNode and the resize clamp,
    // so a symbol never scales below the dimensions it would be created at.
    static (double w, double h) DefaultNodeSize(FlowNodeKind kind, FlowSymbol sym)
    {
        bool offPage = sym == FlowSymbol.OffPageConnector;
        double w = kind == FlowNodeKind.Junction ? 9 : offPage ? 50 : kind == FlowNodeKind.Connector ? 46 : kind == FlowNodeKind.Decision ? 150 : 140;
        double h = kind == FlowNodeKind.Junction ? 9 : offPage ? 54 : kind == FlowNodeKind.Connector ? 46 : 56;
        return (w, h);
    }

    // A spawn position inside the currently visible viewport (accounts for scroll + zoom), with a small
    // cascade so successive additions don't stack exactly — so new nodes always appear in view.
    Point SpawnPoint()
    {
        double cascade = _data.Nodes.Count % 6 * 30;
        if (_scroll is null) return new Point(80 + cascade, 80 + cascade);
        double z = _zoom <= 0 ? 1 : _zoom;
        double x = _scroll.Offset.X / z + 40 + cascade;
        double y = _scroll.Offset.Y / z + 40 + cascade;
        return new Point(Snap(x), Snap(y));
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
        FlowNodeKind.Connector   => Loc.S("Flow_DefConnector"),
        FlowNodeKind.Junction    => "",
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

        Control shape = node.Symbol != FlowSymbol.Auto
            ? SymbolShape(node.Symbol, node.Width, node.Height, fill, stroke)
            : node.Kind switch
            {
                FlowNodeKind.Start or FlowNodeKind.End => RoundedBox(node.Height / 2, fill, stroke),
                FlowNodeKind.Decision    => DiamondShape(node.Width, node.Height, fill, stroke),
                FlowNodeKind.InputOutput => ParallelogramShape(node.Width, node.Height, fill, stroke),
                FlowNodeKind.Subroutine  => SubroutineShape(fill, stroke),
                FlowNodeKind.Comment     => CommentShape(fill, stroke),
                FlowNodeKind.Connector   => new Ellipse { Fill = new SolidColorBrush(fill), Stroke = new SolidColorBrush(stroke), StrokeThickness = 1.5 },
                FlowNodeKind.Junction    => new Ellipse { Fill = new SolidColorBrush(stroke) },
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

        // Mark an extended ISO symbol that's outside the chosen set (option off) — neutral, not "non-norm":
        // these symbols are part of ISO 5807, just not in the selected subset.
        if (!_data.ExtendedIso && FlowSymbols.IsExtended(node.Symbol))
        {
            var mark = new TextBlock
            {
                Text = "ⓘ", FontSize = 11, FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, -2, -2, 0),
            };
            ToolTip.SetTip(mark, Loc.S("Flow_ExtOutsideSet"));
            inner.Children.Add(mark);
        }

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
        // Above the connections' fat hit-zones (ZIndex 3) so a node sitting on a line — e.g. a junction or
        // connector — is grabbed/selected instead of the line beneath it.
        container.ZIndex = 6;
        _canvas!.Children.Add(container);
        _nodeViews[node.Id] = container;
        GrowCanvasFor(node.X, node.Y, node.Width, node.Height);

        WireNode(container, node, label);
    }

    // Wires a node's pointer interactions. Nodes are draggable in BOTH Select and Connect modes (place
    // and move feel like one mode); in Connect mode a node that's clicked — not dragged — starts/finishes
    // an arrow. Double-click (Select) edits / opens; right-click shows the menu or cancels a connection.
    void WireNode(Border container, FlowNode node, TextBlock label)
    {
        bool pressed = false, dragging = false;
        Point pressPos = default, offset = default;

        container.PointerPressed += (_, e) =>
        {
            var props = e.GetCurrentPoint(container).Properties;
            if (props.IsRightButtonPressed) { if (ConnectMode) CancelConnect(); else ShowNodeMenu(node, label, container); e.Handled = true; return; }
            if (!props.IsLeftButtonPressed) return;
            if (_mode == EditMode.Scale) { e.Handled = true; return; }   // resizing happens via the grips, not the body
            if (_mode == EditMode.Remove) { _selected.Clear(); _selected.Add(node.Id); RemoveSelected(); e.Handled = true; return; }
            // Double-click (outside connect mode): open a subroutine's diagram / edit the node text.
            if (e.ClickCount >= 2 && !ConnectMode) { if (node.Kind == FlowNodeKind.Subroutine) ShowChartFlow(node); else _ = EditNodeText(node, label); e.Handled = true; return; }

            pressed = true; dragging = false;
            pressPos = e.GetPosition(_canvas);
            offset   = e.GetPosition(container);
            // Pressing a node that's part of a multi-selection keeps it (so the whole group can be dragged);
            // pressing an unselected node selects it exclusively.
            if (!ConnectMode) { if (!_selected.Contains(node.Id)) { _selected.Clear(); _selected.Add(node.Id); } RefreshSelection(); }
            e.Pointer.Capture(container);
            e.Handled = true;
        };
        container.PointerMoved += (_, e) =>
        {
            if (!pressed) return;
            var pt = e.GetPosition(_canvas);
            if (!dragging)
            {
                if (Math.Abs(pt.X - pressPos.X) < 4 && Math.Abs(pt.Y - pressPos.Y) < 4) return;   // movement threshold
                dragging = true; _liveDrag = true;   // route attached arrows cheaply until release
                // Snapshot the start positions of every selected node, so the whole group moves together.
                _dragStart = _selected
                    .Select(id => _data.Nodes.FirstOrDefault(n => n.Id == id))
                    .Where(n => n is not null)
                    .ToDictionary(n => n!.Id, n => new Point(n!.X, n.Y));
            }
            // Snap the dragged node's CENTRE to the grid; shift the rest of the selection by the same delta.
            var nx = SnapCentered(Math.Max(0, pt.X - offset.X), node.Width);
            var ny = SnapCentered(Math.Max(0, pt.Y - offset.Y), node.Height);
            double dx = nx - (_dragStart!.TryGetValue(node.Id, out var s0) ? s0.X : node.X);
            double dy = ny - (_dragStart!.TryGetValue(node.Id, out s0) ? s0.Y : node.Y);
            foreach (var (id, start) in _dragStart)
            {
                var nd = _data.Nodes.FirstOrDefault(n => n.Id == id);
                if (nd is null) continue;
                nd.X = start.X + dx; nd.Y = start.Y + dy;
                if (_nodeViews.TryGetValue(id, out var v)) { Canvas.SetLeft(v, nd.X); Canvas.SetTop(v, nd.Y); }
                GrowCanvasFor(nd.X, nd.Y, nd.Width, nd.Height);
                UpdateConnectionsFor(id);
            }
            e.Handled = true;
        };
        container.PointerReleased += (_, e) =>
        {
            if (!pressed) return;
            pressed = false; e.Pointer.Capture(null);
            if (dragging)
            {
                dragging = false; _liveDrag = false;
                var moved = _dragStart?.Keys.ToList() ?? new List<string> { node.Id };
                _dragStart = null;
                if (node.Kind == FlowNodeKind.Junction) TrySpliceJunction(node);
                foreach (var id in moved)
                {
                    foreach (var c in _data.Connections)
                        if ((c.FromId == id || c.ToId == id) && c.Waypoints.Count > 0) NormalizeWaypoints(c);
                    UpdateConnectionsFor(id);   // re-route attached arrows with full A* now the drag settled
                }
                FitCanvas();   // grow/shrink the surface to fit the content (all sides)
                RenderCrossovers();
                Save();
            }
            else if (ConnectMode) HandleConnectClick(node.Id);   // a click (no drag) wires the arrow
            e.Handled = true;
        };
    }

    // If the junction now sits on a connection (that doesn't already touch it), split that connection so
    // the flow runs through the junction — so a generated structogram/code follows it too.
    void TrySpliceJunction(FlowNode jn)
    {
        var jr = new Rect(jn.X, jn.Y, jn.Width, jn.Height).Inflate(4);
        foreach (var c in _data.Connections.ToList())
        {
            if (c.FromId == jn.Id || c.ToId == jn.Id) continue;
            var a = NodeRect(c.FromId); var b = NodeRect(c.ToId);
            if (a is null || b is null) continue;
            var pts = _data.DiagonalLines
                ? new List<Point> { RectBorderPoint(a.Value, b.Value.Center), RectBorderPoint(b.Value, a.Value.Center) }
                : c.Waypoints.Count > 0 ? ManualRoute(a.Value, b.Value, c) : Simplify(OrthoRouteAvoiding(a.Value, b.Value, c.FromId, c.ToId, c.Id));
            if (!PolyHitsAny(pts, new List<Rect> { jr })) continue;

            // Splice: A→J keeps the label/style; J→B continues. Drop manual waypoints (route is now in two).
            _data.Connections.Remove(c);
            _data.Connections.Add(new FlowConnection { FromId = c.FromId, ToId = jn.Id, LineColor = c.LineColor, Label = c.Label });
            _data.Connections.Add(new FlowConnection { FromId = jn.Id, ToId = c.ToId, LineColor = c.LineColor });
            if (_connViews.TryGetValue(c.Id, out var vs)) { foreach (var v in vs) _canvas!.Children.Remove(v); _connViews.Remove(c.Id); }
            RenderAllConnections();
            return;   // one splice per drop
        }
    }

    // Lifts a junction out of the line it was spliced into: its connections are removed and, if it had
    // exactly one in and one out, that line is rejoined (X→J→Y ⇒ X→Y). The junction node stays, unwired.
    void DetachJunction(FlowNode jn)
    {
        var ins  = _data.Connections.Where(c => c.ToId   == jn.Id).ToList();
        var outs = _data.Connections.Where(c => c.FromId == jn.Id).ToList();
        if (ins.Count == 1 && outs.Count == 1)
            _data.Connections.Add(new FlowConnection { FromId = ins[0].FromId, ToId = outs[0].ToId, LineColor = ins[0].LineColor, Label = ins[0].Label });
        foreach (var c in ins.Concat(outs))
        {
            _data.Connections.Remove(c);
            if (_connViews.TryGetValue(c.Id, out var vs)) { foreach (var v in vs) _canvas!.Children.Remove(v); _connViews.Remove(c.Id); }
        }
        Save();
        RenderAllConnections();
    }

    // A junction shows its dot only when it's a genuine connected CROSSING — lines pass straight through
    // on BOTH axes (a "+"). A T-junction (one line ending on another: a horizontal through-line plus a
    // stub, or similar 3-way meet) needs no dot. Computed from the realized geometry, so dragging a
    // segment into / out of a cross hides / shows the dot live.
    // A junction shows its dot once four or more lines meet at it (a connected crossing / merge); with
    // three it's a plain T-piece (no dot). Degree-based: simple and robust — no fragile direction guessing
    // that broke when the fourth line happened to approach from an already-used side.
    bool JunctionIsCrossing(FlowNode jn)
        => _data.Connections.Count(c => c.FromId == jn.Id || c.ToId == jn.Id) >= 4;

    // A junction is a DERIVED point, not a thing you grab: it sits where its lines meet. Its X comes from
    // the vertically-arriving line(s) (neighbour above/below), its Y from the horizontally-arriving one(s)
    // (neighbour left/right). So moving the connected nodes/lines slides the junction along; it never has
    // to be dragged itself.
    void RecomputeJunctions()
    {
        foreach (var jn in _data.Nodes)
        {
            if (jn.Kind != FlowNodeKind.Junction) continue;
            double cx = jn.X + jn.Width / 2, cy = jn.Y + jn.Height / 2;
            double? nx = null, ny = null;
            foreach (var c in _data.Connections)
            {
                if (c.FromId != jn.Id && c.ToId != jn.Id) continue;
                var nr = NodeRect(c.FromId == jn.Id ? c.ToId : c.FromId);
                if (nr is null) continue;
                var oc = nr.Value.Center;
                if (Math.Abs(oc.Y - cy) >= Math.Abs(oc.X - cx)) nx ??= oc.X;   // above/below → fixes X (vertical line)
                else                                            ny ??= oc.Y;   // left/right → fixes Y (horizontal line)
            }
            jn.X = (nx ?? cx) - jn.Width / 2;
            jn.Y = (ny ?? cy) - jn.Height / 2;
        }
    }

    // Updates every junction's look + interactivity from its current geometry: a crossing shows its dot
    // and stays grabbable; a T-junction hides its dot and (in Select mode) is click-through, so it moves
    // by dragging the line segments rather than by grabbing an invisible point. In Connect mode every
    // junction stays clickable, so a fourth line can be wired onto / from it.
    void RefreshJunctions()
    {
        foreach (var jn in _data.Nodes)
        {
            if (jn.Kind != FlowNodeKind.Junction) continue;
            if (!_nodeViews.TryGetValue(jn.Id, out var container)) continue;
            bool cross = JunctionIsCrossing(jn);
            if (container.Child is Grid inner && inner.Children.Count > 0)
                inner.Children[0].IsVisible = cross;   // the junction dot (first child = the Ellipse)
            // A junction is never grabbed (it's derived from its lines); only clickable in Connect mode,
            // so a further line can be wired onto it.
            container.IsHitTestVisible = ConnectMode;
        }
    }

    // ── Resize (Scale mode) ──────────────────────────────────────────────────

    // Rebuilds the resize grips: in Scale mode, eight grips around every realized node (except auto
    // junctions); otherwise none. Safe to call any time EXCEPT during an active grip drag (it would drop
    // the captured grip) — the drag reposition path moves grips in place instead of rebuilding.
    void RefreshScaleHandles()
    {
        if (_canvas is null) return;
        foreach (var h in _scaleHandles) _canvas.Children.Remove(h);
        _scaleHandles.Clear();
        if (_mode != EditMode.Scale) return;

        foreach (var n in _data.Nodes)
        {
            if (n.Kind == FlowNodeKind.Junction) continue;       // auto points aren't resized
            if (!_nodeViews.ContainsKey(n.Id)) continue;          // only realized (visible) nodes
            for (int cx = 0; cx <= 2; cx++)
                for (int cy = 0; cy <= 2; cy++)
                {
                    if (cx == 1 && cy == 1) continue;             // skip the centre
                    var grip = MakeHandle(n, left: cx == 0, right: cx == 2, top: cy == 0, bottom: cy == 2);
                    _scaleHandles.Add(grip);
                    _canvas.Children.Add(grip);
                }
        }
    }

    // One resize grip for the given node + edges. Carries a HandleInfo so it can be repositioned.
    Control MakeHandle(FlowNode n, bool left, bool right, bool top, bool bottom)
    {
        var corner = (left || right) && (top || bottom);
        var cursor =
            corner ? (left == top ? StandardCursorType.TopLeftCorner   // NW / SE share a diagonal
                                  : StandardCursorType.TopRightCorner) // NE / SW share the other
            : (left || right) ? StandardCursorType.SizeWestEast
                              : StandardCursorType.SizeNorthSouth;
        var grip = new Rectangle
        {
            Width = HandleSize, Height = HandleSize,
            Fill = Brushes.White,
            Stroke = new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3)), StrokeThickness = 1.5,
            ZIndex = 40, Cursor = new Cursor(cursor),
            Tag = new HandleInfo(n.Id, left, right, top, bottom),
        };
        PlaceHandle(grip);
        WireHandle(grip);
        return grip;
    }

    // Positions a grip at its edge/corner of the (current) node rectangle.
    void PlaceHandle(Control grip)
    {
        if (grip.Tag is not HandleInfo hi) return;
        var n = _data.Nodes.FirstOrDefault(x => x.Id == hi.NodeId);
        if (n is null) return;
        double hx = hi.Left ? n.X : hi.Right ? n.X + n.Width : n.X + n.Width / 2;
        double hy = hi.Top ? n.Y : hi.Bottom ? n.Y + n.Height : n.Y + n.Height / 2;
        Canvas.SetLeft(grip, hx - HandleSize / 2);
        Canvas.SetTop(grip,  hy - HandleSize / 2);
    }

    // Dragging a grip moves the edge(s) it owns, clamped so the node never shrinks below its standard
    // size; the node + its arrows re-render live, and the node's grips follow without being rebuilt.
    void WireHandle(Control grip)
    {
        bool drag = false;
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            drag = true; _liveDrag = true; e.Pointer.Capture(grip); e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!drag || grip.Tag is not HandleInfo hi) return;
            var n = _data.Nodes.FirstOrDefault(x => x.Id == hi.NodeId);
            if (n is null) return;
            var (minW, minH) = DefaultNodeSize(n.Kind, n.Symbol);
            var p = e.GetPosition(_canvas);
            double l = n.X, r = n.X + n.Width, t = n.Y, b = n.Y + n.Height;
            if (hi.Right)  r = Math.Max(l + minW, Snap(p.X));
            if (hi.Left)   l = Math.Min(r - minW, Snap(p.X));
            if (hi.Bottom) b = Math.Max(t + minH, Snap(p.Y));
            if (hi.Top)    t = Math.Min(b - minH, Snap(p.Y));
            n.X = l; n.Y = t; n.Width = r - l; n.Height = b - t;
            ReRenderNode(n.Id);
            UpdateConnectionsFor(n.Id);
            foreach (var g in _scaleHandles)
                if (g.Tag is HandleInfo gi && gi.NodeId == n.Id) PlaceHandle(g);
            e.Handled = true;
        };
        grip.PointerReleased += (_, e) =>
        {
            if (!drag) return;
            drag = false; _liveDrag = false; e.Pointer.Capture(null);
            if (grip.Tag is HandleInfo hi)
            {
                foreach (var c in _data.Connections)
                    if ((c.FromId == hi.NodeId || c.ToId == hi.NodeId) && c.Waypoints.Count > 0) NormalizeWaypoints(c);
                UpdateConnectionsFor(hi.NodeId);
            }
            FitCanvas(); RenderCrossovers(); Save();
            RefreshScaleHandles();
            e.Handled = true;
        };
    }

    // Re-renders a single node from the model (used during live resize).
    void ReRenderNode(string id)
    {
        if (_nodeViews.TryGetValue(id, out var v)) { _canvas!.Children.Remove(v); _nodeViews.Remove(id); }
        var n = _data.Nodes.FirstOrDefault(x => x.Id == id);
        if (n is not null) RenderNode(n);
    }

    // The node right-click menu: edit text or delete.
    void ShowNodeMenu(FlowNode node, TextBlock label, Control anchor)
    {
        var cm = new ContextMenu();
        var edit = new MenuItem { Header = Loc.S("Flow_EditText") };
        edit.Click += (_, _) => _ = EditNodeText(node, label);
        cm.Items.Add(edit);
        if (node.Kind == FlowNodeKind.Subroutine)
        {
            var chart = new MenuItem { Header = Loc.S("Struct_ShowChart") };
            chart.Click += (_, _) => ShowChartFlow(node);
            cm.Items.Add(chart);
        }

        var style = new MenuItem { Header = Loc.S("Style_Open") };
        style.Click += (_, _) => _ = EditNodeStyle(node);
        cm.Items.Add(style);

        // A junction wired into a line can be lifted back out (reconnecting the line it split).
        if (node.Kind == FlowNodeKind.Junction && _data.Connections.Any(c => c.FromId == node.Id || c.ToId == node.Id))
        {
            var detach = new MenuItem { Header = Loc.S("Flow_DetachJunction") };
            detach.Click += (_, _) => DetachJunction(node);
            cm.Items.Add(detach);
        }

        // I/O nodes can take a DIN symbol variant (document, display, punched card, storage media…).
        if (node.Kind == FlowNodeKind.InputOutput)
        {
            var symMenu = new MenuItem { Header = Loc.S("Flow_Symbol") };
            void Sym(string label, FlowSymbol s) { var mi = new MenuItem { Header = label }; mi.Click += (_, _) => SetNodeSymbol(node, s); symMenu.Items.Add(mi); }
            Sym(Loc.S("Flow_SymAuto"),         FlowSymbol.Auto);
            Sym(Loc.S("Flow_SymDocument"),     FlowSymbol.Document);
            Sym(Loc.S("Flow_SymDisplay"),      FlowSymbol.Display);
            Sym(Loc.S("Flow_SymManualInput"),  FlowSymbol.ManualInput);
            Sym(Loc.S("Flow_SymPunchedCard"),  FlowSymbol.PunchedCard);
            Sym(Loc.S("Flow_SymMagneticTape"), FlowSymbol.MagneticTape);
            Sym(Loc.S("Flow_SymMagneticDisk"), FlowSymbol.MagneticDisk);
            Sym(Loc.S("Flow_SymStoredData"),   FlowSymbol.StoredData);
            cm.Items.Add(symMenu);
        }

        cm.Items.Add(new Separator());
        var del = new MenuItem { Header = Loc.S("Flow_DeleteNode") };
        del.Click += (_, _) => { _selected.Clear(); _selected.Add(node.Id); RemoveSelected(); };
        cm.Items.Add(del);
        OpenMenu(cm, anchor);
    }

    // Applies a DIN symbol variant to an I/O node and redraws it (and its connectors).
    void SetNodeSymbol(FlowNode node, FlowSymbol s)
    {
        node.Symbol = s;
        if (_nodeViews.TryGetValue(node.Id, out var v)) { _canvas!.Children.Remove(v); _nodeViews.Remove(node.Id); }
        RenderNode(node);
        UpdateConnectionsFor(node.Id);
        Save();
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

        var conn = new FlowConnection { FromId = _connectFromId, ToId = nodeId, LineColor = _style.LineColor };
        _connectFromId = null;
        RemoveRubberBand();

        // Decisions get a branch label (yes/no, etc.).
        if (_data.Nodes.FirstOrDefault(n => n.Id == conn.FromId)?.Kind == FlowNodeKind.Decision)
            conn.Label = await PromptDialog.Show(this, Loc.S("Flow_BranchPrompt"), "") ?? "";

        _data.Connections.Add(conn);
        Save();
        RenderConnection(conn);
        RefreshJunctions();   // a new line onto a junction may turn it into a crossing → show its dot
    }

    // Renders every saved connection (used once after nodes are laid out).
    void RenderAllConnections()
    {
        // Render plain (node-ending) lines first so their points exist, then the taps that ride on them.
        foreach (var c in _data.Connections) if (string.IsNullOrEmpty(c.ToTapConn)) RenderConnection(c);
        foreach (var c in _data.Connections) if (!string.IsNullOrEmpty(c.ToTapConn)) RenderConnection(c);
        RenderTapDots();
        RenderCrossovers();
    }

    // Optional, non-DIN overlay: at every place a horizontal flow line crosses a vertical one, draw a small
    // "bridge"/hop on the horizontal line (old ANSI / electronics style). Purely a visual marker — never
    // saved, cleared while dragging, and gone on the next load.
    void RenderCrossovers()
    {
        if (_canvas is null) return;
        foreach (var v in _hopVisuals) _canvas.Children.Remove(v);
        _hopVisuals.Clear();
        if (!_crossoverHops || _liveDrag) return;

        var bg   = new SolidColorBrush(ParseColor(_style.BackgroundColor));
        var line = new SolidColorBrush(ParseColor(_style.LineColor));

        // Collect axis-aligned segments of the currently realized connections.
        var segs = new List<(Point a, Point b)>();
        foreach (var (id, pts) in _connPts)
        {
            if (!_connViews.ContainsKey(id)) continue;
            for (int i = 0; i < pts.Count - 1; i++) segs.Add((pts[i], pts[i + 1]));
        }

        const double r = 5;
        foreach (var h in segs)
        {
            if (Math.Abs(h.a.Y - h.b.Y) > 0.5) continue;   // h must be horizontal
            double hy = h.a.Y, hx1 = Math.Min(h.a.X, h.b.X), hx2 = Math.Max(h.a.X, h.b.X);
            foreach (var v in segs)
            {
                if (Math.Abs(v.a.X - v.b.X) > 0.5) continue;   // v must be vertical
                double vx = v.a.X, vy1 = Math.Min(v.a.Y, v.b.Y), vy2 = Math.Max(v.a.Y, v.b.Y);
                if (vx <= hx1 + 1 || vx >= hx2 - 1 || hy <= vy1 + 1 || hy >= vy2 - 1) continue;   // strict interior cross

                // Erase the horizontal line under the bridge, then arc the horizontal over the vertical.
                var gap = new Line { StartPoint = new(vx - r, hy), EndPoint = new(vx + r, hy), Stroke = bg, StrokeThickness = 3, ZIndex = 2 };
                var arc = new Avalonia.Controls.Shapes.Path
                {
                    Stroke = line, StrokeThickness = 1.6, ZIndex = 2,
                    Data = Geometry.Parse($"M {Inv(vx - r)},{Inv(hy)} A {Inv(r)},{Inv(r)} 0 0 1 {Inv(vx + r)},{Inv(hy)}"),
                };
                _canvas.Children.Add(gap); _hopVisuals.Add(gap);
                _canvas.Children.Add(arc); _hopVisuals.Add(arc);
            }
        }
    }

    // The route for a connection that has manual waypoints: node-edge exit/entry (chosen toward the first
    // and last waypoint) with the user's bends in between.
    static List<Point> ManualRoute(Rect a, Rect b, FlowConnection conn)
    {
        var wps = conn.Waypoints.Select(w => new Point(w.X, w.Y)).ToList();
        var pts = new List<Point> { EdgeMid(a, wps[0]) };
        pts.AddRange(wps);
        pts.Add(EdgeMid(b, wps[^1]));
        return Orthogonalize(pts);   // never let a manual route draw a diagonal segment
    }

    // Guarantees an orthogonal polyline: between any two points that aren't axis-aligned, insert an
    // L-corner. Consecutive duplicates/collinear points are dropped. So no segment is ever diagonal,
    // and no end ever cuts a slanted line behind a moved node.
    static List<Point> Orthogonalize(List<Point> pts)
    {
        var res = new List<Point> { pts[0] };
        for (int i = 1; i < pts.Count; i++)
        {
            var prev = res[^1]; var cur = pts[i];
            if (Math.Abs(prev.X - cur.X) > 0.5 && Math.Abs(prev.Y - cur.Y) > 0.5)
                res.Add(new Point(prev.X, cur.Y));   // bend: leave prev vertically, then run horizontally
            if (Math.Abs(res[^1].X - cur.X) > 0.5 || Math.Abs(res[^1].Y - cur.Y) > 0.5) res.Add(cur);
        }
        return Simplify(res);
    }

    // The midpoint of the rectangle edge facing a target point (keeps exits/entries orthogonal).
    static Point EdgeMid(Rect r, Point toward)
    {
        var c = r.Center;
        double dx = toward.X - c.X, dy = toward.Y - c.Y;
        return Math.Abs(dy) >= Math.Abs(dx)
            ? new Point(c.X, dy >= 0 ? r.Bottom : r.Top)
            : new Point(dx >= 0 ? r.Right : r.Left, c.Y);
    }

    // Starts dragging a segment. The full current polyline (incl. node-edge ends) is captured as the
    // baseline; the move then rebuilds it (inserting corners next to fixed ends so even an end segment or
    // a straight same-height line bends cleanly).
    void BeginSegmentDrag(FlowConnection conn, int segIdx, PointerPressedEventArgs e)
    {
        var pts = RouteOf(conn);   // handles both node-ending and tap-ending lines
        if (pts is null || pts.Count < 2 || segIdx < 0 || segIdx >= pts.Count - 1) return;
        _segConn   = conn;
        _segIdx    = segIdx;
        _segBasePts = pts;
        _segHoriz  = Math.Abs(pts[segIdx + 1].Y - pts[segIdx].Y) < Math.Abs(pts[segIdx + 1].X - pts[segIdx].X);
        _segStart  = e.GetPosition(_canvas);

        // Junctions are derived from their lines (RecomputeJunctions), so no segment-drag "follow" hack.
        _segJunctionId = null;

        e.Pointer.Capture(_canvas);
    }

    // Moves the dragged segment perpendicular to its orientation; the result's interior points become the
    // connection's waypoints (node-edge ends are re-derived by ManualRoute).
    void DragSegment(Point cur)
    {
        if (_segConn is null || _segBasePts is null) return;
        double v = _segHoriz ? Snap(_segBasePts[_segIdx].Y + (cur.Y - _segStart.Y))
                             : Snap(_segBasePts[_segIdx].X + (cur.X - _segStart.X));

        // Dragging the end segment of a junction-incident line moves the junction itself along the
        // perpendicular axis, so the whole T/cross relocates instead of bending to its old centre.
        if (_segJunctionId is not null)
        {
            var jn = _data.Nodes.FirstOrDefault(n => n.Id == _segJunctionId);
            if (jn is not null)
            {
                if (_segHoriz) jn.Y = v - jn.Height / 2; else jn.X = v - jn.Width / 2;
                if (_nodeViews.TryGetValue(jn.Id, out var jv)) { Canvas.SetLeft(jv, jn.X); Canvas.SetTop(jv, jn.Y); }
                _liveDrag = true; UpdateConnectionsFor(jn.Id); _liveDrag = false;
                RefreshJunctions();
            }
            return;
        }

        var full = BuildDragged(_segBasePts, _segIdx, _segHoriz, v);
        _segConn.Waypoints = full.Skip(1).Take(full.Count - 2).Select(p => new BoardWaypoint { X = p.X, Y = p.Y }).ToList();
        RenderConnection(_segConn);
        RenderTapsOnto(_segConn.Id);   // any T-piece on this line follows it (and its dot)
    }

    // Rebuilds a polyline with segment k moved to the perpendicular coordinate v, keeping it orthogonal.
    // Interior bend points just shift; a segment touching a fixed (node-edge) end grows a new corner.
    static List<Point> BuildDragged(List<Point> pts, int k, bool horiz, double v)
    {
        int last = pts.Count - 1;
        var P = pts[k]; var Q = pts[k + 1];
        var P2 = horiz ? new Point(P.X, v) : new Point(v, P.Y);
        var Q2 = horiz ? new Point(Q.X, v) : new Point(v, Q.Y);

        var res = new List<Point>();
        for (int i = 0; i < k; i++) res.Add(pts[i]);   // points before the segment
        if (k == 0) res.Add(P);                         // fixed start (exit) stays as the pivot
        res.Add(P2);
        res.Add(Q2);
        if (k + 1 == last) res.Add(Q);                  // fixed end (entry) stays as the pivot
        for (int i = k + 2; i <= last; i++) res.Add(pts[i]);   // points after the segment
        return res;
    }

    // The polyline for a connection: from its source node to either its target node, or — if it taps onto
    // another line — to the point on that line. Diagonal / manual-waypoint / auto-orthogonal as usual.
    List<Point>? RouteOf(FlowConnection conn)
    {
        var a = RouteRect(conn.FromId);
        Rect? b;
        bool toTap = !string.IsNullOrEmpty(conn.ToTapConn);
        if (toTap)
        {
            var tp = TapPoint(conn);
            if (tp is null) return null;
            b = new Rect(tp.Value.X, tp.Value.Y, 0, 0);
        }
        else b = RouteRect(conn.ToId);
        if (a is null || b is null) return null;

        if (_data.DiagonalLines)
            return new List<Point> { RectBorderPoint(a.Value, b.Value.Center), RectBorderPoint(b.Value, a.Value.Center) };
        if (conn.Waypoints.Count > 0) return ManualRoute(a.Value, b.Value, conn);
        // A line that taps onto another runs ALONG it to reach the point, so skip overlap-avoidance there.
        return Simplify(_liveDrag ? OrthoRoute(a.Value, b.Value)
                                  : OrthoRouteAvoiding(a.Value, b.Value, conn.FromId, conn.ToId, conn.Id, endsOnLine: toTap));
    }

    // The point where a tapping connection meets its target line (at fraction ToTapT). Null if the target
    // is gone or not yet routed. Only resolves one level (a tap onto a plain line), no nested taps.
    Point? TapPoint(FlowConnection conn)
    {
        var target = _data.Connections.FirstOrDefault(c => c.Id == conn.ToTapConn);
        if (target is null) return null;
        List<Point>? pts = _connPts.TryGetValue(target.Id, out var tp) && tp.Count >= 2 ? tp : null;
        if (pts is null && string.IsNullOrEmpty(target.ToTapConn)) pts = RouteOf(target);   // compute non-tap target
        if (pts is null || pts.Count < 2) return null;
        return PointAlong(pts, conn.ToTapT).pt;
    }

    // Connecting in connect mode and clicking a line: the new line ENDS on that line (a T-piece / tap) at
    // the click position — no junction node, no splitting of the target.
    async void TapOntoLine(FlowConnection target, Point at)
    {
        if (_connectFromId is null) return;
        var from = _connectFromId;
        _connectFromId = null;
        RemoveRubberBand();

        double t = 0.5;
        if (_connPts.TryGetValue(target.Id, out var pts) && pts.Count >= 2)
        {
            // Project the click onto the line, snap that foot to the grid, then store it as a fraction —
            // so the T-piece sits on a grid line along the target.
            var foot = PointAlong(pts, NearestFraction(pts, at)).pt;
            t = NearestFraction(pts, new Point(Snap(foot.X), Snap(foot.Y)));
        }

        string label = "";
        if (_data.Nodes.FirstOrDefault(n => n.Id == from)?.Kind == FlowNodeKind.Decision)
            label = await PromptDialog.Show(this, Loc.S("Flow_BranchPrompt"), "") ?? "";

        _data.Connections.Add(new FlowConnection { FromId = from, ToTapConn = target.Id, ToTapT = t, LineColor = _style.LineColor, Label = label });
        Save();
        RenderAllConnections();
    }

    // Removes any tap whose target line no longer exists (e.g. its node was deleted).
    void CleanupTaps()
    {
        var ids = _data.Connections.Select(c => c.Id).ToHashSet();
        foreach (var c in _data.Connections.Where(c => !string.IsNullOrEmpty(c.ToTapConn) && !ids.Contains(c.ToTapConn)).ToList())
        {
            _data.Connections.Remove(c);
            if (_connViews.TryGetValue(c.Id, out var vs)) foreach (var v in vs) _canvas!.Children.Remove(v);
            _connViews.Remove(c.Id); _connPts.Remove(c.Id);
        }
    }

    // Draws a dot wherever two lines tap the SAME target at (almost) the same point — i.e. two T-pieces
    // coincide into a connected crossing. A lone tap stays a plain (dotless) T-piece.
    readonly List<Control> _tapDots = new();
    // Slides a tap's meeting point along its target line to follow the cursor (grid-snapped).
    void SlideTap(Point cur)
    {
        if (_tapDrag is null) return;
        var target = _data.Connections.FirstOrDefault(c => c.Id == _tapDrag.ToTapConn);
        if (target is null || !_connPts.TryGetValue(target.Id, out var pts) || pts.Count < 2) return;
        var foot = PointAlong(pts, NearestFraction(pts, cur)).pt;
        _tapDrag.ToTapT = NearestFraction(pts, new Point(Snap(foot.X), Snap(foot.Y)));
        _tapDrag.Waypoints.Clear();   // slide gives a clean straight stub to the new point
        RenderConnection(_tapDrag);
        RenderTapDots();
    }

    // Re-renders every tap that ends on the given line (after it moved), plus the coincidence dots.
    void RenderTapsOnto(string targetId)
    {
        foreach (var c in _data.Connections)
            if (c.ToTapConn == targetId) RenderConnection(c);
        RenderTapDots();
    }

    void RenderTapDots()
    {
        if (_canvas is null) return;
        foreach (var d in _tapDots) _canvas.Children.Remove(d);
        _tapDots.Clear();
        var taps = _data.Connections.Where(c => !string.IsNullOrEmpty(c.ToTapConn)).ToList();
        var brush = new SolidColorBrush(ParseColor(_style.LineColor));
        for (int i = 0; i < taps.Count; i++)
            for (int j = i + 1; j < taps.Count; j++)
            {
                if (taps[i].ToTapConn != taps[j].ToTapConn) continue;
                var p1 = TapPoint(taps[i]); var p2 = TapPoint(taps[j]);
                if (p1 is null || p2 is null || Dist(p1.Value, p2.Value) > 8) continue;
                const double r = 4;
                var dot = new Ellipse { Width = r * 2, Height = r * 2, Fill = brush, IsHitTestVisible = false, ZIndex = 5 };
                Canvas.SetLeft(dot, p1.Value.X - r); Canvas.SetTop(dot, p1.Value.Y - r);
                _canvas.Children.Add(dot); _tapDots.Add(dot);
            }
    }

    // Draws one connection: line, arrowhead, a transparent hit-zone for editing, and an optional label.
    void RenderConnection(FlowConnection conn)
    {
        if (_connViews.TryGetValue(conn.Id, out var old))
            foreach (var v in old) _canvas!.Children.Remove(v);
        _connViews.Remove(conn.Id);

        var pts = RouteOf(conn);
        if (pts is null || pts.Count < 2) return;

        bool toTap = !string.IsNullOrEmpty(conn.ToTapConn);   // this line ends ON another line (a T-piece)

        _connPts[conn.Id] = pts;   // remembered for the optional crossover-bridge overlay

        // All arrows share the diagram's arrow colour, so they stay uniform (and follow the Options picker).
        var brush   = new SolidColorBrush(ParseColor(_style.LineColor));
        var visuals = new List<Control>();

        var line = new Polyline { Stroke = brush, StrokeThickness = conn.Thickness, IsHitTestVisible = false };
        foreach (var p in pts) line.Points.Add(p);
        line.ZIndex = 1;
        _canvas!.Children.Add(line); visuals.Add(line);

        // Arrowhead: explicit per-connection override wins, else automatic (an arrow, except onto a line —
        // a T-piece carries no arrowhead, the meeting itself is the marker).
        if (conn.Arrow ?? !toTap)
        {
            var arrow = BuildArrow(pts[^2], pts[^1], brush);
            arrow.ZIndex = 1;
            _canvas.Children.Add(arrow); visuals.Add(arrow);
        }

        // DIN departure marker: a small filled dot where the flow line leaves the source edge.
        if (!_data.DiagonalLines)
        {
            const double r = 3.5;
            var dot = new Ellipse { Width = r * 2, Height = r * 2, Fill = brush, IsHitTestVisible = false };
            Canvas.SetLeft(dot, pts[0].X - r); Canvas.SetTop(dot, pts[0].Y - r);
            dot.ZIndex = 2;
            _canvas.Children.Add(dot); visuals.Add(dot);
        }

        // Fat transparent per-segment hit zones: right-click menu / remove on any; in Select mode an
        // interior segment (both ends are bends) can be dragged to reposition it for tidy layouts.
        var capConn = conn;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var p0 = pts[i]; var p1 = pts[i + 1];
            // Any segment is draggable (not in diagonal mode): dragging perpendicular bends the line, even
            // for a straight arrow at the same height as its target (it grows a fresh knick).
            bool draggable = !_data.DiagonalLines;
            bool horiz = Math.Abs(p1.Y - p0.Y) < Math.Abs(p1.X - p0.X);
            var seg = new Line
            {
                StartPoint = p0, EndPoint = p1, Stroke = Brushes.Transparent, StrokeThickness = 12, ZIndex = 3,
                Cursor = new Cursor(draggable ? (horiz ? StandardCursorType.SizeNorthSouth : StandardCursorType.SizeWestEast)
                                              : StandardCursorType.Hand),
            };
            int segIdx = i;
            seg.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(seg).Properties.IsRightButtonPressed) { ShowConnMenu(capConn, seg); e.Handled = true; return; }
                if (_mode == EditMode.Remove) { DeleteConnection(capConn); e.Handled = true; return; }
                // Connecting and clicking a line: the new line ENDS on this line (a T-piece / tap).
                if (ConnectMode && _connectFromId is not null) { TapOntoLine(capConn, e.GetPosition(_canvas)); e.Handled = true; return; }
                // Dragging the END segment of a tap line (the one touching the target) slides its meeting
                // point along the target; other segments bend normally, so an L-shaped tap stays adjustable.
                if (_mode == EditMode.Select && !string.IsNullOrEmpty(capConn.ToTapConn)
                    && _connPts.TryGetValue(capConn.Id, out var tpts) && segIdx == tpts.Count - 2)
                { _tapDrag = capConn; e.Pointer.Capture(_canvas); e.Handled = true; return; }
                if (draggable) { BeginSegmentDrag(capConn, segIdx, e); e.Handled = true; }   // Select or Connect mode
            };
            _canvas.Children.Add(seg); visuals.Add(seg);
        }

        // Diagonal lines are allowed but discouraged: a gentle amber "⚠ avoid" marker (no harsh "non-norm"),
        // shown when marking is on.
        if (_data.DiagonalLines && AppSettings.NormMark)
        {
            var m = pts[pts.Count / 2];
            var n = new TextBlock
            {
                Text = "⚠", FontSize = 12, FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF9, 0xA8, 0x25)), IsHitTestVisible = false,
            };
            ToolTip.SetTip(n, Loc.S("Norm_AvoidDiagonal"));
            Canvas.SetLeft(n, m.X + 4); Canvas.SetTop(n, m.Y + 4);
            n.ZIndex = 5;
            _canvas.Children.Add(n); visuals.Add(n);
        }

        // Transmission path marker: a small zig-zag at the route's middle point.
        if (conn.Transmission)
        {
            var m = pts[pts.Count / 2];
            var zig = new Polyline { Stroke = brush, StrokeThickness = 1.6, IsHitTestVisible = false };
            foreach (var p in new[] { new Point(m.X - 7, m.Y), new Point(m.X - 2, m.Y - 6), new Point(m.X + 2, m.Y + 6), new Point(m.X + 7, m.Y) })
                zig.Points.Add(p);
            zig.ZIndex = 2;
            _canvas.Children.Add(zig); visuals.Add(zig);
        }

        if (!string.IsNullOrWhiteSpace(conn.Label))
        {
            var badge = new Border { CornerRadius = new(3), Padding = new(4, 1, 4, 1), Cursor = new Cursor(StandardCursorType.Hand) };
            Ui.Theme(badge, Border.BackgroundProperty, "SidebarBgBrush");
            var t = new TextBlock { Text = conn.Label, FontSize = 10 };
            Ui.Theme(t, TextBlock.ForegroundProperty, "SidebarTextBrush");
            badge.Child = t;

            var (anchor, horizontal) = LabelAnchor(conn, pts);
            PlaceLabel(badge, anchor, horizontal, conn.Label, conn.LabelOff);
            badge.ZIndex = 4;
            WireLabelDrag(badge, conn);   // drag along the line to reposition
            _canvas.Children.Add(badge); visuals.Add(badge);
        }

        _connViews[conn.Id] = visuals;
    }

    // Where the label sits: an explicit LabelPos fraction along the line, else auto — the first segment
    // for a decision branch (so it reads right next to the diamond), otherwise the longest segment.
    (Point anchor, bool horizontal) LabelAnchor(FlowConnection conn, List<Point> pts)
    {
        if (pts.Count < 2) return (pts.Count == 1 ? pts[0] : default, true);
        if (conn.LabelPos >= 0) return PointAlong(pts, conn.LabelPos);

        Point sa, sb;
        bool fromDecision = _data.Nodes.FirstOrDefault(n => n.Id == conn.FromId)?.Kind == FlowNodeKind.Decision;
        if (fromDecision) { sa = pts[0]; sb = pts[1]; }
        else (sa, sb) = LongestSegment(pts);
        return (new Point((sa.X + sb.X) / 2, (sa.Y + sb.Y) / 2), Math.Abs(sb.X - sa.X) >= Math.Abs(sb.Y - sa.Y));
    }

    // Positions the label badge a FIXED small distance from the line; only the SIDE is chosen by off
    // (its sign): +below/right, -above/left, 0 = default (above a horizontal run, right of a vertical one).
    // The label always hugs the line — the side can be flipped, but it can't be dragged away from it.
    static void PlaceLabel(Border badge, Point anchor, bool horizontal, string label, double off)
    {
        double estW = Math.Max(16, label.Length * 6.5 + 8);
        const double h = 16;
        double mag  = horizontal ? h / 2 + 4 : estW / 2 + 6;
        double sign = off > 0 ? 1 : off < 0 ? -1 : (horizontal ? -1 : 1);   // default: above (horiz) / right (vert)
        double useOff = sign * mag;
        double cx = horizontal ? anchor.X : anchor.X + useOff;
        double cy = horizontal ? anchor.Y + useOff : anchor.Y;
        Canvas.SetLeft(badge, cx - estW / 2);
        Canvas.SetTop(badge,  cy - h / 2);
    }

    static double Dist(Point a, Point b) { double dx = a.X - b.X, dy = a.Y - b.Y; return Math.Sqrt(dx * dx + dy * dy); }

    // The point at fraction t (0..1) of the polyline's length, plus whether the local segment is horizontal.
    static (Point pt, bool horizontal) PointAlong(List<Point> pts, double t)
    {
        double total = 0;
        for (int i = 0; i < pts.Count - 1; i++) total += Dist(pts[i], pts[i + 1]);
        double target = Math.Clamp(t, 0, 1) * total, acc = 0;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            double seg = Dist(pts[i], pts[i + 1]);
            if (acc + seg >= target || i == pts.Count - 2)
            {
                double f = seg < 1e-6 ? 0 : (target - acc) / seg;
                var p = new Point(pts[i].X + (pts[i + 1].X - pts[i].X) * f, pts[i].Y + (pts[i + 1].Y - pts[i].Y) * f);
                return (p, Math.Abs(pts[i + 1].X - pts[i].X) >= Math.Abs(pts[i + 1].Y - pts[i].Y));
            }
            acc += seg;
        }
        return (pts[^1], true);
    }

    // The fraction (0..1) along the polyline closest to q — used to map a label drag onto the line.
    static double NearestFraction(List<Point> pts, Point q)
    {
        double total = 0;
        for (int i = 0; i < pts.Count - 1; i++) total += Dist(pts[i], pts[i + 1]);
        if (total < 1e-6) return 0;
        double best = double.MaxValue, bestPos = 0, acc = 0;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            Point a = pts[i], b = pts[i + 1];
            double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
            double f = len2 < 1e-9 ? 0 : Math.Clamp(((q.X - a.X) * dx + (q.Y - a.Y) * dy) / len2, 0, 1);
            double cx = a.X + f * dx, cy = a.Y + f * dy;
            double d = (q.X - cx) * (q.X - cx) + (q.Y - cy) * (q.Y - cy);
            if (d < best) { best = d; bestPos = (acc + f * Math.Sqrt(len2)) / total; }
            acc += Math.Sqrt(len2);
        }
        return bestPos;
    }

    // Lets the user drag a label badge along its line; the chosen position is stored as LabelPos.
    void WireLabelDrag(Border badge, FlowConnection conn)
    {
        bool drag = false;
        badge.PointerPressed += (_, e) =>
        {
            if (_mode != EditMode.Select || !e.GetCurrentPoint(badge).Properties.IsLeftButtonPressed) return;
            drag = true; e.Pointer.Capture(badge); e.Handled = true;
        };
        badge.PointerMoved += (_, e) =>
        {
            if (!drag || !_connPts.TryGetValue(conn.Id, out var pts) || pts.Count < 2) return;
            var q = e.GetPosition(_canvas);
            conn.LabelPos = NearestFraction(pts, q);
            var (anchor, horizontal) = PointAlong(pts, conn.LabelPos);
            double perp = horizontal ? q.Y - anchor.Y : q.X - anchor.X;
            conn.LabelOff = Math.Sign(perp);   // store the SIDE only; distance stays fixed
            PlaceLabel(badge, anchor, horizontal, conn.Label, conn.LabelOff);
            e.Handled = true;
        };
        badge.PointerReleased += (_, e) =>
        {
            if (!drag) return;
            drag = false; e.Pointer.Capture(null); Save(); e.Handled = true;
        };
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

        // Toggle arrowhead: the menu offers the OTHER style (line ⇄ arrow). The current effective state
        // is the explicit override, or the automatic one (no arrow into a junction).
        bool toJunction = _data.Nodes.FirstOrDefault(n => n.Id == conn.ToId)?.Kind == FlowNodeKind.Junction;
        bool hasArrow   = conn.Arrow ?? !toJunction;
        var style = new MenuItem { Header = hasArrow ? Loc.S("Flow_StyleLine") : Loc.S("Flow_StyleArrow") };
        style.Click += (_, _) => { conn.Arrow = !hasArrow; Save(); RenderConnection(conn); };
        cm.Items.Add(style);

        var flip = new MenuItem { Header = Loc.S("Flow_FlipArrow") };
        flip.Click += (_, _) => { (conn.FromId, conn.ToId) = (conn.ToId, conn.FromId); Save(); RenderConnection(conn); };
        cm.Items.Add(flip);
        var trans = new MenuItem { Header = conn.Transmission ? Loc.S("Flow_TransmissionOff") : Loc.S("Flow_Transmission") };
        trans.Click += (_, _) => { conn.Transmission = !conn.Transmission; Save(); RenderConnection(conn); };
        cm.Items.Add(trans);
        if (conn.Waypoints.Count > 0)
        {
            var reset = new MenuItem { Header = Loc.S("Flow_ResetRoute") };
            reset.Click += (_, _) => { conn.Waypoints.Clear(); Save(); RenderConnection(conn); };
            cm.Items.Add(reset);
        }
        var del = new MenuItem { Header = Loc.S("Flow_DeleteArrow") };
        del.Click += (_, _) => DeleteConnection(conn);
        cm.Items.Add(del);
        OpenMenu(cm, anchor);
    }

    // Re-draws all arrows touching a node (called while it is being dragged).
    void UpdateConnectionsFor(string nodeId)
    {
        var affected = new HashSet<string>();
        foreach (var c in _data.Connections)
            if (c.FromId == nodeId || c.ToId == nodeId) { AlignManualEnds(c); RenderConnection(c); affected.Add(c.Id); }
        // Any line tapping onto a re-routed line must follow its new path.
        foreach (var c in _data.Connections)
            if (!string.IsNullOrEmpty(c.ToTapConn) && affected.Contains(c.ToTapConn)) RenderConnection(c);
        RenderTapDots();
    }

    // Keeps a manually-routed connection's END segments straight when a node it touches moves: the first
    // waypoint stays aligned with the (new) exit edge midpoint, the last with the entry — so the stub
    // stretches/shifts orthogonally instead of drawing a diagonal to the old bend (and the next segment,
    // sharing the other axis, stays straight too).
    void AlignManualEnds(FlowConnection c)
    {
        if (c.Waypoints.Count == 0) return;
        var a = RouteRect(c.FromId); var b = RouteRect(c.ToId);
        if (a is null || b is null) return;

        var w0 = c.Waypoints[0];
        var exit = EdgeMid(a.Value, new Point(w0.X, w0.Y));
        if (Math.Abs(exit.Y - w0.Y) >= Math.Abs(exit.X - w0.X)) w0.X = exit.X;   // vertical stub → align X
        else                                                    w0.Y = exit.Y;   // horizontal stub → align Y

        var wl = c.Waypoints[^1];
        var entry = EdgeMid(b.Value, new Point(wl.X, wl.Y));
        if (Math.Abs(entry.Y - wl.Y) >= Math.Abs(entry.X - wl.X)) wl.X = entry.X;
        else                                                      wl.Y = entry.Y;
    }

    // Tidies a manual route's stored waypoints to the fewest needed (drops collinear runs and the little
    // back-and-forth jogs that pile up while shoving a connected node about). Only ever removes points,
    // so it can't accumulate. Called when an edit settles (on release).
    void NormalizeWaypoints(FlowConnection c)
    {
        if (c.Waypoints.Count == 0) return;
        var a = NodeRect(c.FromId); var b = NodeRect(c.ToId);
        if (a is null || b is null) return;
        var full = ManualRoute(a.Value, b.Value, c);   // orthogonalised + simplified
        c.Waypoints = full.Skip(1).Take(full.Count - 2).Select(p => new BoardWaypoint { X = p.X, Y = p.Y }).ToList();
    }

    // Expands the canvas when content nears its right/bottom edge, so there's always room to grow.
    void GrowCanvasFor(double x, double y, double w, double h)
    {
        if (_canvas is null) return;
        const double margin = 400;
        bool grew = false;
        if (x + w + margin > _canvas.Width)  { _canvas.Width  = x + w + margin; grew = true; }
        if (y + h + margin > _canvas.Height) { _canvas.Height = y + h + margin; grew = true; }
        if (grew && _gridRect is not null) { _gridRect.Width = _canvas.Width; _gridRect.Height = _canvas.Height; }
    }

    // Translates the whole world (nodes + waypoints) by (dx,dy) — used to make room on the left/top (a
    // Canvas can't hold negative coordinates). Scrolls by the same amount so the view doesn't jump.
    void ShiftWorld(double dx, double dy)
    {
        if (_canvas is null || (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)) return;
        foreach (var n in _data.Nodes) { n.X += dx; n.Y += dy; }
        foreach (var c in _data.Connections) foreach (var w in c.Waypoints) { w.X += dx; w.Y += dy; }
        if (_scroll is not null) _scroll.Offset = new Vector(Math.Max(0, _scroll.Offset.X + dx * _zoom), Math.Max(0, _scroll.Offset.Y + dy * _zoom));
        foreach (var (id, v) in _nodeViews)
            if (_data.Nodes.FirstOrDefault(n => n.Id == id) is { } nd) { Canvas.SetLeft(v, nd.X); Canvas.SetTop(v, nd.Y); }
        foreach (var c in _data.Connections) RenderConnection(c);
    }

    // Fits the canvas snugly around the content with a uniform margin — so it grows when you push to an
    // edge and shrinks back when you move away (all four sides). Called when an edit settles.
    // <param name="trim">When true (the "Crop" action), also pull content up/left to remove top/left
    // whitespace. The automatic call (false) only grows that side, preserving a centered layout.</param>
    void FitCanvas(bool trim = false)
    {
        if (_canvas is null || _data.Nodes.Count == 0) return;
        const double pad = 80;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Inc(double x, double y) { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); }
        foreach (var n in _data.Nodes) { Inc(n.X, n.Y); Inc(n.X + n.Width, n.Y + n.Height); }
        foreach (var c in _data.Connections) foreach (var w in c.Waypoints) Inc(w.X, w.Y);

        // Shift by a whole number of grid cells, so everything stays aligned to the grid (which tiles from
        // 0,0) — a non-grid shift would knock all placements off the grid.
        // Only ever GROW toward the top-left (dx/dy >= 0). Never pull content leftward/upward to trim a
        // margin: a deliberately centered layout (e.g. a tree fanning down from a top-centre start) must
        // keep its left/top whitespace. Right/bottom still shrink, since that just resizes the surface.
        double g = _data.GridSize >= 1 ? _data.GridSize : 10;
        double dx = Math.Round((pad - minX) / g) * g, dy = Math.Round((pad - minY) / g) * g;
        if (!trim) { dx = Math.Max(0, dx); dy = Math.Max(0, dy); }   // auto-fit grows only; Crop also trims
        ShiftWorld(dx, dy);   // bring the top-left of the content near the margin

        // Never shrink below the visible viewport (so a near-empty chart doesn't collapse to a tiny box).
        double minW = Math.Max(800, _scroll?.Viewport.Width  / (_zoom <= 0 ? 1 : _zoom) ?? 800);
        double minH = Math.Max(600, _scroll?.Viewport.Height / (_zoom <= 0 ? 1 : _zoom) ?? 600);
        double cw = Math.Max(minW, maxX + dx + pad), ch = Math.Max(minH, maxY + dy + pad);
        if (Math.Abs(_canvas.Width  - cw) > 0.5) _canvas.Width  = cw;
        if (Math.Abs(_canvas.Height - ch) > 0.5) _canvas.Height = ch;
        if (_gridRect is not null) { _gridRect.Width = _canvas.Width; _gridRect.Height = _canvas.Height; }
    }

    // ── Remove ─────────────────────────────────────────────────────────────

    // Deletes every selected node (and its arrows), then persists once.
    void RemoveSelected()
    {
        bool anySub = _selected.Any(id => _data.Nodes.FirstOrDefault(n => n.Id == id) is { Kind: FlowNodeKind.Subroutine } s && !string.IsNullOrEmpty(s.RefId));
        foreach (var id in _selected.ToList()) DeleteNode(id, persist: false);
        _selected.Clear();
        CleanupTaps();             // drop taps whose target line was deleted with its node
        RenderAllConnections();
        FitCanvas();   // shrink the surface back if the removed nodes freed up edge space
        Save();
        if (anySub) _ = InfoDialog.Show(this, "sub_remove", Loc.S("Sub_RemoveInfo"), Loc.S("Flow_Subroutine"));
    }

    // Opens (or creates, in the Functions library) the function a subroutine node calls, then its diagram.
    async void ShowChartFlow(FlowNode node)
    {
        if (string.IsNullOrEmpty(node.RefId))
        {
            var id = await SubroutineLinkDialog.Show(this, _projFolder, "");
            if (string.IsNullOrEmpty(id)) return;
            var fn = CodeEntityService.LoadAll(_projFolder, "Function").FirstOrDefault(x => x.Id == id);
            if (fn is null) return;
            node.RefId = fn.Id; node.Text = fn.Name; Save();
            if (_nodeViews.TryGetValue(node.Id, out var v)) { _canvas!.Children.Remove(v); _nodeViews.Remove(node.Id); }
            RenderNode(node); UpdateConnectionsFor(node.Id);
        }
        var f = CodeEntityService.LoadAll(_projFolder, "Function").FirstOrDefault(x => x.Id == node.RefId);
        _ = DiagramLauncher.ChooseAndOpen(this, _projFolder, node.RefId, f?.Name ?? node.Text, _themePath);
    }

    // ── Drag functions in from the cockpit (→ subroutine nodes) ──────────────

    void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(CodeBoardWindow.EntityDragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetValue(CodeBoardWindow.EntityDragFormat) is not string payload) return;
        var sep = payload.IndexOf(CodeBoardWindow.DragSep);
        if (sep < 0 || !string.Equals(payload[..sep], _projFolder, StringComparison.OrdinalIgnoreCase)) return;
        var ids = payload[(sep + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries);

        var funcs = CodeEntityService.LoadAll(_projFolder, "Function").ToDictionary(x => x.Id);
        var at = e.GetPosition(_canvas!);
        double off = 0;
        foreach (var id in ids)
        {
            if (!funcs.TryGetValue(id, out var fn)) continue;   // only functions become subroutine nodes
            var node = new FlowNode
            {
                Kind = FlowNodeKind.Subroutine, RefId = fn.Id, Text = fn.Name,
                X = Math.Max(0, at.X + off), Y = Math.Max(0, at.Y + off), Width = 150, Height = 56,
            };
            _data.Nodes.Add(node);
            RenderNode(node);
            off += 26;
        }
        Save();
        e.Handled = true;
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
            _connPts.Remove(c.Id);
        }
        if (persist) Save();
    }

    // Removes a single connection and its visuals.
    void DeleteConnection(FlowConnection conn)
    {
        // Lines that tapped onto this one lose their anchor — remove them too.
        var tappers = _data.Connections.Where(c => c.ToTapConn == conn.Id).ToList();
        foreach (var c in tappers.Append(conn))
        {
            _data.Connections.Remove(c);
            if (_connViews.TryGetValue(c.Id, out var vs)) foreach (var v in vs) _canvas!.Children.Remove(v);
            _connViews.Remove(c.Id);
            _connPts.Remove(c.Id);
        }
        RenderAllConnections();
        Save();
    }

    // Tidies up junctions after a deletion: a junction is only meaningful with 3+ lines. With two, it is
    // merged into a single line running through its point (waypoints concatenated, so the course is kept
    // exactly); with one, the dangling stub is dropped; with none, the orphan point is removed. So deleting
    // the line that fed a T-piece rejoins the original line, and no invisible point is left behind (which
    // would also act as a phantom routing obstacle).
    void CleanupJunctions()
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var jn in _data.Nodes.Where(n => n.Kind == FlowNodeKind.Junction).ToList())
            {
                var touching = _data.Connections.Where(c => c.FromId == jn.Id || c.ToId == jn.Id).ToList();
                if (touching.Count >= 3) continue;   // still a real junction (T or +)

                // A junction carrying just two lines isn't a junction — merge the two into one line that
                // runs through the junction's point (concatenate their waypoints), so the course is kept
                // exactly, then drop the junction.
                if (touching.Count == 2)
                {
                    var c1 = touching[0]; var c2 = touching[1];
                    string e1 = c1.FromId == jn.Id ? c1.ToId : c1.FromId;
                    string e2 = c2.FromId == jn.Id ? c2.ToId : c2.FromId;
                    var jc = new BoardWaypoint { X = jn.X + jn.Width / 2, Y = jn.Y + jn.Height / 2 };
                    // c1 oriented e1→J, c2 oriented J→e2 (reverse whichever is stored the other way round).
                    var wp1 = c1.ToId   == jn.Id ? c1.Waypoints.ToList() : Enumerable.Reverse(c1.Waypoints).ToList();
                    var wp2 = c2.FromId == jn.Id ? c2.Waypoints.ToList() : Enumerable.Reverse(c2.Waypoints).ToList();
                    _data.Connections.Add(new FlowConnection
                    {
                        FromId = e1, ToId = e2, LineColor = c1.LineColor, Label = c1.Label,
                        Waypoints = wp1.Append(jc).Concat(wp2).ToList(),
                    });
                }

                foreach (var c in touching)
                {
                    _data.Connections.Remove(c);
                    if (_connViews.TryGetValue(c.Id, out var cvs)) { foreach (var v in cvs) _canvas!.Children.Remove(v); _connViews.Remove(c.Id); }
                    _connPts.Remove(c.Id);
                }
                _data.Nodes.Remove(jn);
                if (_nodeViews.TryGetValue(jn.Id, out var nv)) { _canvas!.Children.Remove(nv); _nodeViews.Remove(jn.Id); }
                changed = true;
            }
        }
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

    // DIN 66001 I/O symbol variants — cosmetic shapes drawn at the node's size (semantics stay I/O).
    static Control SymbolShape(FlowSymbol sym, double w, double h, Color fill, Color stroke)
    {
        var fb = new SolidColorBrush(fill);
        var sb = new SolidColorBrush(stroke);
        static string F(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        Avalonia.Controls.Shapes.Path P(string d, bool filled = true) => new() { Data = Geometry.Parse(d), Fill = filled ? fb : null, Stroke = sb, StrokeThickness = 1.5 };

        switch (sym)
        {
            case FlowSymbol.Document:    // rectangle with a wavy bottom edge
                return P($"M0,0 L{F(w)},0 L{F(w)},{F(h * 0.82)} C{F(w * 0.66)},{F(h * 1.04)} {F(w * 0.33)},{F(h * 0.6)} 0,{F(h * 0.82)} Z");
            case FlowSymbol.Display:     // screen — curved-in left, rounded right
                return P($"M{F(w * 0.18)},0 L{F(w * 0.85)},0 C{F(w)},0 {F(w)},{F(h)} {F(w * 0.85)},{F(h)} L{F(w * 0.18)},{F(h)} C0,{F(h * 0.7)} 0,{F(h * 0.3)} {F(w * 0.18)},0 Z");
            case FlowSymbol.ManualInput: // slanted top edge (keyboard)
                return P($"M0,{F(h * 0.28)} L{F(w)},0 L{F(w)},{F(h)} L0,{F(h)} Z");
            case FlowSymbol.PunchedCard: // clipped top-left corner
                return P($"M{F(w * 0.2)},0 L{F(w)},0 L{F(w)},{F(h)} L0,{F(h)} L0,{F(h * 0.28)} Z");
            case FlowSymbol.OffPageConnector: // home-plate pentagon pointing down (continues on another page)
                return P($"M0,0 L{F(w)},0 L{F(w)},{F(h * 0.58)} L{F(w * 0.5)},{F(h)} L0,{F(h * 0.58)} Z");
            case FlowSymbol.StoredData:  // curved left + right edges
                return P($"M{F(w * 0.12)},0 L{F(w)},0 C{F(w * 0.86)},{F(h * 0.5)} {F(w * 0.86)},{F(h * 0.5)} {F(w)},{F(h)} L{F(w * 0.12)},{F(h)} C0,{F(h * 0.5)} 0,{F(h * 0.5)} {F(w * 0.12)},0 Z");
            case FlowSymbol.MagneticTape: // reel: circle + tangent foot
            {
                var g = new Grid();
                g.Children.Add(new Ellipse { Fill = fb, Stroke = sb, StrokeThickness = 1.5, Margin = new(w * 0.06, 0, w * 0.06, h * 0.12) });
                g.Children.Add(new Line { StartPoint = new(w * 0.52, h * 0.95), EndPoint = new(w * 0.98, h * 0.95), Stroke = sb, StrokeThickness = 1.5 });
                return g;
            }
            case FlowSymbol.MagneticDisk: // database cylinder
            {
                var g = new Grid();
                g.Children.Add(P($"M0,{F(h * 0.16)} C0,{F(-h * 0.02)} {F(w)},{F(-h * 0.02)} {F(w)},{F(h * 0.16)} L{F(w)},{F(h * 0.84)} C{F(w)},{F(h * 1.02)} 0,{F(h * 1.02)} 0,{F(h * 0.84)} Z"));
                g.Children.Add(P($"M0,{F(h * 0.16)} C0,{F(h * 0.34)} {F(w)},{F(h * 0.34)} {F(w)},{F(h * 0.16)}", filled: false));
                return g;
            }
            case FlowSymbol.Preparation:   // elongated hexagon (setup / loop init)
                return P($"M{F(w * 0.18)},0 L{F(w * 0.82)},0 L{F(w)},{F(h * 0.5)} L{F(w * 0.82)},{F(h)} L{F(w * 0.18)},{F(h)} L0,{F(h * 0.5)} Z");
            case FlowSymbol.Delay:         // rectangle with a rounded right end (D shape)
                // Same cap curvature as before (control points sit 0.4·w past where the rounding starts),
                // just shifted right: start at 0.7·w with controls at 1.1·w, so the bulge reaches exactly
                // x=w — as wide as the other symbols, without stretching the rounding into an egg shape.
                return P($"M0,0 L{F(w * 0.7)},0 C{F(w * 1.1)},0 {F(w * 1.1)},{F(h)} {F(w * 0.7)},{F(h)} L0,{F(h)} Z");
            case FlowSymbol.ManualOperation: // trapezoid, wider at the top
                return P($"M0,0 L{F(w)},0 L{F(w * 0.82)},{F(h)} L{F(w * 0.18)},{F(h)} Z");
            case FlowSymbol.LoopLimit:     // rectangle with the two top corners cut off
                return P($"M{F(w * 0.18)},0 L{F(w * 0.82)},0 L{F(w)},{F(h * 0.28)} L{F(w)},{F(h)} L0,{F(h)} L0,{F(h * 0.28)} Z");
            case FlowSymbol.Parallel:      // two horizontal bars (parallel-mode fork/join)
            {
                var g = new Grid();
                g.Children.Add(new Line { StartPoint = new(0, h * 0.22), EndPoint = new(w, h * 0.22), Stroke = sb, StrokeThickness = 2 });
                g.Children.Add(new Line { StartPoint = new(0, h * 0.78), EndPoint = new(w, h * 0.78), Stroke = sb, StrokeThickness = 2 });
                return g;
            }
            case FlowSymbol.Merge:         // downward triangle ▽
                return P($"M0,0 L{F(w)},0 L{F(w * 0.5)},{F(h)} Z");
            case FlowSymbol.Extract:       // upward triangle △
                return P($"M{F(w * 0.5)},0 L{F(w)},{F(h)} L0,{F(h)} Z");
            case FlowSymbol.Collate:       // two triangles tip-to-tip (hourglass)
            {
                var g = new Grid();
                g.Children.Add(P($"M0,0 L{F(w)},0 L{F(w * 0.5)},{F(h * 0.5)} Z"));
                g.Children.Add(P($"M{F(w * 0.5)},{F(h * 0.5)} L{F(w)},{F(h)} L0,{F(h)} Z"));
                return g;
            }
            case FlowSymbol.Sort:          // diamond split by a horizontal line
            {
                var g = new Grid();
                g.Children.Add(P($"M{F(w * 0.5)},0 L{F(w)},{F(h * 0.5)} L{F(w * 0.5)},{F(h)} L0,{F(h * 0.5)} Z"));
                g.Children.Add(new Line { StartPoint = new(0, h * 0.5), EndPoint = new(w, h * 0.5), Stroke = sb, StrokeThickness = 1.5 });
                return g;
            }
            case FlowSymbol.CloudStorage:  // a simple cloud outline (modern)
                return P($"M{F(w * 0.25)},{F(h * 0.85)} C{F(w * 0.02)},{F(h * 0.85)} {F(w * 0.02)},{F(h * 0.5)} {F(w * 0.22)},{F(h * 0.48)} C{F(w * 0.24)},{F(h * 0.2)} {F(w * 0.6)},{F(h * 0.18)} {F(w * 0.64)},{F(h * 0.42)} C{F(w * 0.9)},{F(h * 0.36)} {F(w * 0.98)},{F(h * 0.78)} {F(w * 0.75)},{F(h * 0.85)} Z");
            default: return RoundedBox(4, fill, stroke);
        }
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
        FlowNodeKind.Connector   => (Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0x42, 0x42, 0x42)),
        FlowNodeKind.Junction    => (Color.FromRgb(0x37, 0x47, 0x4F), Color.FromRgb(0x37, 0x47, 0x4F)),
        _                        => (Color.FromRgb(0xE3, 0xF2, 0xFD), Color.FromRgb(0x15, 0x65, 0xC0)),
    };

    // ── Geometry helpers ───────────────────────────────────────────────────

    // The model rectangle for a node id, or null if it's gone.
    Rect? NodeRect(string id)
    {
        var n = _data.Nodes.FirstOrDefault(x => x.Id == id);
        return n is null ? null : new Rect(n.X, n.Y, n.Width, n.Height);
    }

    // The rect used for ROUTING a connection to/from a node. A junction collapses to a zero-size point at
    // its EXACT centre, so every line incident to it begins/ends on the same coordinate — the junction
    // itself (which sits on the line it splits). Don't re-snap here: snapping the centre to the grid can
    // move the meeting point off a line that runs on a non-grid coordinate, leaving a gap.
    Rect? RouteRect(string id)
    {
        var n = _data.Nodes.FirstOrDefault(x => x.Id == id);
        if (n is null) return null;
        if (n.Kind == FlowNodeKind.Junction)
            return new Rect(n.X + n.Width / 2, n.Y + n.Height / 2, 0, 0);
        return new Rect(n.X, n.Y, n.Width, n.Height);
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

    // A DIN-style orthogonal route between two node rects: exit at an edge midpoint (bottom/top for a
    // mostly-vertical link, right/left for a horizontal one), straight H/V segments, enter the target's
    // opposite edge midpoint. Collinear points collapse to a straight line.
    // Shortest distance from a point to a line segment (for finding which segment a click/junction is on).
    static double DistToSegment(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        double t = len2 < 1e-9 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        double cx = a.X + t * dx, cy = a.Y + t * dy;
        return Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
    }

    static List<Point> OrthoRoute(Rect s, Rect t, ISet<char>? srcBusy = null, ISet<char>? dstBusy = null)
    {
        var sc = s.Center; var tc = t.Center;
        double dx = tc.X - sc.X, dy = tc.Y - sc.Y;
        // Departure bias: prefer leaving from the bottom/right (the "forward" edges). Only fall back to a
        // top/left exit when the target is up and/or left with no forward option. So a target down-left
        // exits at the BOTTOM (not the left, where an incoming arrow often sits), down-right by dominance.
        bool down = dy > 0, right = dx > 0;
        bool vertical = (down && right) ? Math.Abs(dy) >= Math.Abs(dx)
                       : down  ? true
                       : right ? false
                       : Math.Abs(dy) >= Math.Abs(dx);   // up-left: by dominance

        // If the chosen exit edge already carries another connection but the alternative axis' edge is
        // free, switch axes — so a new line doesn't depart from a side that already has an arrow.
        char SrcSide(bool vert) => vert ? (down ? 'B' : 'T') : (right ? 'R' : 'L');
        if (srcBusy is { Count: > 0 } && srcBusy.Contains(SrcSide(vertical)) && !srcBusy.Contains(SrcSide(!vertical)))
            vertical = !vertical;

        if (vertical)
        {
            var exit  = new Point(sc.X, dy >= 0 ? s.Bottom : s.Top);
            var entry = new Point(tc.X, dy >= 0 ? t.Top    : t.Bottom);
            double midY = (exit.Y + entry.Y) / 2;
            return new() { exit, new(exit.X, midY), new(entry.X, midY), entry };
        }
        else
        {
            var exit  = new Point(dx >= 0 ? s.Right : s.Left, sc.Y);
            var entry = new Point(dx >= 0 ? t.Left  : t.Right, tc.Y);
            double midX = (exit.X + entry.X) / 2;
            return new() { exit, new(midX, exit.Y), new(midX, entry.Y), entry };
        }
    }

    // Orthogonal route that steers clear of other nodes. Starts from the simple Z-route; if that already
    // misses every node it's kept (cleanest). Otherwise a grid A* (one grid-unit clearance, turn-penalised
    // for straight runs) routes around them. Falls back to the simple route if the area is huge or blocked.
    List<Point> OrthoRouteAvoiding(Rect s, Rect t, string fromId, string toId, string? selfId = null, bool endsOnLine = false)
    {
        var simple = OrthoRoute(s, t, OccupiedSides(fromId, selfId), OccupiedSides(toId, selfId));
        double g = _data.GridSize >= 4 ? _data.GridSize : 10;

        var obstacles = new List<Rect>();
        foreach (var n in _data.Nodes)
        {
            if (n.Id == fromId || n.Id == toId) continue;
            if (n.Kind == FlowNodeKind.Junction) continue;   // junctions are points lines pass THROUGH, not around
            obstacles.Add(new Rect(n.X, n.Y, n.Width, n.Height).Inflate(g));   // ≥1 grid clearance
        }

        // A line that taps onto another must run ALONG that line to reach the point, so overlap-avoidance
        // must not apply (it would otherwise bow the line around its own target).
        // Other lines the route should avoid lying on top of (it may still cross them, just not overlap).
        var otherSegs = endsOnLine ? new List<(Point a, Point b)>() : OtherConnectionSegments(selfId);
        bool hitsNodes = obstacles.Count > 0 && PolyHitsAny(simple, obstacles);
        bool onLines   = !endsOnLine && OverlapsExisting(simple, otherSegs, g);
        if (!hitsNodes && !onLines) return simple;

        var start = simple[0];
        var goal  = simple[^1];
        double pad = 5 * g;
        double minX = Math.Max(0, Math.Min(start.X, goal.X) - pad);
        double minY = Math.Max(0, Math.Min(start.Y, goal.Y) - pad);
        double maxX = Math.Max(start.X, goal.X) + pad;
        double maxY = Math.Max(start.Y, goal.Y) + pad;

        int cols = (int)Math.Round((maxX - minX) / g) + 1;
        int rows = (int)Math.Round((maxY - minY) / g) + 1;
        if (cols < 2 || rows < 2 || (long)cols * rows > 40000) return simple;

        var blk = new bool[cols, rows];
        for (int c = 0; c < cols; c++)
            for (int r = 0; r < rows; r++)
            {
                var p = new Point(minX + c * g, minY + r * g);
                foreach (var o in obstacles) if (o.Contains(p)) { blk[c, r] = true; break; }
            }

        // Soft penalty on cells lying on another line, so A* prefers a clear corridor but never fails.
        var pen = new double[cols, rows];
        const double linePenalty = 4.0;
        foreach (var seg in otherSegs) MarkSegment(pen, seg, minX, minY, g, cols, rows, linePenalty);

        int Cc(double v, int max) => Math.Clamp((int)Math.Round(v), 0, max);
        int sc = Cc((start.X - minX) / g, cols - 1), sr = Cc((start.Y - minY) / g, rows - 1);
        int gc = Cc((goal.X  - minX) / g, cols - 1), gr = Cc((goal.Y  - minY) / g, rows - 1);
        blk[sc, sr] = false; blk[gc, gr] = false;   // endpoints must be enterable
        pen[sc, sr] = 0; pen[gc, gr] = 0;            // don't punish entering/leaving at the endpoints

        var cells = AStar(blk, cols, rows, sc, sr, gc, gr, pen);
        if (cells is null) return simple;

        // Cells → points, with the exact node-edge endpoints, then drop redundant collinear points.
        var pts = new List<Point> { start };
        foreach (var (c, r) in cells) pts.Add(new Point(minX + c * g, minY + r * g));
        pts.Add(goal);
        return Simplify(pts);
    }

    // The edges (L/R/T/B) of a node already used by other connections' endpoints — so a new line can
    // avoid departing/arriving on a side that already has an arrow.
    HashSet<char> OccupiedSides(string nodeId, string? exceptId)
    {
        var set = new HashSet<char>();
        var n = _data.Nodes.FirstOrDefault(x => x.Id == nodeId);
        if (n is null || n.Kind == FlowNodeKind.Junction) return set;   // a junction is a point, not a box with sides
        var r = new Rect(n.X, n.Y, n.Width, n.Height);
        foreach (var (id, pts) in _connPts)
        {
            if (id == exceptId || pts.Count == 0) continue;
            var conn = _data.Connections.FirstOrDefault(c => c.Id == id);
            if (conn is null) continue;
            Point ep;
            if (conn.FromId == nodeId)    ep = pts[0];
            else if (conn.ToId == nodeId) ep = pts[^1];
            else continue;
            set.Add(NearestSide(r, ep));
        }
        return set;
    }

    static char NearestSide(Rect r, Point p)
    {
        double dl = Math.Abs(p.X - r.Left), dr = Math.Abs(p.X - r.Right), dt = Math.Abs(p.Y - r.Top), db = Math.Abs(p.Y - r.Bottom);
        double m = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
        return m == dl ? 'L' : m == dr ? 'R' : m == dt ? 'T' : 'B';
    }

    // The realized segments of every OTHER connection (for line-avoidance), excluding the one being routed.
    List<(Point a, Point b)> OtherConnectionSegments(string? selfId)
    {
        var live = _data.Connections.Select(c => c.Id).ToHashSet();
        var segs = new List<(Point, Point)>();
        foreach (var (id, pts) in _connPts)
        {
            if (id == selfId || !live.Contains(id)) continue;   // skip self and any stale (deleted) entries
            for (int i = 0; i < pts.Count - 1; i++) segs.Add((pts[i], pts[i + 1]));
        }
        return segs;
    }

    // True if a route segment runs collinear with, and overlaps (for more than one grid cell), an existing
    // segment — i.e. the new line would lie on top of another line rather than merely crossing it.
    static bool OverlapsExisting(List<Point> route, List<(Point a, Point b)> others, double g)
    {
        for (int i = 0; i < route.Count - 1; i++)
        {
            Point p = route[i], q = route[i + 1];
            bool horiz = Math.Abs(p.Y - q.Y) < 0.5, vert = Math.Abs(p.X - q.X) < 0.5;
            if (!horiz && !vert) continue;
            foreach (var (oa, ob) in others)
            {
                if (horiz)
                {
                    if (Math.Abs(oa.Y - ob.Y) > 0.5 || Math.Abs(oa.Y - p.Y) > 0.5) continue;
                    double lo = Math.Max(Math.Min(p.X, q.X), Math.Min(oa.X, ob.X));
                    double hi = Math.Min(Math.Max(p.X, q.X), Math.Max(oa.X, ob.X));
                    if (hi - lo > g) return true;
                }
                else
                {
                    if (Math.Abs(oa.X - ob.X) > 0.5 || Math.Abs(oa.X - p.X) > 0.5) continue;
                    double lo = Math.Max(Math.Min(p.Y, q.Y), Math.Min(oa.Y, ob.Y));
                    double hi = Math.Min(Math.Max(p.Y, q.Y), Math.Max(oa.Y, ob.Y));
                    if (hi - lo > g) return true;
                }
            }
        }
        return false;
    }

    // Adds a penalty to every grid cell along an axis-aligned segment (used to keep routes off other lines).
    static void MarkSegment(double[,] pen, (Point a, Point b) seg, double minX, double minY, double g, int cols, int rows, double val)
    {
        var (a, b) = seg;
        if (Math.Abs(a.Y - b.Y) < 0.5)        // horizontal
        {
            int r = (int)Math.Round((a.Y - minY) / g);
            if (r < 0 || r >= rows) return;
            int c0 = (int)Math.Round((Math.Min(a.X, b.X) - minX) / g);
            int c1 = (int)Math.Round((Math.Max(a.X, b.X) - minX) / g);
            for (int c = Math.Max(0, c0); c <= Math.Min(cols - 1, c1); c++) pen[c, r] += val;
        }
        else if (Math.Abs(a.X - b.X) < 0.5)   // vertical
        {
            int c = (int)Math.Round((a.X - minX) / g);
            if (c < 0 || c >= cols) return;
            int r0 = (int)Math.Round((Math.Min(a.Y, b.Y) - minY) / g);
            int r1 = (int)Math.Round((Math.Max(a.Y, b.Y) - minY) / g);
            for (int r = Math.Max(0, r0); r <= Math.Min(rows - 1, r1); r++) pen[c, r] += val;
        }
    }

    // 4-direction A* with a turn penalty (prefers long straight runs). Returns cell path or null.
    static List<(int c, int r)>? AStar(bool[,] blk, int cols, int rows, int sc, int sr, int gc, int gr, double[,]? pen = null)
    {
        const double turn = 2.0;
        int Key(int c, int r, int d) => (r * cols + c) * 5 + (d + 1);   // d: -1 start, 0..3 dirs
        int[] dx = { 1, -1, 0, 0 }, dy = { 0, 0, 1, -1 };

        var gScore = new Dictionary<int, double>();
        var came   = new Dictionary<int, (int c, int r, int d)>();
        var open   = new PriorityQueue<(int c, int r, int d), double>();
        int sKey = Key(sc, sr, -1);
        gScore[sKey] = 0;
        open.Enqueue((sc, sr, -1), Math.Abs(gc - sc) + Math.Abs(gr - sr));

        while (open.TryDequeue(out var cur, out _))
        {
            if (cur.c == gc && cur.r == gr)
            {
                var path = new List<(int, int)>();
                var node = cur;
                while (!(node.c == sc && node.r == sr && node.d == -1))
                {
                    path.Add((node.c, node.r));
                    node = came[Key(node.c, node.r, node.d)];
                }
                path.Reverse();
                return path;
            }
            double cg = gScore[Key(cur.c, cur.r, cur.d)];
            for (int d = 0; d < 4; d++)
            {
                int nc = cur.c + dx[d], nr = cur.r + dy[d];
                if (nc < 0 || nr < 0 || nc >= cols || nr >= rows || blk[nc, nr]) continue;
                double ng = cg + 1 + (cur.d != -1 && d != cur.d ? turn : 0) + (pen?[nc, nr] ?? 0);
                int nk = Key(nc, nr, d);
                if (gScore.TryGetValue(nk, out var old) && old <= ng) continue;
                gScore[nk] = ng;
                came[nk] = cur;
                open.Enqueue((nc, nr, d), ng + Math.Abs(gc - nc) + Math.Abs(gr - nr));
            }
        }
        return null;
    }

    // Drops middle points that lie on a straight run, leaving only the corner vertices.
    static List<Point> Simplify(List<Point> pts)
    {
        if (pts.Count <= 2) return pts;
        var outp = new List<Point> { pts[0] };
        for (int i = 1; i < pts.Count - 1; i++)
        {
            var a = outp[^1]; var b = pts[i]; var c = pts[i + 1];
            bool collinear = (Math.Abs(a.X - b.X) < 0.5 && Math.Abs(b.X - c.X) < 0.5)
                          || (Math.Abs(a.Y - b.Y) < 0.5 && Math.Abs(b.Y - c.Y) < 0.5);
            if (!collinear) outp.Add(b);
        }
        outp.Add(pts[^1]);
        return outp;
    }

    // Whether any axis-aligned segment of a polyline crosses any obstacle rectangle.
    static bool PolyHitsAny(List<Point> pts, List<Rect> obstacles)
    {
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var p = pts[i]; var q = pts[i + 1];
            double x0 = Math.Min(p.X, q.X), x1 = Math.Max(p.X, q.X);
            double y0 = Math.Min(p.Y, q.Y), y1 = Math.Max(p.Y, q.Y);
            foreach (var o in obstacles)
                if (x1 >= o.Left && x0 <= o.Right && y1 >= o.Top && y0 <= o.Bottom) return true;
        }
        return false;
    }

    // The longest segment of a polyline — where a connection label reads best.
    static (Point a, Point b) LongestSegment(List<Point> pts)
    {
        var best = (a: pts[0], b: pts[^1]);
        double bestLen = -1;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            double dx = pts[i + 1].X - pts[i].X, dy = pts[i + 1].Y - pts[i].Y;
            double len = dx * dx + dy * dy;
            if (len > bestLen) { bestLen = len; best = (pts[i], pts[i + 1]); }
        }
        return best;
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

    // Snaps a node's top-left so that its CENTRE lands on a grid line (centre-aligned → straight arrows).
    double SnapCentered(double topLeft, double size) =>
        !_data.SnapToGrid || _data.GridSize < 1 ? topLeft : Snap(topLeft + size / 2) - size / 2;

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

    // Aborts an in-progress connection (right-click during connect mode).
    void CancelConnect()
    {
        _connectFromId = null;
        RemoveRubberBand();
    }

    // While connecting, clicking an existing line drops a junction at that spot, splits the line through
    // it (X→Y ⇒ X→J→Y) and wires the armed node into it — a quick way to merge flows onto a line.
    async void InsertJunctionOnLine(FlowConnection line, Point at)
    {
        if (_connectFromId is null) return;
        var from = _connectFromId;

        // A branch leaving a decision gets a label here too (same as a node-to-node connection).
        string fromLabel = "";
        if (_data.Nodes.FirstOrDefault(n => n.Id == from)?.Kind == FlowNodeKind.Decision)
            fromLabel = await PromptDialog.Show(this, Loc.S("Flow_BranchPrompt"), "") ?? "";

        // Find which segment of the line was clicked and project the click exactly ONTO it, so the
        // junction sits on the line — both halves stay straight (no spurious left-right jog). The along-edge
        // coordinate is grid-snapped; the cross-edge one is taken from the line.
        var a = NodeRect(line.FromId); var b = NodeRect(line.ToId);
        List<Point> pre = new(), post = new();
        double jx = Snap(at.X), jy = Snap(at.Y);   // fallback if the line can't be resolved
        if (a is not null && b is not null)
        {
            var route = line.Waypoints.Count > 0 ? ManualRoute(a.Value, b.Value, line)
                                                 : Simplify(OrthoRouteAvoiding(a.Value, b.Value, line.FromId, line.ToId, line.Id));
            int k = 0; double best = double.MaxValue;
            for (int i = 0; i < route.Count - 1; i++)
            {
                double d = DistToSegment(at, route[i], route[i + 1]);
                if (d < best) { best = d; k = i; }
            }
            var s = route[k]; var en = route[k + 1];
            if (Math.Abs(s.X - en.X) <= Math.Abs(s.Y - en.Y))   // vertical segment → keep X on the line
            { jx = s.X; jy = Snap(Math.Clamp(at.Y, Math.Min(s.Y, en.Y), Math.Max(s.Y, en.Y))); }
            else                                                // horizontal segment → keep Y on the line
            { jy = s.Y; jx = Snap(Math.Clamp(at.X, Math.Min(s.X, en.X), Math.Max(s.X, en.X))); }

            if (k > 0) pre = route.GetRange(1, k);
            int postStart = k + 1, postCount = Math.Max(0, route.Count - 1 - postStart);
            if (postCount > 0) post = route.GetRange(postStart, postCount);
        }

        var jn = new FlowNode { Kind = FlowNodeKind.Junction, Text = "", Width = 9, Height = 9, X = jx - 4.5, Y = jy - 4.5 };
        _data.Nodes.Add(jn);
        RenderNode(jn);

        _data.Connections.Remove(line);
        if (_connViews.TryGetValue(line.Id, out var vs)) { foreach (var v in vs) _canvas!.Children.Remove(v); _connViews.Remove(line.Id); }
        _data.Connections.Add(new FlowConnection { FromId = line.FromId, ToId = jn.Id, LineColor = _style.LineColor, Label = line.Label,
            Waypoints = pre.Select(p => new BoardWaypoint { X = p.X, Y = p.Y }).ToList() });
        _data.Connections.Add(new FlowConnection { FromId = jn.Id, ToId = line.ToId, LineColor = _style.LineColor,
            Waypoints = post.Select(p => new BoardWaypoint { X = p.X, Y = p.Y }).ToList() });
        _data.Connections.Add(new FlowConnection { FromId = from,        ToId = jn.Id, LineColor = _style.LineColor, Label = fromLabel });

        _connectFromId = null;
        RemoveRubberBand();
        Save();
        RenderAllConnections();
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
