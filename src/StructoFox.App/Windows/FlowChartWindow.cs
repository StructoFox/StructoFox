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
    readonly HashSet<string> _flaggedNodes = new();   // nodes the last structogram conversion couldn't structure
    double   _zoom = 1.0;
    int      _page;        // the flowchart page currently shown/edited (0 = first)
    TextBlock? _pageLabel; // "page x/y" in the toolbar
    bool     _liveDrag;    // a node is being dragged → route attached arrows cheaply (no A*) until release
    bool     _culling;     // re-entrancy guard for viewport culling
    bool     _crossoverHops;   // temporary, non-DIN crossover "bridges" at line crossings (per-window, not saved)
    readonly List<Control> _hopVisuals = new();
    readonly Dictionary<string, List<Point>> _connPts = new();   // last rendered polyline per connection
    Point?   _mousePos;    // last pointer position over the canvas (null when outside) — for paste-at-cursor
    Dictionary<string, Point>? _dragStart;   // start positions of all selected nodes during a multi-drag
    Dictionary<string, Point>? _dragTapStart;        // start anchors of taps whose target moves with the group
    Dictionary<string, List<Point>>? _dragWpStart;   // start waypoints of lines that move as a whole
    Dictionary<string, List<Point>>? _dragRoute;     // start RENDERED route of rigidly-moving lines (pure translate)

    // Does the connection move rigidly with the group (everything it depends on is in the moved set)?
    // node-to-node: both ends; tap: its source plus its target line (recursively, through tap chains).
    bool MovesRigidly(FlowConnection c, HashSet<string> moved, int depth = 0)
    {
        if (depth > 32) return false;
        if (!string.IsNullOrEmpty(c.ToTapConn))
            return moved.Contains(c.FromId)
                && _data.Connections.FirstOrDefault(x => x.Id == c.ToTapConn) is { } tgt
                && MovesRigidly(tgt, moved, depth + 1);
        return moved.Contains(c.FromId) && moved.Contains(c.ToId);
    }

    // Does the TARGET line (this id) move rigidly? Used to decide if a tap's anchor (which lives on that
    // line) should shift with the move.
    bool TargetRigid(string connId, HashSet<string> moved, int depth)
    {
        var t = _data.Connections.FirstOrDefault(x => x.Id == connId);
        return t is not null && MovesRigidly(t, moved, depth);
    }

    Avalonia.Controls.Shapes.Rectangle? _gridRect;   // the tiled grid behind the diagram
    Avalonia.Controls.Shapes.Rectangle? _selRect;    // rubber-band multi-select rectangle
    bool   _selecting;  Point _selStart;             // left-drag selection on empty canvas
    bool   _panning;    Point _panStart;  Vector _panOrigin;   // right-drag canvas pan
    bool   _panMoved;   bool _rightCancelConnect;    // distinguish right-drag (pan) from right-click (cancel)

    FlowConnection? _segConn;  int _segIdx;  List<Point>? _segBasePts;  bool _segHoriz;  Point _segStart;  // segment drag
    string? _segJunctionId;   // if the dragged segment ends at a junction, the junction moves with it
    FlowConnection? _tapDrag;  // a tap line being slid along its target
    FlowConnection? _tineDrag; // a free comb tine whose open tip is being dragged onto a target
    FlowConnection? _toothDrag; // a wired comb tooth being slid along its bar (sets TineOffset)
    Point _toothGrabCur;  int _toothGrabOffset;   // tooth-slide grab anchor (relative drag, no jump)
    FlowConnection? _toothEndDrag; // a wired comb tooth whose target end is being dragged to another side
    FlowConnection? _armedTine; // in connect mode: the specific free tine clicked, to wire on the next target click
    FlowNode? _combDrag;  bool _combVert;   // a Multi-Verzweigung whose comb spine is being pushed nearer/further
    Point _combGrabCur;  int _combGrabGap, _combGrabShift, _combGrabBarShift;   // bar grab anchor (relative drag, no jump)
    FlowNode? _stemDrag;  bool _stemVertexMode;   // stem drag: near diamond = pick vertex, near bar = slide pos
    FlowNode? _stemSegNode; List<Point>? _stemSegBase; int _stemSegIdx; bool _stemSegHoriz; Point _stemSegStart;  // stem bend
    bool _combGapOnly;      // the L bottom-bar grab moves gap + bar-shift; a single comb's bar grab is 2D
    readonly List<Control> _combHandles = new();   // draggable spine handles, redrawn with the connections

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
        bool isNew  = !FlowChartService.Exists(projFolder, key);
        _data       = FlowChartService.Load(projFolder, key);
        if (string.IsNullOrEmpty(_data.Title)) _data.Title = title;
        // A brand-new flowchart gets the user's chosen default header, if any.
        if (isNew) { HeaderTemplateService.ApplyDefault(isPap: true, _data.Style); FlowChartService.Save(projFolder, key, _data); }
        _style      = _data.Style;   // persisted with the diagram
        _snapshot   = Serialize(_data);   // baseline for undo
        // Register any objects already instantiated in this diagram ("x = new Class()") on open, too.
        try { ObjectUsageScanner.RecognizeInstantiations(projFolder, _data.Nodes.Select(n => n.Text)); } catch { }

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
            // Recognize object instantiations ("x = new Class()") written into nodes and register the objects.
            try { ObjectUsageScanner.RecognizeInstantiations(_projFolder, _data.Nodes.Select(n => n.Text)); }
            catch { /* recognition is best-effort; never block a save */ }
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
            // Pin top-left: with an explicit Width, the default Stretch alignment would CENTRE the canvas inside
            // a wider header band, so growing/shrinking the surface would slide every node sideways. Left/Top
            // keeps the origin fixed (only matters when the canvas is narrower than a header band).
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.Parse(_style.BackgroundColor)),  // diagram surface, not app theme
        };
        // Decoration (title/watermark/logo) overlays the canvas IN the scrollable+zoomable content, so it
        // travels with the diagram into print / PDF / image exports instead of floating over the viewport.
        _canvasHost = new Grid();
        _canvasHost.Children.Add(_canvas);
        // Wrap the canvas so zoom can use a LayoutTransform (scales the scrollable extent too). Pin it
        // top-left so that when it's zoomed smaller than the viewport it stays at the top-left corner
        // instead of drifting to the right/bottom.
        _zoomHost = new LayoutTransformControl
        {
            Child = _canvasHost,
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
                // From an armed comb tine the band starts at its open tip; otherwise at the source node centre.
                if (_armedTine is not null && _connPts.TryGetValue(_armedTine.Id, out var ap) && ap.Count > 0)
                    _rubberBand.StartPoint = ap[^1];
                else if (NodeCenter(_connectFromId) is { } c) _rubberBand.StartPoint = c;
                _rubberBand.EndPoint = e.GetPosition(_canvas);
                return;
            }
            if (_combDrag is not null) { DragComb(e.GetPosition(_canvas)); return; }
            if (_stemSegNode is not null && _stemSegBase is not null) { DragStemSeg(e.GetPosition(_canvas)); return; }
            if (_stemDrag is not null) { DragStem(e.GetPosition(_canvas)); return; }
            if (_toothDrag is not null) { DragTooth(e.GetPosition(_canvas)); return; }
            if (_toothEndDrag is not null) { DragToothEnd(e.GetPosition(_canvas)); return; }
            if (_tineDrag is not null) { if (_rubberBand is not null) _rubberBand.EndPoint = e.GetPosition(_canvas); return; }
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
            if (_combDrag is not null) { _combDrag = null; Save(); e.Pointer.Capture(null); return; }
            if (_stemSegNode is not null) { _stemSegNode = null; _stemSegBase = null; Save(); e.Pointer.Capture(null); return; }
            if (_stemDrag is not null) { _stemDrag = null; Save(); e.Pointer.Capture(null); return; }
            if (_toothDrag is not null) { _toothDrag = null; Save(); e.Pointer.Capture(null); return; }
            if (_toothEndDrag is not null) { SettleToothEnd(_toothEndDrag); RenderConnection(_toothEndDrag); _toothEndDrag = null; Save(); e.Pointer.Capture(null); return; }
            if (_tineDrag is not null)
            {
                var tine = _tineDrag; _tineDrag = null;
                RemoveRubberBand(); e.Pointer.Capture(null);
                _ = WireTineByDrag(tine, e.GetPosition(_canvas));
                return;
            }
            if (_tapDrag is not null) { Save(); _tapDrag = null; e.Pointer.Capture(null); return; }
            if (_segConn is not null)
            {
                // A comb tooth (re-anchored to the bar) and a Bemerkung leader (re-anchored to the bracket
                // spine) have a fixed, non-node-edge end, so skip ManualRoute normalisation for them.
                bool fixedEnd = string.IsNullOrEmpty(_segConn.ToTapConn)
                    && (_data.Nodes.FirstOrDefault(n => n.Id == _segConn.FromId)?.Kind is FlowNodeKind.MultiDecision or FlowNodeKind.Annotation
                        || _data.Nodes.FirstOrDefault(n => n.Id == _segConn.ToId)?.Kind == FlowNodeKind.Annotation);
                if (!fixedEnd) NormalizeWaypoints(_segConn);
                RenderConnection(_segConn);
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
        // Fit the surface to the content on open (the canvas starts at a large default size otherwise).
        Dispatcher.UIThread.Post(() => FitCanvas(), DispatcherPriority.Loaded);
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
    // The page a node lives on (defaults to the current page for a free/comb endpoint with no matching node).
    int NodePage(string id) => _data.Nodes.FirstOrDefault(n => n.Id == id)?.Page ?? _page;

    // Highest page index in use (so we know how many pages exist / where to add the next).
    int MaxPage() => _data.Nodes.Count == 0 ? 0 : _data.Nodes.Max(n => n.Page);

    void CullToViewport()
    {
        if (_canvas is null || _culling) return;
        _culling = true;
        try
        {
            RecomputeJunctions();   // derive junction positions from their lines before realizing them
            // _renderAll (export/print): realize the WHOLE page, not just the viewport.
            var vis = _renderAll ? new Rect(-100000, -100000, 200000, 200000) : VisibleRect().Inflate(400);

            foreach (var n in _data.Nodes)
            {
                bool show = n.Page == _page && (vis.Intersects(new Rect(n.X, n.Y, n.Width, n.Height)) || _selected.Contains(n.Id));
                bool realized = _nodeViews.ContainsKey(n.Id);
                if (show && !realized) RenderNode(n);
                else if (!show && realized) { _canvas.Children.Remove(_nodeViews[n.Id]); _nodeViews.Remove(n.Id); }
            }

            // Plain (node→node) lines first, so their points exist for any tap that rides on them.
            foreach (var c in _data.Connections)
            {
                if (!string.IsNullOrEmpty(c.ToTapConn)) continue;
                if (NodePage(c.FromId) != _page) continue;   // only the current page's lines
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
                else if (a is not null && string.IsNullOrEmpty(c.ToId)
                         && _data.Nodes.FirstOrDefault(n => n.Id == c.FromId)?.Kind == FlowNodeKind.MultiDecision)
                    show = vis.Intersects(a.Value.Inflate(300));   // a free comb tine hangs off its diamond (no ToId)
                if (show && !realized) RenderConnection(c);
                else if (!show && realized) { foreach (var v in _connViews[c.Id]) _canvas.Children.Remove(v); _connViews.Remove(c.Id); }
            }
            // Taps: visibility from the source node + the meeting point (ToId is empty, so the node-pair
            // test above would never show them — that left them invisible until something moved).
            foreach (var c in _data.Connections)
            {
                if (string.IsNullOrEmpty(c.ToTapConn)) continue;
                if (NodePage(c.FromId) != _page) continue;   // only the current page's taps
                var a = NodeRect(c.FromId); var tp = TapInfo(c)?.pt;
                bool realized = _connViews.ContainsKey(c.Id);
                bool show = false;
                if (a is not null && tp is not null)
                {
                    var bbox = new Rect(Math.Min(a.Value.X, tp.Value.X), Math.Min(a.Value.Y, tp.Value.Y),
                        Math.Abs(a.Value.X - tp.Value.X) + a.Value.Width, Math.Abs(a.Value.Y - tp.Value.Y) + a.Value.Height);
                    foreach (var w in c.Waypoints) bbox = bbox.Union(new Rect(w.X - 1, w.Y - 1, 2, 2));
                    show = vis.Intersects(bbox);
                }
                if (show && !realized) RenderConnection(c);
                else if (!show && realized) { foreach (var v in _connViews[c.Id]) _canvas.Children.Remove(v); _connViews.Remove(c.Id); }
            }
            if (_crossoverHops) RenderCrossovers();
            RenderTapDots();         // dots where two T-pieces coincide
            RenderCombHandles();     // draggable comb spine handles on Multi-Verzweigung nodes
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
        // Ctrl+A — select every node on the current page.
        if (e.Key == Key.A)
        {
            _selected.Clear();
            foreach (var n in _data.Nodes.Where(n => n.Page == _page)) _selected.Add(n.Id);
            RefreshSelection();
            e.Handled = true;
            return;
        }
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
        // Base lines: both ends are selected nodes.
        var baseConns = _data.Connections
            .Where(c => string.IsNullOrEmpty(c.ToTapConn) && _selected.Contains(c.FromId) && _selected.Contains(c.ToId))
            .ToList();
        var baseIds = baseConns.Select(c => c.Id).ToHashSet();
        // Taps: source node is selected AND they land on a base line that's being copied — so a T-piece
        // travels with its target. (Manually-corrected taps were lost before: they were never matched.)
        var taps = _data.Connections
            .Where(c => !string.IsNullOrEmpty(c.ToTapConn) && _selected.Contains(c.FromId) && baseIds.Contains(c.ToTapConn))
            .ToList();
        var payload = new ClipPayload
        {
            Nodes       = _data.Nodes.Where(n => _selected.Contains(n.Id)).ToList(),
            Connections = baseConns.Concat(taps).ToList(),
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
            n.Page = _page;   // paste onto the page being viewed
            if (IsOffPage(n)) { n.OffPageEntry = false; n.OffPagePair = Guid.NewGuid().ToString("N")[..8]; n.Text = NextOffPageLabel(); }  // a pasted off-page connector is a fresh exit
            _data.Nodes.Add(n);
            _selected.Add(n.Id);
            RenderNode(n);
        }
        // Base (node→node) lines first, remembering old→new ids so taps can re-point at the copies.
        var connMap = new Dictionary<string, string>();
        foreach (var c in p.Connections.Where(c => string.IsNullOrEmpty(c.ToTapConn)))
        {
            if (!idMap.TryGetValue(c.FromId, out var f) || !idMap.TryGetValue(c.ToId, out var t)) continue;
            var oldId = c.Id;
            c.Id = Guid.NewGuid().ToString("N")[..8];
            c.FromId = f; c.ToId = t;
            foreach (var w in c.Waypoints) { w.X += offX; w.Y += offY; }
            connMap[oldId] = c.Id;
            _data.Connections.Add(c);
            RenderConnection(c);
        }
        // Then taps: re-point onto the copied target line, offset their anchor + waypoints.
        foreach (var c in p.Connections.Where(c => !string.IsNullOrEmpty(c.ToTapConn)))
        {
            if (!idMap.TryGetValue(c.FromId, out var f) || !connMap.TryGetValue(c.ToTapConn, out var nt)) continue;
            c.Id = Guid.NewGuid().ToString("N")[..8];
            c.FromId = f; c.ToTapConn = nt;
            c.ToTapX += offX; c.ToTapY += offY;
            foreach (var w in c.Waypoints) { w.X += offX; w.Y += offY; }
            _data.Connections.Add(c);
            RenderConnection(c);
        }
        RenderTapDots();
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
            if (n.Page == _page && r.Intersects(new Rect(n.X, n.Y, n.Width, n.Height))) _selected.Add(n.Id);
        RefreshSelection();
    }

    Grid? _root;
    Grid? _canvasHost;   // wraps the canvas + decoration inside the zoom/scroll content

    // Rebuilds the decoration (title / info field / watermark / logo) around the canvas, INSIDE the scroll/zoom
    // content, so it travels into print / PDF / image exports; edge decorations reserve an empty band.
    // When true, CullToViewport realizes the whole page (used by RenderPageBitmap for export/print).
    bool _renderAll;

    /// <summary>Renders one page's full diagram (all nodes/connections + decoration/legend), sized to content, to
    /// a bitmap at <paramref name="scale"/> (1.0 = 96 DPI). Reuses the exact editor rendering (RebuildAll +
    /// DiagramDecor), so the output is identical to the on-screen diagram. Used by the print composer.</summary>
    public Avalonia.Media.Imaging.RenderTargetBitmap? RenderPageBitmap(int page = 0, double scale = 1.0)
    {
        if (_canvas is null || _canvasHost is null) return null;
        var savedPage = _page; var savedW = _canvas.Width; var savedH = _canvas.Height; var savedRA = _renderAll;
        _page = page; _renderAll = true;
        try
        {
            // Size the surface to the page's content bounding box (a small margin all round).
            double w = 60, h = 60;
            foreach (var n in _data.Nodes.Where(n => n.Page == page))
            { w = Math.Max(w, n.X + n.Width + 30); h = Math.Max(h, n.Y + n.Height + 30); }
            _canvas.Width = w; _canvas.Height = h;

            RebuildAll();   // grid + all nodes/connections (unculled) + decoration, composed into _canvasHost

            var host = _canvasHost;
            host.Measure(Size.Infinity);
            var size = host.DesiredSize;
            if (size.Width < 1 || size.Height < 1) size = new Size(w, h);
            host.Arrange(new Rect(size));

            var px = new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scale)),
                                   Math.Max(1, (int)Math.Ceiling(size.Height * scale)));
            var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(px, new Vector(96 * scale, 96 * scale));
            rtb.Render(host);
            return rtb;
        }
        catch { return null; }
        finally { _page = savedPage; _renderAll = savedRA; _canvas.Width = savedW; _canvas.Height = savedH; }
    }

    /// <summary>Renders JUST the diagram (no decoration), with a TRANSPARENT background, tightly cropped to the
    /// node content — so it sits on the print page with a minimal footprint and can overlap freely. Scale 1.0 = 96 DPI.</summary>
    public Avalonia.Media.Imaging.RenderTargetBitmap? RenderDiagramOnly(int page = 0, double scale = 1.0)
    {
        if (_canvas is null) return null;
        var savedPage = _page; var savedW = _canvas.Width; var savedH = _canvas.Height; var savedRA = _renderAll; var savedBg = _canvas.Background;
        _page = page; _renderAll = true;
        try
        {
            var nodes = _data.Nodes.Where(n => n.Page == page).ToList();
            if (nodes.Count == 0) return null;

            // Route everything first (line routing is independent of the canvas size), which populates _connPts —
            // the actual polyline points of every connection on this page.
            RebuildAll();
            _canvas.Background = Brushes.Transparent;   // no white fill — the PAP background is see-through

            // Determine the TRUE content bounds from the real coordinates: node rects UNION every routed
            // connection point (lines, connectors, taps). Margin only covers arrowheads/labels sitting a few px
            // beyond the bare geometry — so the export sits tight on the content and moves cleanly on the page.
            double minX = double.MaxValue, minY = double.MaxValue, maxX = 0, maxY = 0;
            foreach (var n in nodes) { minX = Math.Min(minX, n.X); minY = Math.Min(minY, n.Y); maxX = Math.Max(maxX, n.X + n.Width); maxY = Math.Max(maxY, n.Y + n.Height); }
            foreach (var pts in _connPts.Values)
                foreach (var p in pts) { minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); }
            const double m = 14;
            double cx = Math.Max(0, minX - m), cy = Math.Max(0, minY - m);
            double cw = (maxX + m) - cx, ch = (maxY + m) - cy;

            // Size the canvas to exactly cover the content (max edge + margin), then render — nothing is clipped
            // because every point lies within [0 .. max]. No oversized scratch surface.
            _canvas.Width = maxX + m; _canvas.Height = maxY + m;
            _canvas.Measure(new Size(_canvas.Width, _canvas.Height));
            _canvas.Arrange(new Rect(0, 0, _canvas.Width, _canvas.Height));

            var full = new Avalonia.Media.Imaging.RenderTargetBitmap(
                new PixelSize(Math.Max(1, (int)Math.Ceiling(_canvas.Width * scale)), Math.Max(1, (int)Math.Ceiling(_canvas.Height * scale))),
                new Vector(96 * scale, 96 * scale));
            full.Render(_canvas);
            // The crop is DPI-NEUTRAL (96 DPI): its DIP size equals its pixel size. This matters when the bitmap is
            // embedded into another DPI-scaled render (the print export) — a 96*scale DPI here would multiply with
            // the outer scale and blow the diagram up. With 96 DPI the caller controls the size purely by pixels.
            var crop = new Avalonia.Media.Imaging.RenderTargetBitmap(
                new PixelSize(Math.Max(1, (int)Math.Ceiling(cw * scale)), Math.Max(1, (int)Math.Ceiling(ch * scale))),
                new Vector(96, 96));
            using (var ctx = crop.CreateDrawingContext())
                ctx.DrawImage(full, new Rect(cx * scale, cy * scale, cw * scale, ch * scale), new Rect(0, 0, cw * scale, ch * scale));
            full.Dispose();
            return crop;
        }
        catch { return null; }
        finally { _page = savedPage; _renderAll = savedRA; _canvas.Width = savedW; _canvas.Height = savedH; _canvas.Background = savedBg; }
    }

    /// <summary>The decoration positions present on this diagram (title block / info / legend), so the composer can
    /// create one movable item per position.</summary>
    public List<DecorPos> DecorPositions() => DiagramDecor.EnumeratePositions(_data.Title, _style);

    /// <summary>Renders ONE decoration block (the merged title/info/legend at <paramref name="pos"/>) to its own
    /// bitmap, so it can be placed as an independent movable item. Null if that position is empty.</summary>
    public Avalonia.Media.Imaging.RenderTargetBitmap? RenderDecorPiece(DecorPos pos, double scale = 1.0)
    {
        var piece = DiagramDecor.EnumeratePieces(_data.Title, _style).FirstOrDefault(p => p.Pos == pos).Ctrl;
        if (piece is null) return null;
        piece.Measure(Size.Infinity);
        var size = piece.DesiredSize;
        if (size.Width < 1 || size.Height < 1) return null;
        piece.Arrange(new Rect(size));
        var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(
            new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scale)), Math.Max(1, (int)Math.Ceiling(size.Height * scale))),
            new Vector(96 * scale, 96 * scale));
        rtb.Render(piece);
        return rtb;
    }

    void RefreshDecor()
    {
        if (_canvasHost is null || _canvas is null) return;
        (_canvas.Parent as Panel)?.Children.Remove(_canvas);   // detach from the previous composition
        _canvasHost.Children.Clear();
        _canvasHost.Children.Add(DiagramDecor.Compose(_canvas, _data.Title, _style, () => _ = OpenDecor()));
        // Re-attach the scroll content to clear the zoom wrapper + presenter size cache, so the canvas snaps
        // back when the header (and thus the composed size) shrinks.
        if (_scroll is not null && _zoomHost is { } zh) { _scroll.Content = null; _scroll.Content = zh; }
    }

    // Opens the decoration dialog (title / watermark / logo) and re-applies on OK.
    async Task OpenDecor()
    {
        var newTitle = await DiagramDecorDialog.Show(this, _data.Title, _style, null, ProjectService.DisplayName(_projFolder));
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

        var dec = Cat(Loc.S("Flow_Decision"));
        dec.Items.Add(MI(Loc.S("Flow_Decision"),      () => AddNode(FlowNodeKind.Decision)));
        dec.Items.Add(MI(Loc.S("Flow_MultiDecision"), () => AddNode(FlowNodeKind.MultiDecision)));
        shapeMenu.Items.Add(dec);

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

        var annCat = Cat(Loc.S("Flow_Annotation"));   // "Bemerkung" group — the standard sits first
        annCat.Items.Add(MI(Loc.S("Flow_Annotation"), () => AddNode(FlowNodeKind.Annotation)));
        annCat.Items.Add(MI(Loc.S("Flow_Note"),       () => AddNode(FlowNodeKind.Comment)));
        shapeMenu.Items.Add(annCat);

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

        // Page switcher (multi-page PAP via off-page connectors).
        row.Children.Add(new Border { Width = 12 });
        var pagePrev = TBtn("‹", Loc.S("Flow_PagePrev")); pagePrev.Click += (_, _) => GoToPage(_page - 1);
        _pageLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0) };
        Ui.Theme(_pageLabel, TextBlock.ForegroundProperty, "SidebarTextBrush");
        var pageNext = TBtn("›", Loc.S("Flow_PageNext")); pageNext.Click += (_, _) => GoToPage(_page + 1);
        row.Children.Add(pagePrev); row.Children.Add(_pageLabel); row.Children.Add(pageNext);
        UpdatePageLabel();

        // "Options" gathers the set-once-and-forget surface settings: colours, decoration, zoom reset and
        // the grid (visibility / colour / style / opacity / snap) — rarely touched while actually drawing.
        var viewBtn = TBtn(Loc.S("Flow_View"), Loc.S("Flow_ViewTip"));
        viewBtn.Flyout = BuildViewFlyout();
        row.Children.Add(viewBtn);

        // Project authoring language — drives the node-editor autocomplete's signature syntax. Per project.
        row.Children.Add(new Border { Width = 12 });
        var langLabel = new TextBlock { Text = Loc.S("Flow_LangLabel"), VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 4, 0) };
        Ui.Theme(langLabel, TextBlock.ForegroundProperty, "SidebarTextBrush");
        row.Children.Add(langLabel);
        row.Children.Add(BuildLanguageCombo());

        return bar;
    }

    // Languages offered as the project's authoring language (autocomplete signature syntax). Shared with the
    // new-project dialog so both pickers list the same set.
    internal static readonly (ExportLanguage Lang, string Label)[] AuthorLanguages =
    {
        (ExportLanguage.CSharp, "C#"), (ExportLanguage.Cpp, "C++"), (ExportLanguage.C, "C"), (ExportLanguage.Java, "Java"),
        (ExportLanguage.Python, "Python"), (ExportLanguage.TypeScript, "TypeScript"), (ExportLanguage.JavaScript, "JavaScript"),
        (ExportLanguage.Go, "Go"), (ExportLanguage.Rust, "Rust"), (ExportLanguage.Kotlin, "Kotlin"), (ExportLanguage.Swift, "Swift"),
        (ExportLanguage.Php, "PHP"),
    };

    // The per-project language picker; loads the current choice and persists a change to ProjectInfo.
    ComboBox BuildLanguageCombo()
    {
        var combo = new ComboBox { MinWidth = 120, VerticalAlignment = VerticalAlignment.Center };
        Ui.Theme(combo, TemplatedControl.BackgroundProperty,  "InputBgBrush");
        Ui.Theme(combo, TemplatedControl.ForegroundProperty,  "SidebarTextBrush");
        Ui.Theme(combo, TemplatedControl.BorderBrushProperty, "ControlBorderBrush");
        ToolTip.SetTip(combo, Loc.S("Flow_LangTip"));
        foreach (var (_, label) in AuthorLanguages) combo.Items.Add(label);
        combo.SelectedIndex = Math.Max(0, Array.FindIndex(AuthorLanguages, x => x.Lang == ProjectLanguage()));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0) return;
            var info = ProjectService.Load(_projFolder) ?? ProjectService.Create(_projFolder, new DirectoryInfo(_projFolder).Name);
            info.Language = AuthorLanguages[combo.SelectedIndex].Lang.ToString();
            ProjectService.Save(_projFolder, info);
        };
        return combo;
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
        // In the sketchbook a PAP and a structogram under the same id can't both show (the home lists one entry per
        // sketch by its Type), so a conversion there gets its OWN structogram sketch entry instead of overwriting.
        bool sketchbook = string.Equals(
            System.IO.Path.GetFullPath(_projFolder), System.IO.Path.GetFullPath(SketchbookService.Root),
            StringComparison.OrdinalIgnoreCase);

        if (!sketchbook && StructogramService.Exists(_projFolder, _key))
        {
            var res = await MessageDialog.Show(this,
                Loc.S("Flow_ToStructogramOverwrite"), Loc.S("Flow_ToStructogramTitle"), DialogButtons.YesNo);
            if (res != DialogResult.Yes) return;
        }

        var title = string.IsNullOrEmpty(_data.Title) ? Loc.S("Common_Untitled") : _data.Title;
        var sd = StructogramConverter.Convert(_data, title, out var unstructured);

        // Mark the unconvertible nodes orange in the PAP — that's where they can be fixed (marking the
        // half-built structogram wouldn't help). Clears whatever was flagged before.
        _flaggedNodes.Clear();
        foreach (var id in unstructured) _flaggedNodes.Add(id);
        RebuildAll();

        if (_flaggedNodes.Count > 0)
        {
            // Don't write/open a misleading partial structogram — point the user at the PAP instead.
            await MessageDialog.Show(this, Loc.S("Flow_ToStructogramPartial"), Loc.S("Flow_ToStructogramTitle"));
            return;
        }

        // Sketchbook: register a new structogram sketch so it appears on the home. Project: keep the shared function
        // key (the structogram stays linked to its PAP and shows under the entity).
        string targetKey = sketchbook ? SketchbookService.Create(SketchType.Structogram, title).Id : _key;
        StructogramService.Save(_projFolder, targetKey, sd);
        DiagramWindows.OpenOrActivate(DiagramWindows.StructId(_projFolder, targetKey),
            () => new StructogramWindow(_projFolder, targetKey, title, _themePath));
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
        // One entry point per flowchart: a PAP maps to exactly one function for code generation, so a second
        // Start node is refused (multiple diagrams per canvas is a future presentation/export feature).
        if (kind == FlowNodeKind.Start && _data.Nodes.Any(n => n.Kind == FlowNodeKind.Start))
        {
            _ = MessageDialog.Show(this, Loc.S("Flow_OnlyOneStart"), Loc.S("Flow_Start"));
            return;
        }

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
            Page   = _page,   // new nodes belong to the page being edited
        };
        // An off-page connector (Folgeseite) is an EXIT: pair id + a unique label so its entry can mirror it.
        if (kind == FlowNodeKind.Connector && sym == FlowSymbol.OffPageConnector)
        {
            node.OffPagePair = Guid.NewGuid().ToString("N")[..8];
            node.Text = NextOffPageLabel();
        }
        _data.Nodes.Add(node);
        // A Multi-Verzweigung is born with a small comb of free tines (cases), ready to be wired to targets.
        if (kind == FlowNodeKind.MultiDecision) for (int k = 0; k < 3; k++) AddTine(node);
        Save();
        RenderNode(node);
        if (kind == FlowNodeKind.MultiDecision) RenderAllConnections();
        if (_mode != EditMode.Select) SetMode(EditMode.Select);   // a fresh node is ready to place, not delete
    }

    // Adds one free comb tine (an outgoing connection with no target yet) to a Multi-Verzweigung, with a
    // unique default case label. The tine hangs in the air until the user connects it to a target.
    FlowConnection AddTine(FlowNode node)
    {
        var baseLbl = Loc.S("Flow_CaseDefault");
        int n = _data.Connections.Count(c => c.FromId == node.Id) + 1;
        string lbl;
        do { lbl = $"{baseLbl} {n++}"; }
        while (_data.Connections.Any(c => c.FromId == node.Id &&
               string.Equals(c.Label.Trim(), lbl, StringComparison.OrdinalIgnoreCase)));
        var tine = new FlowConnection { FromId = node.Id, Label = lbl, LineColor = _style.LineColor };
        _data.Connections.Add(tine);
        return tine;
    }

    // A diamond branch — either a binary Decision or a multi-way Multi-Verzweigung (switch/case). Both
    // share the norm-compliant diamond shape, colours, branch labelling and default size; only the number
    // of outgoing tines and the comb layout differ.
    static bool IsDecision(FlowNodeKind k) => k is FlowNodeKind.Decision or FlowNodeKind.MultiDecision;

    // The standard (and minimum) size per node kind/symbol — shared by AddNode and the resize clamp,
    // so a symbol never scales below the dimensions it would be created at.
    static (double w, double h) DefaultNodeSize(FlowNodeKind kind, FlowSymbol sym)
    {
        bool offPage = sym == FlowSymbol.OffPageConnector;
        double w = kind == FlowNodeKind.Junction ? 9 : offPage ? 50 : kind == FlowNodeKind.Connector ? 46 : 140;   // diamonds match the others, so their L/R vertices stay grid-aligned
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
        FlowNodeKind.Decision      => Loc.S("Flow_DefDecision"),
        FlowNodeKind.MultiDecision => Loc.S("Flow_DefDecision"),
        FlowNodeKind.InputOutput => Loc.S("Flow_DefIO"),
        FlowNodeKind.Subroutine  => Loc.S("Flow_DefCall"),
        FlowNodeKind.Comment     => Loc.S("Flow_DefNote"),
        FlowNodeKind.Annotation  => Loc.S("Flow_DefAnnotation"),
        FlowNodeKind.Connector   => Loc.S("Flow_DefConnector"),
        FlowNodeKind.Junction    => "",
        _                        => Loc.S("Flow_DefStep"),
    };

    // Builds one node's shape + label inside a draggable, selectable container on the canvas.
    void RenderNode(FlowNode node)
    {
        if (node.Page != _page) return;   // only the current page is drawn
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
                FlowNodeKind.Decision or FlowNodeKind.MultiDecision => DiamondShape(node.Width, node.Height, fill, stroke),
                FlowNodeKind.InputOutput => ParallelogramShape(node.Width, node.Height, fill, stroke),
                FlowNodeKind.Subroutine  => SubroutineShape(fill, stroke),
                FlowNodeKind.Comment     => CommentShape(fill, stroke),
                FlowNodeKind.Annotation  => AnnotationShape(fill, stroke, node.Mirrored),
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

        // Marked orange when the last structogram conversion couldn't structure this node — so the user
        // sees WHERE to fix it in the PAP itself (clears on the next edit/convert).
        if (_flaggedNodes.Contains(node.Id))
        {
            var warn = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x7F, 0x17)), BorderThickness = new(3),
                CornerRadius = new(4), IsHitTestVisible = false, Margin = new(-3),
            };
            ToolTip.SetTip(warn, Loc.S("Flow_ToStructogramNodeTip"));
            inner.Children.Add(warn);
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
            if (e.ClickCount >= 2 && !ConnectMode)
            {
                if (node.Kind == FlowNodeKind.Subroutine) ShowChartFlow(node);
                else if (IsOffPage(node) && !node.OffPageEntry) OpenOffPage(node);   // exit → jump to its page
                else if (!node.OffPageEntry) _ = EditNodeText(node, label);          // (entry label is read-only)
                e.Handled = true; return;
            }

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
                var movedSet = _dragStart.Keys.ToHashSet();
                // Snapshot the geometry that should travel rigidly with the group (like paste's offset):
                // tap anchors whose target line moves, and waypoints of lines that move as a whole.
                _dragTapStart = _data.Connections
                    .Where(c => !string.IsNullOrEmpty(c.ToTapConn) && TargetRigid(c.ToTapConn, movedSet, 0))
                    .ToDictionary(c => c.Id, c => new Point(c.ToTapX, c.ToTapY));
                _dragWpStart = _data.Connections
                    .Where(c => c.Waypoints.Count > 0 && MovesRigidly(c, movedSet))
                    .ToDictionary(c => c.Id, c => c.Waypoints.Select(w => new Point(w.X, w.Y)).ToList());
                // Rigidly-moving lines keep their EXACT shape: snapshot their rendered route, then just
                // translate it (no re-routing) — so nothing "corrects" or springs while moving.
                _dragRoute = _data.Connections
                    .Where(c => MovesRigidly(c, movedSet) && _connPts.ContainsKey(c.Id))
                    .ToDictionary(c => c.Id, c => _connPts[c.Id].ToList());
            }
            // Snap the dragged node's CENTRE to the grid; shift the rest of the selection by the same delta.
            var nx = SnapCentered(Math.Max(0, pt.X - offset.X), node.Width);
            var ny = SnapCentered(Math.Max(0, pt.Y - offset.Y), node.Height);
            double dx = nx - (_dragStart!.TryGetValue(node.Id, out var s0) ? s0.X : node.X);
            double dy = ny - (_dragStart!.TryGetValue(node.Id, out s0) ? s0.Y : node.Y);
            // 1) Move EVERY selected node to its final spot first (no per-node re-routing).
            foreach (var (id, start) in _dragStart)
            {
                var nd = _data.Nodes.FirstOrDefault(n => n.Id == id);
                if (nd is null) continue;
                nd.X = start.X + dx; nd.Y = start.Y + dy;
                if (_nodeViews.TryGetValue(id, out var v)) { Canvas.SetLeft(v, nd.X); Canvas.SetTop(v, nd.Y); }
                GrowCanvasFor(nd.X, nd.Y, nd.Width, nd.Height);
            }
            // 2) Rigidly-moving lines: just translate their snapshot route by the delta (no re-routing).
            TranslateRigid(dx, dy);
            // 3) Only the boundary lines (one end fixed) actually re-route — and after the rigid routes are
            //    in place, so taps reading them see current points. Render once.
            RerouteAfterMove(_dragStart.Keys.ToHashSet());
            e.Handled = true;
        };
        container.PointerReleased += (_, e) =>
        {
            if (!pressed) return;
            pressed = false; e.Pointer.Capture(null);
            if (dragging)
            {
                dragging = false; _liveDrag = false;
                var movedSet = _dragStart?.Keys.ToHashSet() ?? new HashSet<string> { node.Id };
                // Total delta of the move (from a moved node's start), to commit the rigid geometry.
                double tdx = 0, tdy = 0;
                if (_dragStart is not null && _dragStart.TryGetValue(node.Id, out var st)) { tdx = node.X - st.X; tdy = node.Y - st.Y; }
                ShiftDraggedGeometry(tdx, tdy);   // commit rigid waypoints + tap anchors to their final spot
                TranslateRigid(tdx, tdy);         // and leave the rigid routes as the exact translated shape
                if (node.Kind == FlowNodeKind.Junction) TrySpliceJunction(node);
                RerouteAfterMove(movedSet);       // boundary lines re-route (rigid still excluded via _dragRoute)
                _dragStart = null; _dragTapStart = null; _dragWpStart = null; _dragRoute = null;
                FitCanvas(keepPosition: true);   // fit the surface to content but leave the move where the user put it
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
            GrowCanvasFor(n.X, n.Y, n.Width, n.Height);   // grow live like a move (no jump-to-fit on release)
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
            FitCanvas(keepPosition: true); RenderCrossovers(); Save();   // leave content where the user sized it (like a move)
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
            if (string.IsNullOrEmpty(node.RefId))
            {
                // Not linked yet → offer linking from the menu (previously only reachable via double-click).
                var link = new MenuItem { Header = Loc.S("Sub_Link") };
                link.Click += (_, _) => _ = RelinkSubroutine(node);
                cm.Items.Add(link);
            }
            else
            {
                var chart = new MenuItem { Header = Loc.S("Struct_ShowChart") };
                chart.Click += (_, _) => ShowChartFlow(node);
                cm.Items.Add(chart);
                // Re-point it at a different target (or adjust the call form) without redrawing the node.
                var relink = new MenuItem { Header = Loc.S("Sub_Relink") };
                relink.Click += (_, _) => _ = RelinkSubroutine(node);
                cm.Items.Add(relink);
            }
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

        // A Bemerkung can be mirrored: bracket + connection point flip to the other side.
        if (node.Kind == FlowNodeKind.Annotation)
        {
            var flip = new MenuItem { Header = Loc.S("Flow_MirrorAnnotation") };
            Ui.Theme(flip, MenuItem.ForegroundProperty, "SidebarTextBrush");
            flip.Click += (_, _) => FlipAnnotation(node);
            cm.Items.Add(flip);
        }

        // Multi-Verzweigung: choose which comb(s) the tines hang on and how far apart they sit.
        if (node.Kind == FlowNodeKind.MultiDecision)
        {
            var combMenu = new MenuItem { Header = Loc.S("Flow_CombDir") };
            Ui.Theme(combMenu, MenuItem.ForegroundProperty, "SidebarTextBrush");
            void Dir(string label, CombDirection d)
            {
                var mi = new MenuItem { Header = (node.CombDir == d ? "● " : "") + label };
                Ui.Theme(mi, MenuItem.ForegroundProperty, "SidebarTextBrush");
                mi.Click += (_, _) =>
                {
                    if (node.CombDir != d)
                    {
                        node.CombDir = d;
                        // Reset the (direction-specific) layout so the new comb lays out cleanly.
                        node.CombShift = 0; node.CombBarShift = 0; node.CombStemPos = 0; node.CombStemVertex = -1;
                        node.CombStemWaypoints.Clear();
                        foreach (var c in _data.Connections.Where(c => c.FromId == node.Id))
                        { c.TineOffset = 0; c.TineTargetSet = false; c.Waypoints.Clear(); }
                    }
                    Save(); RenderAllConnections();
                };
                combMenu.Items.Add(mi);
            }
            Dir(Loc.S("Flow_CombBottom"), CombDirection.Bottom);
            Dir(Loc.S("Flow_CombRight"),  CombDirection.Right);
            Dir(Loc.S("Flow_CombBoth"),   CombDirection.Both);
            cm.Items.Add(combMenu);

            var spacing = new MenuItem { Header = Loc.S("Flow_TineSpacing") };
            spacing.Click += async (_, _) =>
            {
                double gg = _data.GridSize >= 4 ? _data.GridSize : 10;
                var baseComb = node.CombDir == CombDirection.Right ? CombDirection.Right : CombDirection.Bottom;
                int eff = node.TineSpacing > 0 ? node.TineSpacing : (int)Math.Round(CombStep(node, baseComb, gg) / gg);
                var s = await PromptDialog.Show(this, Loc.S("Flow_TineSpacing"), eff.ToString());
                if (int.TryParse(s, out var v) && v >= 0) { node.TineSpacing = v; Save(); UpdateConnectionsFor(node.Id); }
            };
            cm.Items.Add(spacing);

            var addTine = new MenuItem { Header = Loc.S("Flow_AddTine") };
            addTine.Click += (_, _) => { AddTine(node); Save(); RenderAllConnections(); };
            cm.Items.Add(addTine);

            var resetStem = new MenuItem { Header = Loc.S("Flow_ResetStem") };
            Ui.Theme(resetStem, MenuItem.ForegroundProperty, "SidebarTextBrush");
            resetStem.Click += (_, _) =>
            {
                node.CombStemWaypoints.Clear(); node.CombStemPos = 0; node.CombStemVertex = -1;
                Save(); RenderAllConnections();
            };
            cm.Items.Add(resetStem);

            var remTine = new MenuItem { Header = Loc.S("Flow_RemoveTine") };
            remTine.Click += (_, _) =>
            {
                // Prefer dropping a still-free tine; otherwise the last one added.
                var drop = _data.Connections.LastOrDefault(c => c.FromId == node.Id
                               && string.IsNullOrEmpty(c.ToId) && string.IsNullOrEmpty(c.ToTapConn))
                           ?? _data.Connections.LastOrDefault(c => c.FromId == node.Id);
                if (drop is not null) DeleteConnection(drop);
            };
            cm.Items.Add(remTine);
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

    // Mirrors a Bemerkung (Annotation): bracket spine + its connection point flip to the other side.
    void FlipAnnotation(FlowNode node)
    {
        node.Mirrored = !node.Mirrored;
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
        // An off-page ENTRY mirrors its exit — its label is read-only.
        if (node.OffPageEntry) { _ = MessageDialog.Show(this, Loc.S("Flow_EntryReadOnly"), Loc.S("Flow_SymOffPage")); return; }

        var before = node.Text;
        var locals = LocalVariableScanner.TypedFromNodeTexts(AncestorNodeTexts(node));
        if (!await NodeTextDialog.Edit(this, node, _projFolder, locals, ProjectLanguage())) return;

        // An off-page EXIT must keep a unique label and push it to its entry.
        if (IsOffPage(node) && !node.OffPageEntry)
        {
            if (_data.Nodes.Any(n => IsOffPage(n) && !n.OffPageEntry && n.Id != node.Id
                                     && string.Equals(n.Text.Trim(), node.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                node.Text = before;   // revert the duplicate
                _ = MessageDialog.Show(this, Loc.S("Flow_OffPageDup"), Loc.S("Flow_SymOffPage"));
            }
            else SyncOffPageEntry(node);
        }
        ApplyTextFormat(label, node);
        Save();
    }

    // Texts of every node that can reach <paramref name="node"/> via connections (its ancestors), so the editor's
    // autocomplete only suggests variables introduced EARLIER in the flow. Falls back to all other nodes when the
    // node has no incoming path yet (freshly placed / unconnected).
    IEnumerable<string> AncestorNodeTexts(FlowNode node)
    {
        var byId  = _data.Nodes.ToDictionary(n => n.Id);
        var preds = _data.Connections
            .Where(c => !string.IsNullOrEmpty(c.FromId) && !string.IsNullOrEmpty(c.ToId))
            .ToLookup(c => c.ToId, c => c.FromId);

        var seen  = new HashSet<string>();
        var queue = new Queue<string>(preds[node.Id]);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id)) continue;
            foreach (var f in preds[id]) queue.Enqueue(f);
        }
        return seen.Count == 0
            ? _data.Nodes.Where(n => n.Id != node.Id).Select(n => n.Text)
            : seen.Where(byId.ContainsKey).Select(id => byId[id].Text);
    }

    // The project's authoring language (for autocomplete signature syntax); C# when unset/unknown.
    ExportLanguage ProjectLanguage() =>
        Enum.TryParse<ExportLanguage>(ProjectService.Load(_projFolder)?.Language, out var l) ? l : ExportLanguage.CSharp;

    // Applies a node's text and formatting (font, size, weight, style, decorations) to its label.
    // A zero-width space after each dot gives the layout a preferred break point, so long qualified names
    // (Namespace.Class.method) wrap AT the dots instead of mid-word.
    static string BreakAtDots(string s) => s.Replace(".", "." + (char)0x200B);

    static void ApplyTextFormat(TextBlock label, FlowNode node)
    {
        label.Text       = BreakAtDots(node.Text);
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

        var fromId = _connectFromId;
        _connectFromId = null;
        RemoveRubberBand();
        var src = _data.Nodes.FirstOrDefault(n => n.Id == fromId);

        // A Multi-Verzweigung doesn't make a brand-new line — it WIRES a comb tine to the target: the one the
        // user armed by clicking its handle, else the next still-free tine (growing one if all are taken),
        // keeping its mandatory unique case label.
        if (src is { Kind: FlowNodeKind.MultiDecision })
        {
            var armed = _armedTine; _armedTine = null;
            var free = armed ?? _data.Connections.FirstOrDefault(c => c.FromId == src.Id
                           && string.IsNullOrEmpty(c.ToId) && string.IsNullOrEmpty(c.ToTapConn));
            var lbl = await PromptBranchLabel(src, free?.Label ?? "", free?.Id);
            if (lbl is null) { RenderAllConnections(); return; }   // cancelled: leave the comb untouched
            var tine = free ?? AddTine(src);
            tine.ToId = nodeId;
            tine.Label = lbl;
            Save();
            RenderAllConnections();
            RefreshJunctions();
            return;
        }

        var conn = new FlowConnection { FromId = fromId, ToId = nodeId, LineColor = _style.LineColor };
        // A plain decision gets an optional branch label (yes/no, etc.).
        if (src is not null && IsDecision(src.Kind))
            conn.Label = await PromptBranchLabel(src, "", conn.Id) ?? "";

        _data.Connections.Add(conn);
        Save();
        RenderConnection(conn);
        RefreshJunctions();   // a new line onto a junction may turn it into a crossing → show its dot
    }

    // Prompts for a branch/case label on a line leaving a (multi-)decision. For a Multi-Verzweigung the
    // label is mandatory and must be unique among that node's other outgoing tines — re-prompts on a blank
    // or duplicate. Returns the chosen label, or null if the user cancelled.
    async Task<string?> PromptBranchLabel(FlowNode src, string current, string? excludeConnId)
    {
        bool multi = src.Kind == FlowNodeKind.MultiDecision;
        while (true)
        {
            var entered = (await PromptDialog.Show(this, Loc.S("Flow_BranchPrompt"), current))?.Trim();
            if (entered is null) return null;                       // cancelled
            if (!multi) return entered;                             // plain decision: any label (incl. empty) is fine
            bool dup = entered.Length == 0 || _data.Connections.Any(c =>
                c.Id != excludeConnId && c.FromId == src.Id &&
                string.Equals(c.Label.Trim(), entered, StringComparison.OrdinalIgnoreCase));
            if (!dup) return entered;
            await MessageDialog.Show(this, Loc.S("Flow_CaseLabelDup"), Loc.S("Flow_MultiDecision"));
            current = entered;
        }
    }

    // Renders every saved connection (used once after nodes are laid out).
    void RenderAllConnections()
    {
        // Render plain (node-ending) lines first so their points exist, then the taps that ride on them.
        foreach (var c in _data.Connections) if (string.IsNullOrEmpty(c.ToTapConn)) RenderConnection(c);
        foreach (var c in _data.Connections) if (!string.IsNullOrEmpty(c.ToTapConn)) RenderConnection(c);
        RenderTapDots();
        RenderCombHandles();
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
        // Source exit stays at the edge CENTRE (DIN style — outgoing flow leaves an element centred); the
        // target entry SLIDES to where the line actually arrives so the arrowhead meets the edge head-on.
        var pts = new List<Point> { EdgeMid(a, wps[0]) };
        pts.AddRange(wps);
        pts.Add(EdgeSlide(b, wps[^1]));
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

    // Like EdgeMid, but the attach point SLIDES ALONG the facing edge to line up with the approaching
    // point (clamped within the edge, kept a small inset from the corners). So a manually-routed end meets
    // the symbol perpendicularly right where the line arrives — instead of always snapping to the edge
    // centre, which drew a slanted final approach and hid the arrowhead behind the symbol when the stub
    // was dragged off-centre. The chosen edge is still the one facing the point (so the head stays on the
    // outside), and a centred approach still lands dead-centre, exactly as before.
    static Point EdgeSlide(Rect r, Point toward)
    {
        double inset = Math.Max(0, Math.Min(6, Math.Min(r.Width, r.Height) / 2 - 1));
        double cx = Math.Clamp(toward.X, r.Left + inset, r.Right  - inset);
        double cy = Math.Clamp(toward.Y, r.Top  + inset, r.Bottom - inset);

        // Choose the edge by signed distance to each side (negative = the point is OUTSIDE that side). The
        // smallest wins: if the point is outside one side, that side's (negative) distance is the smallest,
        // so the line attaches there; if it's inside/on the box, the nearest side is chosen. Then the end
        // SLIDES along that edge to line up with the approaching point — so it rides the full length of an
        // edge without flipping to a neighbour (which jumped the endpoint and hid the arrowhead), and a
        // point sitting exactly on, or just inside, the edge no longer falls through to a wrong side.
        double dL = toward.X - r.Left, dR = r.Right  - toward.X;
        double dT = toward.Y - r.Top,  dB = r.Bottom - toward.Y;
        double m = Math.Min(Math.Min(dL, dR), Math.Min(dT, dB));
        if (m == dT) return new Point(cx, r.Top);
        if (m == dB) return new Point(cx, r.Bottom);
        if (m == dL) return new Point(r.Left,  cy);
        return new Point(r.Right, cy);
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
        if (a is null) return null;
        bool toTap = !string.IsNullOrEmpty(conn.ToTapConn);

        if (toTap)
        {
            if (TapInfo(conn) is not { } info) return null;
            var b = new Rect(info.pt.X, info.pt.Y, 0, 0);
            if (_data.DiagonalLines)
                return new List<Point> { RectBorderPoint(a.Value, info.pt), info.pt };
            if (conn.Waypoints.Count > 0) return ManualRoute(a.Value, b, conn);
            return TapRoute(a.Value, info.pt, info.targetHoriz, conn);
        }

        var bn = RouteRect(conn.ToId);

        // A Multi-Verzweigung lays its outgoing tines out as a comb — a shared spine off the diamond with
        // one labelled tooth per case. A free (not-yet-connected) tine ends in the air at its comb slot
        // (bn is null); a connected one runs into its target. In the L (Both) the tooth always starts on the
        // shared bar — even when hand-routed — so a bent tooth keeps a bar anchor (no stray line at the node).
        if (_data.Nodes.FirstOrDefault(n => n.Id == conn.FromId) is { Kind: FlowNodeKind.MultiDecision } mds)
            return CombRoute(mds, a.Value, bn, conn);

        if (bn is null) return null;

        // A Bemerkung (Annotation) link always attaches at the annotation's LEFT edge (the bracket's spine) —
        // a short dashed leader from the element, never snapping to its right/other sides.
        var fromN = _data.Nodes.FirstOrDefault(n => n.Id == conn.FromId);
        var toN   = _data.Nodes.FirstOrDefault(n => n.Id == conn.ToId);
        if (fromN?.Kind == FlowNodeKind.Annotation || toN?.Kind == FlowNodeKind.Annotation)
        {
            bool fromAnn = fromN?.Kind == FlowNodeKind.Annotation;
            var annNode = fromAnn ? fromN : toN;
            var annR = fromAnn ? a.Value : bn.Value;
            var othR = fromAnn ? bn.Value : a.Value;
            double g = _data.GridSize >= 4 ? _data.GridSize : 10;
            // Attach at the bracket's spine (right edge when mirrored, else left) and leave it HORIZONTALLY.
            var annPt = new Point(annNode!.Mirrored ? annR.Right : annR.Left, annR.Center.Y);
            var stub   = new Point(annPt.X + (annNode.Mirrored ? g : -g), annPt.Y);
            var wps = conn.Waypoints.Select(w => new Point(w.X, w.Y)).ToList();
            var elemNeighbor = wps.Count > 0 ? (fromAnn ? wps[^1] : wps[0]) : stub;
            var othPt = EdgeSlide(othR, elemNeighbor);           // element edge facing the leader, perpendicular
            var seq = new List<Point>();
            if (fromAnn) { seq.Add(annPt); seq.Add(stub); seq.AddRange(wps); seq.Add(othPt); }
            else         { seq.Add(othPt); seq.AddRange(wps); seq.Add(stub); seq.Add(annPt); }
            return Orthogonalize(seq);   // straight, right-angled leader with movable bends
        }

        if (_data.DiagonalLines)
            return new List<Point> { RectBorderPoint(a.Value, bn.Value.Center), RectBorderPoint(bn.Value, a.Value.Center) };
        if (conn.Waypoints.Count > 0) return ManualRoute(a.Value, bn.Value, conn);
        return Simplify(_liveDrag ? OrthoRoute(a.Value, bn.Value)
                                  : OrthoRouteAvoiding(a.Value, bn.Value, conn.FromId, conn.ToId, conn.Id));
    }

    // Which comb a Multi-Verzweigung tine rides on. A single-direction node forces it; in Both mode each
    // tine joins the comb its target most faces (further right → right comb, else bottom).
    CombDirection TineComb(FlowNode node, FlowConnection c)
    {
        if (node.CombDir != CombDirection.Both) return node.CombDir;
        // Both: alternate tines between the two arms by creation order (even → bottom, odd → right), so both
        // bars of the L are populated and visible. Hand-routed (waypoint) teeth keep their slot too.
        var all = _data.Connections.Where(x => x.FromId == node.Id && string.IsNullOrEmpty(x.ToTapConn)).ToList();
        int idx = all.FindIndex(x => x.Id == c.Id);
        return idx % 2 == 0 ? CombDirection.Bottom : CombDirection.Right;
    }

    // All comb tines of a node on the same comb, in stable creation order (so a tooth keeps its slot as
    // others are added/removed). Includes free (not-yet-connected) tines so they occupy slots too.
    List<FlowConnection> CombTines(FlowNode node, CombDirection comb) =>
        _data.Connections.Where(c => c.FromId == node.Id && string.IsNullOrEmpty(c.ToTapConn)
                                     && TineComb(node, c) == comb).ToList();

    // Pixel gap between adjacent tines. An explicit TineSpacing (grid steps) wins; 0 = the recommended
    // auto value: a standard symbol plus one grid of breathing room on EACH side, so a case body can sit
    // between two teeth with a clear field of gap to its neighbours. For the default 140-wide symbol that
    // works out to 160px = 16 grid steps for a downward comb.
    double CombStep(FlowNode node, CombDirection comb, double g)
    {
        if (node.TineSpacing > 0) return node.TineSpacing * g;
        var (dw, dh) = DefaultNodeSize(FlowNodeKind.Process, FlowSymbol.Auto);
        return (comb == CombDirection.Right ? dh : dw) + 2 * g;
    }

    // Lays a transparent grab-line over each realized Multi-Verzweigung's comb SPINE (the bar at right angles
    // to the stem). Dragging the bar pushes the whole comb nearer to / further from the diamond
    // (node.CombGap). Redrawn whenever connections are.
    void RenderCombHandles()
    {
        if (_canvas is null) return;
        foreach (var h in _combHandles) _canvas.Children.Remove(h);
        _combHandles.Clear();
        double g = _data.GridSize >= 4 ? _data.GridSize : 10;
        foreach (var node in _data.Nodes)
        {
            if (node.Kind != FlowNodeKind.MultiDecision || !_nodeViews.ContainsKey(node.Id)) continue;
            var s = new Rect(node.X, node.Y, node.Width, node.Height);
            var brush = new SolidColorBrush(ParseColor(_style.LineColor));
            void Spine(Point a2, Point b2)
            {
                var ln = new Line { StartPoint = a2, EndPoint = b2, Stroke = brush, StrokeThickness = 1.6, ZIndex = 1, IsHitTestVisible = false };
                _canvas!.Children.Add(ln); _combHandles.Add(ln);
            }
            // Draws the stem from a chosen diamond vertex to its meeting point on the bar (CombStemPos along),
            // routed around as needed, with grab segments. Grabbing near the diamond snaps the vertex; grabbing
            // near the bar slides the meeting (along the bar, and for the L around onto the right arm). Returns
            // the meeting point.
            Point Stem(bool vertical, double spine)
            {
                int side = StemSide(node, vertical);
                var (vtx, od) = StemVertex(s, side);
                double defAlong = vertical ? s.Center.X : s.Center.Y;
                // A single comb's stem rides with the group shift (so shifting the whole comb doesn't leave the
                // bar overhanging to the stem); the L's stem is independent of its bar shift.
                double baseShift = node.CombDir == CombDirection.Both ? node.CombBarShift : node.CombShift;
                double along = Snap(defAlong + (baseShift + node.CombStemPos) * g);
                if (node.CombDir == CombDirection.Both) along = Math.Min(along, CombLGeom(node, g).cornerX);   // stays on the bottom bar
                // The final straight into the bar is a third of the distance from the diamond's near side to
                // the bar (min one grid), so it's long enough to grab comfortably.
                double lastLen = Math.Max(g, (vertical ? spine - s.Bottom : spine - s.Right) / 3);
                Point exit = new(vtx.X + od.X * g, vtx.Y + od.Y * g);
                // The L stem is always auto-routed (its bar-following Z would otherwise expose a grabbable
                // connector that snags); only single combs keep hand-routed stem bends.
                bool bendable = node.CombDir != CombDirection.Both;
                var wps = bendable ? node.CombStemWaypoints.Select(w => new Point(w.X, w.Y)).ToList() : new List<Point>();
                Point meetPt = vertical ? new(along, spine) : new(spine, along);
                Point approach = vertical ? new(along, spine - lastLen) : new(spine - lastLen, along);
                bool opposite = vertical ? side == 0 /*Top*/ : side == 2 /*Left*/;
                List<Point> raw;
                if (wps.Count > 0) raw = new List<Point> { vtx }.Concat(wps).Append(meetPt).ToList();   // hand-routed
                else if (opposite && vertical)
                { double sideX = along >= s.Center.X ? s.Right + g : s.Left - g; raw = new() { vtx, exit, new(sideX, exit.Y), new(sideX, approach.Y), approach, meetPt }; }
                else if (opposite)
                { double sideY = along >= s.Center.Y ? s.Bottom + g : s.Top - g; raw = new() { vtx, exit, new(exit.X, sideY), new(approach.X, sideY), approach, meetPt }; }
                else raw = new() { vtx, exit, approach, meetPt };
                var st = Simplify(Orthogonalize(raw));
                var capNode = node;
                int last = st.Count - 2;
                for (int k = 0; k < st.Count - 1; k++)
                {
                    Spine(st[k], st[k + 1]);
                    int ki = k;
                    var baseRoute = st;
                    if (ki != last && !bendable) continue;   // L: only the bar-touching segment is grabby (no bend footgun)
                    var sg = new Line { StartPoint = st[k], EndPoint = st[k + 1], Stroke = Brushes.Transparent, StrokeThickness = 14, ZIndex = 6, Cursor = new Cursor(StandardCursorType.SizeAll) };
                    sg.PointerPressed += (_, e) =>
                    {
                        if (_mode == EditMode.Remove) return;
                        if (ki == last)   // the segment touching the bar slides the meeting (pos)
                            { _stemDrag = capNode; _stemVertexMode = false; }
                        else              // any other segment bends the stem (hand-routed waypoints)
                        {
                            _stemSegNode = capNode; _stemSegBase = baseRoute; _stemSegIdx = ki;
                            _stemSegHoriz = Math.Abs(baseRoute[ki + 1].Y - baseRoute[ki].Y) < Math.Abs(baseRoute[ki + 1].X - baseRoute[ki].X);
                            _stemSegStart = e.GetPosition(_canvas);
                        }
                        e.Pointer.Capture(_canvas); e.Handled = true;
                    };
                    _canvas!.Children.Add(sg); _combHandles.Add(sg);
                }
                // The contact point at the diamond: a small visible departure dot, with a larger transparent
                // grab that chooses which vertex the stem leaves from (drag it toward a side).
                const double r = 3.5;
                var dot = new Ellipse { Width = r * 2, Height = r * 2, Fill = brush, ZIndex = 7, IsHitTestVisible = false };
                Canvas.SetLeft(dot, vtx.X - r); Canvas.SetTop(dot, vtx.Y - r);
                _canvas!.Children.Add(dot); _combHandles.Add(dot);
                const double gr = 9;
                var dotGrab = new Ellipse { Width = gr * 2, Height = gr * 2, Fill = Brushes.Transparent, ZIndex = 8, Cursor = new Cursor(StandardCursorType.SizeAll) };
                Canvas.SetLeft(dotGrab, vtx.X - gr); Canvas.SetTop(dotGrab, vtx.Y - gr);
                dotGrab.PointerPressed += (_, e) =>
                {
                    if (_mode == EditMode.Remove) return;
                    _stemDrag = capNode; _stemVertexMode = true;
                    e.Pointer.Capture(_canvas); e.Handled = true;
                };
                _canvas!.Children.Add(dotGrab); _combHandles.Add(dotGrab);
                return meetPt;
            }
            void Bar(CombDirection comb)
            {
                var teeth = CombTines(node, comb);
                if (teeth.Count == 0) return;
                bool vertical = comb == CombDirection.Bottom;   // a bottom comb's spine is horizontal → drag vertically
                // Slot extent of the tine group (incl. per-tooth offsets); the bar also reaches the stem meeting.
                double lo = double.MaxValue, hi = double.MinValue, spine = 0;
                for (int k = 0; k < teeth.Count; k++)
                {
                    var (slot, sp, _) = CombSlot(node, comb, k, teeth[k].TineOffset, g); spine = sp;
                    double along = vertical ? slot.X : slot.Y; lo = Math.Min(lo, along); hi = Math.Max(hi, along);
                }
                var mp = Stem(vertical, spine);
                double meet = vertical ? mp.X : mp.Y;
                lo = Math.Min(lo, meet); hi = Math.Max(hi, meet);
                Point a, b;
                if (vertical) { a = new(lo, spine); b = new(hi, spine); }
                else          { a = new(spine, lo); b = new(spine, hi); }
                Spine(a, b);   // the bar
                var bar = new Line
                {
                    StartPoint = a, EndPoint = b, Stroke = Brushes.Transparent, StrokeThickness = 12, ZIndex = 6,
                    Cursor = new Cursor(StandardCursorType.SizeAll),   // 2D drag: gap (perpendicular) + shift (along)
                };
                var capNode = node; bool capVert = vertical;
                bar.PointerPressed += (_, e) =>
                {
                    if (_mode == EditMode.Remove) return;
                    _combDrag = capNode; _combVert = capVert; _combGapOnly = false;
                    _combGrabCur = e.GetPosition(_canvas);
                    _combGrabGap = capNode.CombGap; _combGrabShift = capNode.CombShift; _combGrabBarShift = capNode.CombBarShift;
                    e.Pointer.Capture(_canvas); e.Handled = true;
                };
                _canvas!.Children.Add(bar); _combHandles.Add(bar);
            }
            if (node.CombDir == CombDirection.Both)
            {
                var L = CombLGeom(node, g);
                // Span the bars to the actual teeth (incl. per-tooth offsets) so they reach a dragged tooth.
                var bTeeth = CombTines(node, CombDirection.Bottom);
                var rTeeth = CombTines(node, CombDirection.Right);
                // The bar spans only stem ↔ actual teeth (and the elbow if a right arm exists) — no phantom
                // overhang at the abstract bar origin, so moving the outer tooth inward shortens the bar.
                var stemMp = Stem(true, L.bottomY);   // Z-stem to its meeting point on the bottom bar (call first)
                // The bottom bar spans only the actual elements (stem meeting, teeth, and the elbow when a
                // right arm exists) — NOT the diamond centre, so a shifted bar leaves no spike to the middle.
                double barLeft = stemMp.X, barRight = stemMp.X, rTop = L.bottomY, rBot = L.bottomY;
                if (rTeeth.Count > 0) { barLeft = Math.Min(barLeft, L.cornerX); barRight = Math.Max(barRight, L.cornerX); }
                for (int k = 0; k < bTeeth.Count; k++)
                { double x = CombSlot(node, CombDirection.Bottom, k, bTeeth[k].TineOffset, g).slot.X; barLeft = Math.Min(barLeft, x); barRight = Math.Max(barRight, x); }
                for (int k = 0; k < rTeeth.Count; k++)
                { double y = CombSlot(node, CombDirection.Right, k, rTeeth[k].TineOffset, g).slot.Y; rTop = Math.Min(rTop, y); rBot = Math.Max(rBot, y); }
                // Visible single L: bottom bar + right bar (the stem is drawn by Stem()).
                Spine(new(barLeft, L.bottomY), new(barRight, L.bottomY));      // bottom bar
                if (rTeeth.Count > 0)
                    Spine(new(L.cornerX, rTop), new(L.cornerX, rBot));         // right bar (above + below the elbow)

                var capNode = node;
                // Grab the bottom bar → gap (down) + slide the bar left/right; the stem stays put.
                var barGrab = new Line
                {
                    StartPoint = new(barLeft, L.bottomY), EndPoint = new(barRight, L.bottomY),
                    Stroke = Brushes.Transparent, StrokeThickness = 12, ZIndex = 6,
                    Cursor = new Cursor(StandardCursorType.SizeAll),
                };
                barGrab.PointerPressed += (_, e) =>
                {
                    if (_mode == EditMode.Remove) return;
                    _combDrag = capNode; _combVert = true; _combGapOnly = true;
                    _combGrabCur = e.GetPosition(_canvas);
                    _combGrabGap = capNode.CombGap; _combGrabShift = capNode.CombShift; _combGrabBarShift = capNode.CombBarShift;
                    e.Pointer.Capture(_canvas); e.Handled = true;
                };
                _canvas!.Children.Add(barGrab); _combHandles.Add(barGrab);
            }
            else
            {
                Bar(node.CombDir);
            }
        }
    }

    // Drags a Multi-Verzweigung's spine bar in 2D: perpendicular to the diamond sets the gap (min 1), along
    // the bar the shift. A hand-routed tooth's bends + target stay FIXED in space — only the bar-side segment
    // re-stretches to the moved slot (the user's principle: what's held stays held, touched lines just stretch).
    void DragComb(Point cur)
    {
        if (_combDrag is null) return;
        double g = _data.GridSize >= 4 ? _data.GridSize : 10;
        var s = new Rect(_combDrag.X, _combDrag.Y, _combDrag.Width, _combDrag.Height);
        // Relative drag: deltas are measured from the grab point, never the absolute cursor — so the bar
        // doesn't jump to centre under the mouse on grab. A hand-routed tooth stays attached to the bar and
        // rides along with it (BOTH axes) — its tip re-glues to the target via re-projection — so it can't
        // split off or fold into a loop. Straight teeth just re-route from their moved slot.
        if (_combGapOnly)   // L bottom bar: gap (down) + bar slide left/right
        {
            int gap = Math.Max(1, _combGrabGap + (int)Math.Round((cur.Y - _combGrabCur.Y) / g));
            int barShift = _combGrabBarShift + (int)Math.Round((cur.X - _combGrabCur.X) / g);
            if (gap == _combDrag.CombGap && barShift == _combDrag.CombBarShift) return;
            double dx = (barShift - _combDrag.CombBarShift) * g, dy = (gap - _combDrag.CombGap) * g;
            _combDrag.CombGap = gap; _combDrag.CombBarShift = barShift;
            TranslateTeeth(_combDrag, dx, dy);
        }
        else   // single comb bar: 2D — gap (perpendicular) + shift (along)
        {
            int gap, shift;
            if (_combVert) { gap = _combGrabGap + (int)Math.Round((cur.Y - _combGrabCur.Y) / g); shift = _combGrabShift + (int)Math.Round((cur.X - _combGrabCur.X) / g); }
            else           { gap = _combGrabGap + (int)Math.Round((cur.X - _combGrabCur.X) / g); shift = _combGrabShift + (int)Math.Round((cur.Y - _combGrabCur.Y) / g); }
            gap = Math.Max(1, gap);
            if (gap == _combDrag.CombGap && shift == _combDrag.CombShift) return;
            double dGap = (gap - _combDrag.CombGap) * g, dShift = (shift - _combDrag.CombShift) * g;
            _combDrag.CombGap = gap; _combDrag.CombShift = shift;
            // Map gap (perpendicular) + shift (along bar) onto X/Y for this comb's orientation.
            if (_combVert) TranslateTeeth(_combDrag, dShift, dGap); else TranslateTeeth(_combDrag, dGap, dShift);
        }
        UpdateConnectionsFor(_combDrag.Id);
        RenderCombHandles();
    }

    // The stem's diamond vertex: -1 stored = the comb's natural side (bottom for a bottom/L comb, right for a
    // right comb), else the explicit 0=Top/1=Bottom/2=Left/3=Right.
    static int StemSide(FlowNode node, bool vertical) => node.CombStemVertex >= 0 ? node.CombStemVertex : (vertical ? 1 : 3);

    // The vertex point + its outward normal for a diamond side index.
    static (Point pt, Point outDir) StemVertex(Rect s, int side) => side switch
    {
        0 => (new Point(s.Center.X, s.Top),    new Point(0, -1)),
        2 => (new Point(s.Left,     s.Center.Y), new Point(-1, 0)),
        3 => (new Point(s.Right,    s.Center.Y), new Point(1, 0)),
        _ => (new Point(s.Center.X, s.Bottom), new Point(0, 1)),
    };

    // Bends a stem segment (perpendicular), storing the result as the stem's hand-routed waypoints. The
    // vertex and the bends are fixed; the final straight into the bar still flexes when the bar moves.
    void DragStemSeg(Point cur)
    {
        if (_stemSegNode is null || _stemSegBase is null) return;
        double v = _stemSegHoriz ? Snap(_stemSegBase[_stemSegIdx].Y + (cur.Y - _stemSegStart.Y))
                                 : Snap(_stemSegBase[_stemSegIdx].X + (cur.X - _stemSegStart.X));
        var full = BuildDragged(_stemSegBase, _stemSegIdx, _stemSegHoriz, v);
        _stemSegNode.CombStemWaypoints = full.Skip(1).Take(full.Count - 2).Select(p => new BoardWaypoint { X = p.X, Y = p.Y }).ToList();
        RenderCombHandles();
    }

    // Drags the stem: near the diamond picks which vertex it leaves from; near the bar slides where it meets.
    void DragStem(Point cur)
    {
        if (_stemDrag is null) return;
        double g = _data.GridSize >= 4 ? _data.GridSize : 10;
        var s = new Rect(_stemDrag.X, _stemDrag.Y, _stemDrag.Width, _stemDrag.Height);
        bool vertical = _stemDrag.CombDir != CombDirection.Right;   // bottom & both → horizontal bar (drag along X)
        if (_stemVertexMode)   // snap the diamond end to the vertex the cursor points at (dominant axis)
        {
            double dx = cur.X - s.Center.X, dy = cur.Y - s.Center.Y;
            int side = Math.Abs(dx) >= Math.Abs(dy) ? (dx >= 0 ? 3 : 2) : (dy >= 0 ? 1 : 0);
            if (side == _stemDrag.CombStemVertex) return;
            _stemDrag.CombStemVertex = side;
        }
        else                   // slide the meeting along the bar (and, for the L, around onto the right arm)
        {
            double defAlong = vertical ? s.Center.X : s.Center.Y;
            double along;
            if (_stemDrag.CombDir == CombDirection.Both)
                along = Math.Min(cur.X, CombLGeom(_stemDrag, g).cornerX);   // along the bottom bar (up to the elbow)
            else along = vertical ? cur.X : cur.Y;
            int pos = (int)Math.Round((along - defAlong) / g);
            pos -= _stemDrag.CombDir == CombDirection.Both ? _stemDrag.CombBarShift : _stemDrag.CombShift;   // relative to the shifted comb
            if (pos == _stemDrag.CombStemPos) return;
            _stemDrag.CombStemPos = pos;
        }
        RenderCombHandles();
    }

    // Slides a single comb tooth along its bar: sets its TineOffset so its slot follows the cursor MOVEMENT
    // (relative to the grab point — no jump). Grid-stepped; the whole tooth (re-targeted tip + hand bends)
    // rides along, so a bent tooth slides cleanly instead of skewing.
    void DragTooth(Point cur)
    {
        if (_toothDrag is null) return;
        var node = _data.Nodes.FirstOrDefault(n => n.Id == _toothDrag.FromId);
        if (node is null) return;
        double g = _data.GridSize >= 4 ? _data.GridSize : 10;
        var comb = TineComb(node, _toothDrag);
        double along     = comb == CombDirection.Right ? cur.Y : cur.X;
        double grabAlong = comb == CombDirection.Right ? _toothGrabCur.Y : _toothGrabCur.X;
        int offset = _toothGrabOffset + (int)Math.Round((along - grabAlong) / g);
        if (offset == _toothDrag.TineOffset) return;
        double d = (offset - _toothDrag.TineOffset) * g;   // along-bar delta
        if (_toothDrag.TineTargetSet) { if (comb == CombDirection.Right) _toothDrag.TineTargetY += d; else _toothDrag.TineTargetX += d; }
        if (_toothDrag.Waypoints.Count > 0) foreach (var w in _toothDrag.Waypoints) { if (comb == CombDirection.Right) w.Y += d; else w.X += d; }
        _toothDrag.TineOffset = offset;
        UpdateConnectionsFor(node.Id);
        RenderCombHandles();
    }

    // Drags a wired comb tooth's TARGET end: stores the cursor as the approach anchor so the entry projects
    // onto the target's nearest edge (any side, perpendicular). The bar-side bends absorb the rest.
    void DragToothEnd(Point cur)
    {
        if (_toothEndDrag is null) return;
        _toothEndDrag.TineTargetSet = true;
        _toothEndDrag.TineTargetX = Snap(cur.X); _toothEndDrag.TineTargetY = Snap(cur.Y);   // grid-snapped anchor
        RenderConnection(_toothEndDrag);
    }

    // After dragging a tooth end: if its anchor lands back where the automatic entry would be, drop the
    // anchor so the tooth is auto again (and rides along when the bar/node moves).
    void SettleToothEnd(FlowConnection conn)
    {
        if (!conn.TineTargetSet) return;
        var node = _data.Nodes.FirstOrDefault(n => n.Id == conn.FromId);
        var tr = NodeRect(conn.ToId);
        if (node is null || tr is null) return;
        double g = _data.GridSize >= 4 ? _data.GridSize : 10;
        var comb = TineComb(node, conn);
        int i = Math.Max(0, CombTines(node, comb).FindIndex(c => c.Id == conn.Id));
        var slot = CombSlot(node, comb, i, conn.TineOffset, g).slot;
        var auto = RouteToothFromSlot(slot, comb, tr, g, null);
        var here = RouteToothFromSlot(slot, comb, tr, g, new Point(conn.TineTargetX, conn.TineTargetY));
        if (auto.Count > 0 && here.Count > 0 && Dist(auto[^1], here[^1]) < g) conn.TineTargetSet = false;
    }

    // Slides the stored waypoints of a node's hand-routed comb teeth by (dx,dy). Used by the bar drag to carry
    // bent teeth ALONG the bar (one axis only), so their shape stays attached without skewing.
    void TranslateTeeth(FlowNode node, double dx, double dy)
    {
        if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01) return;
        foreach (var c in _data.Connections)
            if (c.FromId == node.Id && string.IsNullOrEmpty(c.ToTapConn) && c.Waypoints.Count > 0)
                foreach (var w in c.Waypoints) { w.X += dx; w.Y += dy; }
    }

    // The topmost node whose box contains p (excluding one id), or null — for dropping a dragged tine tip.
    FlowNode? NodeAtPoint(Point p, string? exceptId)
    {
        for (int i = _data.Nodes.Count - 1; i >= 0; i--)
        {
            var n = _data.Nodes[i];
            if (n.Id == exceptId || n.Kind == FlowNodeKind.Junction) continue;
            if (new Rect(n.X, n.Y, n.Width, n.Height).Contains(p)) return n;
        }
        return null;
    }

    // The rendered connection whose polyline passes within tol of p (excluding one id), or null.
    FlowConnection? ConnNearPoint(Point p, string? exceptId, double tol)
    {
        FlowConnection? best = null; double bestD = tol;
        foreach (var (id, pts) in _connPts)
        {
            if (id == exceptId || pts.Count < 2) continue;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                double d = DistToSegment(p, pts[i], pts[i + 1]);
                if (d < bestD) { bestD = d; best = _data.Connections.FirstOrDefault(c => c.Id == id); }
            }
        }
        return best;
    }

    // Finishes a tine-tip drag: dropping the open end on a node wires the tine to it; on a line taps onto
    // that line; in empty space leaves the tine free. Either way the case label stays mandatory + unique.
    async Task WireTineByDrag(FlowConnection tine, Point at)
    {
        var src = _data.Nodes.FirstOrDefault(n => n.Id == tine.FromId);
        if (src is null) { RenderAllConnections(); return; }

        var node = NodeAtPoint(at, src.Id);
        var line = node is null ? ConnNearPoint(at, tine.Id, 10) : null;
        if (node is null && line is null) { RenderAllConnections(); return; }   // dropped in space → stays free

        var lbl = await PromptBranchLabel(src, tine.Label, tine.Id);
        if (lbl is null) { RenderAllConnections(); return; }
        tine.Label = lbl;
        if (node is not null) { tine.ToId = node.Id; }
        else
        {
            var pts = _connPts.TryGetValue(line!.Id, out var lp) ? lp : null;
            var foot = pts is not null ? NearestPointOn(pts, at).pt : at;
            tine.ToTapConn = line.Id; tine.ToTapX = Snap(foot.X); tine.ToTapY = Snap(foot.Y);
        }
        Save();
        RenderAllConnections();
        RefreshJunctions();
    }

    // Routes one tine of a Multi-Verzweigung as part of a comb: a spine one grid off the diamond's bottom
    // (or right) edge, then a tooth at this tine's slot (index * spacing). A connected tine jogs into its
    // target head-on; a free tine just hangs a short stub in the air, ready to be wired up.
    // Shared geometry of a Both-mode L comb: where the single stem leaves the diamond bottom (slidable via
    // shift), the spine level below it, the elbow where the bottom arm meets the right arm, and the two
    // per-arm tine spacings.
    // Geometry of a Both-mode L comb. The bottom bar starts at the diamond centre and runs right (one tooth
    // per bottom tine); the right bar rises from the elbow (one tooth per right tine). A single stem drops
    // from the diamond bottom onto the bottom bar at stemX (slidable along the edge via CombShift).
    (double stemX, double bottomY, double cornerX, double barStartX, double topRightY, double stepB, double stepR)
        CombLGeom(FlowNode node, double g)
    {
        var s = new Rect(node.X, node.Y, node.Width, node.Height);
        double stepB = CombStep(node, CombDirection.Bottom, g), stepR = CombStep(node, CombDirection.Right, g);
        double barStartX = s.Center.X + node.CombBarShift * g;   // the tine group slides left/right here
        int nB = CombTines(node, CombDirection.Bottom).Count, nR = CombTines(node, CombDirection.Right).Count;
        double bottomY   = Snap(s.Bottom + Math.Max(1, node.CombGap) * g);
        double cornerX   = Snap(barStartX + Math.Max(1, nB) * stepB);
        double stemX     = Snap(s.Center.X);   // the diamond's bottom vertex — the only real bottom anchor
        double topRightY = Snap(bottomY - Math.Max(1, nR) * stepR);
        return (stemX, bottomY, cornerX, barStartX, topRightY, stepB, stepR);
    }

    List<Point> CombRoute(FlowNode node, Rect s, Rect? t, FlowConnection conn)
    {
        double g = _data.GridSize >= 4 ? _data.GridSize : 10;
        var comb = TineComb(node, conn);
        int i = Math.Max(0, CombTines(node, comb).FindIndex(c => c.Id == conn.Id));
        var slot = CombSlot(node, comb, i, conn.TineOffset, g).slot;
        // Hand-routed tooth: the user grabbed a segment, so honour the bends — slot → waypoints → a clean
        // perpendicular entry into the target. The tooth still STARTS on the bar (no stray line at the node).
        if (conn.Waypoints.Count > 0 && t is { } tr)
        {
            // The tooth rides with the bar (DragComb translates its waypoints), so the slot↔bend relationship
            // is fixed — no culling needed. Route: slot → bends → a clean perpendicular entry into the target.
            var wps = conn.Waypoints.Select(w => new Point(w.X, w.Y)).ToList();
            var pts = new List<Point> { slot };
            pts.AddRange(wps);
            pts.Add(EdgeSlide(tr, wps[^1]));   // entry projected onto the nearest edge (perpendicular, outside)
            return Simplify(Orthogonalize(pts));
        }
        Point? anchor = conn.TineTargetSet ? new Point(conn.TineTargetX, conn.TineTargetY) : null;
        return RouteToothFromSlot(slot, comb, t, g, anchor);
    }

    // The tooth from a bar slot to its target (or a free stub in the air). Without a user anchor the tooth
    // drops down (bottom) / runs right (right) and jogs in. With an anchor the entry is that point projected
    // onto the target's nearest edge (any side, perpendicular, from outside); the bar-side bends absorb a
    // moving bar while the target end stays put.
    List<Point> RouteToothFromSlot(Point slot, CombDirection comb, Rect? t, double g, Point? anchor = null)
    {
        double stub = 3 * g;
        var head = new List<Point> { slot };
        if (t is { } tr)
        {
            if (anchor is { } a)
            {
                // Entry = the anchor projected onto the target's NEAREST edge (slides along the facing edge,
                // wraps around a corner to a neighbour). Route: leave the bar perpendicular, then an L whose
                // corner is chosen to go AROUND the node (never back through it / no U), then enter perpendicular.
                var e = EdgeSlide(tr, a);
                var od = Outward(tr, e);
                var bn = comb == CombDirection.Right ? new Point(1, 0) : new Point(0, 1);   // off-bar normal
                var o  = new Point(slot.X + bn.X * g, slot.Y + bn.Y * g);   // leave the bar perpendicular
                var ap = new Point(e.X + od.X * g, e.Y + od.Y * g);         // stand-off one grid outside the entry edge
                // Route o → ap AROUND the node (identical algorithm for both combs), then perpendicular into e.
                // The old corner pick could send the right comb's wrap straight THROUGH the node; this can't.
                foreach (var p in AroundNode(o, ap, tr.Inflate(g * 0.5), g, bn)) head.Add(p);
                head.Add(e);
            }
            else
            {
                // Auto route: always enter the target's FACING edge (left for a right comb, top for a bottom
                // comb), sliding to the slot's level — so teeth stay clean even when a target isn't aligned
                // with its slot (no entering the top/bottom edge with a kink).
                double inset = Math.Max(0, Math.Min(6, Math.Min(tr.Width, tr.Height) / 2 - 1));
                // The jog (knick) sits at the MIDPOINT between bar and target — never hard against the bar — so
                // an off-level target gets a clean centred Z whose segments the user can still grab and nudge.
                if (comb == CombDirection.Right)
                { var e = new Point(tr.Left, Math.Clamp(slot.Y, tr.Top + inset, tr.Bottom - inset)); double j = Math.Max(slot.X + g, Math.Min(e.X - g, Snap((slot.X + e.X) / 2))); head.Add(new(j, slot.Y)); head.Add(new(j, e.Y)); head.Add(e); }
                else
                { var e = new Point(Math.Clamp(slot.X, tr.Left + inset, tr.Right - inset), tr.Top); double j = Math.Max(slot.Y + g, Math.Min(e.Y - g, Snap((slot.Y + e.Y) / 2))); head.Add(new(slot.X, j)); head.Add(new(e.X, j)); head.Add(e); }
            }
        }
        else head.Add(comb == CombDirection.Right ? new(slot.X + stub, slot.Y) : new(slot.X, slot.Y + stub));
        return Simplify(Orthogonalize(head));
    }

    // An orthogonal path from o to ap (both already OUTSIDE box) that never crosses the box. The tooth ALWAYS
    // leaves the bar along the off-bar axis (bn), so every fallback prefers the route that heads AWAY from the
    // bar first — never the bar-hugging corner. Centred Z when clear, else an L, else a detour around the node.
    List<Point> AroundNode(Point o, Point ap, Rect box, double g, Point bn)
    {
        bool offH = Math.Abs(bn.X) > Math.Abs(bn.Y);   // tooth leaves the bar horizontally (right comb)
        // Centred Z: first segment runs along the off-bar axis (away from the bar), knick in the middle.
        var z = offH
            ? new List<Point> { o, new(Snap((o.X + ap.X) / 2), o.Y), new(Snap((o.X + ap.X) / 2), ap.Y), ap }
            : new List<Point> { o, new(o.X, Snap((o.Y + ap.Y) / 2)), new(ap.X, Snap((o.Y + ap.Y) / 2)), ap };
        if (!HitsBox(z, box)) return z;

        // L fallback: try the corner whose FIRST segment leaves along the off-bar axis first (target-side, not
        // bar-hugging); only fall back to the other corner if that one is blocked.
        var first = offH ? new Point(ap.X, o.Y) : new Point(o.X, ap.Y);
        var other = offH ? new Point(o.X, ap.Y) : new Point(ap.X, o.Y);
        if (!SegHitsRect(o, first, box) && !SegHitsRect(first, ap, box)) return new() { o, first, ap };
        if (!SegHitsRect(o, other, box) && !SegHitsRect(other, ap, box)) return new() { o, other, ap };

        // Detour around the node, perpendicular to the off-bar axis (over top/bottom for a horizontal tooth).
        double yTop = box.Top - g, yBot = box.Bottom + g, xL = box.Left - g, xR = box.Right + g;
        if (offH)
        { double y = Math.Abs(o.Y - yTop) + Math.Abs(ap.Y - yTop) <= Math.Abs(o.Y - yBot) + Math.Abs(ap.Y - yBot) ? yTop : yBot; return new() { o, new(o.X, y), new(ap.X, y), ap }; }
        double x = Math.Abs(o.X - xL) + Math.Abs(ap.X - xL) <= Math.Abs(o.X - xR) + Math.Abs(ap.X - xR) ? xL : xR;
        return new() { o, new(x, o.Y), new(x, ap.Y), ap };
    }

    // Whether any segment of a polyline crosses the interior of box.
    bool HitsBox(List<Point> pts, Rect box)
    {
        for (int i = 0; i < pts.Count - 1; i++) if (SegHitsRect(pts[i], pts[i + 1], box)) return true;
        return false;
    }

    // Whether an axis-aligned segment a→b crosses the interior of rect r (touching an edge doesn't count).
    static bool SegHitsRect(Point a, Point b, Rect r)
    {
        if (Math.Abs(a.Y - b.Y) < 0.01)   // horizontal
        {
            if (a.Y <= r.Top || a.Y >= r.Bottom) return false;
            return Math.Min(a.X, b.X) < r.Right && Math.Max(a.X, b.X) > r.Left;
        }
        if (Math.Abs(a.X - b.X) < 0.01)   // vertical
        {
            if (a.X <= r.Left || a.X >= r.Right) return false;
            return Math.Min(a.Y, b.Y) < r.Bottom && Math.Max(a.Y, b.Y) > r.Top;
        }
        return false;
    }

    // The slot point on the bar for tine index i (nudged by its per-tooth TineOffset along the bar), plus the
    // bar's spine coordinate and the diamond vertex / elbow the stem leaves from. Handles single combs and the
    // Both-mode L. The stem stays at the diamond's vertex; the group is centred + CombShift; each tooth is then
    // nudged by tineOffset so the user can re-space individual teeth (the bar follows).
    (Point slot, double spine, Point vertex) CombSlot(FlowNode node, CombDirection comb, int i, int tineOffset, double g)
    {
        var s = new Rect(node.X, node.Y, node.Width, node.Height);
        if (node.CombDir == CombDirection.Both)
        {
            var L = CombLGeom(node, g);
            return comb == CombDirection.Bottom
                ? (new Point(Snap(L.barStartX + i * L.stepB + tineOffset * g), L.bottomY), L.bottomY, new Point(L.stemX, s.Bottom))
                : (new Point(L.cornerX, Snap(L.bottomY - (i + 1) * L.stepR + tineOffset * g)), L.cornerX, new Point(L.cornerX, L.bottomY));
        }
        int n = CombTines(node, comb).Count;
        double off = node.CombShift * g + (i - (n - 1) / 2.0) * CombStep(node, comb, g) + tineOffset * g;
        double gap = Math.Max(1, node.CombGap) * g;
        if (comb == CombDirection.Right)
        {
            double spineX = Snap(s.Right + gap);
            return (new Point(spineX, Snap(s.Center.Y + off)), spineX, new Point(s.Right, s.Center.Y));
        }
        double spineY = Snap(s.Bottom + gap);
        return (new Point(Snap(s.Center.X + off), spineY), spineY, new Point(s.Center.X, s.Bottom));
    }

    // Routes a tap as a clean L whose FINAL segment is perpendicular to the target line (a proper T): the
    // source exits toward the target, runs to the tap's level, then a straight stub into the tap point.
    List<Point> TapRoute(Rect s, Point tp, bool targetHoriz, FlowConnection conn)
    {
        double g = _data.GridSize >= 4 ? _data.GridSize : 10;
        var sc = s.Center;
        // Approach point: one grid out from the meeting point, perpendicular to the target on the SOURCE's
        // side — so the final stub meets the line head-on. The head from the source to this point is routed
        // with full obstacle-avoidance (so it goes AROUND symbols) and the departure-edge/occupancy rule
        // (so it leaves a sensible free edge), rather than a naive straight L that cuts through symbols.
        Point approach = targetHoriz
            ? new Point(tp.X, tp.Y + (sc.Y <= tp.Y ? -g : g))
            : new Point(tp.X + (sc.X <= tp.X ? -g : g), tp.Y);
        var ar = new Rect(approach.X, approach.Y, 0, 0);
        var head = _liveDrag ? OrthoRoute(s, ar)
                             : OrthoRouteAvoiding(s, ar, conn.FromId, "", conn.Id, endsOnLine: true);
        // Clean the final approach: drop any short grid-staircase right before the line, then add one
        // straight perpendicular stub into the meeting point (no zig-zag at the end).
        var pts = new List<Point>(head);
        while (pts.Count > 1 && Dist(pts[^1], tp) < g * 1.6) pts.RemoveAt(pts.Count - 1);
        var lastp = pts[^1];
        pts.Add(targetHoriz ? new Point(tp.X, lastp.Y) : new Point(lastp.X, tp.Y));
        pts.Add(tp);
        return Simplify(Orthogonalize(pts));
    }

    readonly HashSet<string> _resolvingTaps = new();   // cycle guard for tap-on-tap resolution

    // The tap's meeting point and whether the target line runs horizontally there (so a stub can approach
    // it perpendicularly). Null if the target is gone. Targets may themselves be taps (nested) — resolved
    // recursively with a cycle guard.
    (Point pt, bool targetHoriz)? TapInfo(FlowConnection conn)
    {
        var target = _data.Connections.FirstOrDefault(c => c.Id == conn.ToTapConn);
        if (target is null) return null;
        List<Point>? pts = _connPts.TryGetValue(target.Id, out var tp) && tp.Count >= 2 ? tp : null;
        if (pts is null && _resolvingTaps.Add(conn.Id))   // compute the target on the fly (incl. nested taps)
        {
            try { pts = RouteOf(target); } finally { _resolvingTaps.Remove(conn.Id); }
        }
        if (pts is null || pts.Count < 2) return null;
        // Project the stored anchor onto the target: a sideways move of the target keeps the anchor's other
        // coordinate, so the stub grows/shrinks instead of dragging the meeting point along.
        return NearestPointOn(pts, new Point(conn.ToTapX, conn.ToTapY));
    }

    // The closest point on a polyline to q, plus whether that segment runs horizontally.
    static (Point pt, bool horiz) NearestPointOn(List<Point> pts, Point q)
    {
        double best = double.MaxValue; Point bestPt = q; bool bestH = true;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            Point a = pts[i], b = pts[i + 1];
            double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
            double f = len2 < 1e-9 ? 0 : Math.Clamp(((q.X - a.X) * dx + (q.Y - a.Y) * dy) / len2, 0, 1);
            Point c = new(a.X + f * dx, a.Y + f * dy);
            double d = (q.X - c.X) * (q.X - c.X) + (q.Y - c.Y) * (q.Y - c.Y);
            if (d < best) { best = d; bestPt = c; bestH = Math.Abs(dx) >= Math.Abs(dy); }
        }
        return (bestPt, bestH);
    }

    Point? TapPoint(FlowConnection conn) => TapInfo(conn)?.pt;

    // Connecting in connect mode and clicking a line: the new line ENDS on that line (a T-piece / tap) at
    // the click position — no junction node, no splitting of the target.
    async void TapOntoLine(FlowConnection target, Point at)
    {
        if (_connectFromId is null) return;
        var from = _connectFromId;
        var armedTine = _armedTine; _armedTine = null;
        _connectFromId = null;
        RemoveRubberBand();

        // Tap onto exactly the line that was clicked — even if that line is itself a tap (nested T-pieces).
        // Anchor = the click projected onto the line, snapped to the grid.
        Point anchor = new(Snap(at.X), Snap(at.Y));
        if (_connPts.TryGetValue(target.Id, out var pts) && pts.Count >= 2)
        {
            var foot = NearestPointOn(pts, at).pt;
            anchor = new Point(Snap(foot.X), Snap(foot.Y));
        }

        // Guard against a duplicate (a stray second press): same source tapping the same line at ~the same
        // point shouldn't make a parallel twin.
        if (_data.Connections.Any(c => c.FromId == from && c.ToTapConn == target.Id
                                       && Math.Abs(c.ToTapX - anchor.X) < 1 && Math.Abs(c.ToTapY - anchor.Y) < 1))
        { RenderAllConnections(); return; }

        string label = "";
        var tapSrc = _data.Nodes.FirstOrDefault(n => n.Id == from);
        if (tapSrc is not null && IsDecision(tapSrc.Kind))
        {
            var lbl = await PromptBranchLabel(tapSrc, "", null);
            if (lbl is null && tapSrc.Kind == FlowNodeKind.MultiDecision) { RenderAllConnections(); return; }
            label = lbl ?? "";
        }

        // A tap normally carries no arrowhead, but a backward tap (the line meets a point LEFT of or ABOVE
        // its source — typically a loop) gets one: it aids readability where the flow direction isn't obvious.
        bool? arrow = NodeCenter(from) is { } fc && (anchor.X < fc.X - 1 || anchor.Y < fc.Y - 1) ? true : null;
        // An armed comb tine taps onto the line in place (it keeps its slot/label) instead of spawning a twin.
        if (armedTine is not null)
        { armedTine.ToTapConn = target.Id; armedTine.ToTapX = anchor.X; armedTine.ToTapY = anchor.Y; if (!string.IsNullOrEmpty(label)) armedTine.Label = label; armedTine.Arrow = arrow; }
        else
            _data.Connections.Add(new FlowConnection { FromId = from, ToTapConn = target.Id, ToTapX = anchor.X, ToTapY = anchor.Y, LineColor = _style.LineColor, Label = label, Arrow = arrow });
        Save();
        RenderAllConnections();
    }

    // Removes any tap whose target line no longer exists (e.g. its node was deleted).
    void CleanupTaps()
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            var ids = _data.Connections.Select(c => c.Id).ToHashSet();
            foreach (var c in _data.Connections.Where(c => !string.IsNullOrEmpty(c.ToTapConn) && !ids.Contains(c.ToTapConn)).ToList())
            {
                _data.Connections.Remove(c);
                if (_connViews.TryGetValue(c.Id, out var vs)) foreach (var v in vs) _canvas!.Children.Remove(v);
                _connViews.Remove(c.Id); _connPts.Remove(c.Id);
                changed = true;
            }
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
        var foot = NearestPointOn(pts, cur).pt;
        _tapDrag.ToTapX = Snap(foot.X); _tapDrag.ToTapY = Snap(foot.Y);
        // Keep any manual bend (don't auto-clear) — sliding moves the meeting, the bend stays. Use the
        // right-click "reset route" to go back to the clean auto-L.
        RenderConnection(_tapDrag);
        RenderTapDots();
    }

    // Re-renders every tap that ends on the given line — and any tap riding on THOSE (nested) — plus the
    // coincidence dots.
    void RenderTapsOnto(string targetId)
    {
        RenderTapChain(targetId, 0);
        RenderTapDots();
    }
    void RenderTapChain(string targetId, int depth)
    {
        if (depth > 20) return;   // guard against a pathological tap cycle
        foreach (var c in _data.Connections.Where(c => c.ToTapConn == targetId).ToList())
        {
            RenderConnection(c);
            RenderTapChain(c.Id, depth + 1);
        }
    }

    void RenderTapDots()
    {
        if (_canvas is null) return;
        foreach (var d in _tapDots) _canvas.Children.Remove(d);
        _tapDots.Clear();

        // Pure visual indicator: two (or more) T-pieces on the SAME line meeting at the same point. Keyed
        // by (target line, grid-snapped meeting point); a group of 2+ gets a dot. Not interactive.
        var groups = new Dictionary<(string conn, double x, double y), Point>();
        var counts = new Dictionary<(string conn, double x, double y), int>();
        foreach (var c in _data.Connections)
        {
            if (string.IsNullOrEmpty(c.ToTapConn)) continue;
            if (TapPoint(c) is not { } p) continue;
            var key = (c.ToTapConn, Snap(p.X), Snap(p.Y));
            counts[key] = counts.GetValueOrDefault(key) + 1;
            groups[key] = p;
        }

        var brush = new SolidColorBrush(ParseColor(_style.LineColor));
        const double r = 4;
        foreach (var (key, n) in counts)
        {
            if (n < 2) continue;
            var p = groups[key];
            var dot = new Ellipse { Width = r * 2, Height = r * 2, Fill = brush, IsHitTestVisible = false, ZIndex = 5 };
            Canvas.SetLeft(dot, p.X - r); Canvas.SetTop(dot, p.Y - r);
            _canvas.Children.Add(dot); _tapDots.Add(dot);
        }
    }

    // Draws one connection: line, arrowhead, a transparent hit-zone for editing, and an optional label.
    void RenderConnection(FlowConnection conn, List<Point>? forcePts = null)
    {
        if (NodePage(conn.FromId) != _page) return;   // only the current page's connections are drawn
        if (_connViews.TryGetValue(conn.Id, out var old))
            foreach (var v in old) _canvas!.Children.Remove(v);
        _connViews.Remove(conn.Id);

        // forcePts lets a group-move draw a line as its pre-move route translated by the delta (a pure
        // shift, no re-routing), so a rigidly-moved line keeps its exact shape instead of being recomputed.
        var pts = forcePts ?? RouteOf(conn);
        if (pts is null || pts.Count < 2) return;

        bool toTap = !string.IsNullOrEmpty(conn.ToTapConn);   // this line ends ON another line (a T-piece)
        // A free comb tine of a Multi-Verzweigung: not yet wired (no node target, no tap). Its open end is an
        // empty HANDLE you drag onto a target — never an arrowhead (an arrow tempts users to just butt a node
        // against it and think it's connected, which it wouldn't be).
        var srcNode = _data.Nodes.FirstOrDefault(n => n.Id == conn.FromId);
        bool isMultiSrc = srcNode?.Kind == FlowNodeKind.MultiDecision;
        bool freeTine = string.IsNullOrEmpty(conn.ToId) && !toTap && isMultiSrc;
        // A comb tooth sits on the shared spine — no DIN departure dot (it doesn't leave a node edge), even
        // when hand-routed (it still starts on the bar).
        bool combTine = isMultiSrc && !toTap;

        // A link to/from a Bemerkung (Annotation) is a documentary tie, not control flow: drawn dashed and
        // WITHOUT an arrowhead, per DIN 66001.
        var dstNode = _data.Nodes.FirstOrDefault(n => n.Id == conn.ToId);
        bool annotationLink = srcNode?.Kind == FlowNodeKind.Annotation || dstNode?.Kind == FlowNodeKind.Annotation;

        _connPts[conn.Id] = pts;   // remembered for the optional crossover-bridge overlay

        // All arrows share the diagram's arrow colour, so they stay uniform (and follow the Options picker).
        var brush   = new SolidColorBrush(ParseColor(_style.LineColor));
        var visuals = new List<Control>();

        var line = new Polyline { Stroke = brush, StrokeThickness = conn.Thickness, IsHitTestVisible = false };
        if (annotationLink) line.StrokeDashArray = new AvaloniaList<double> { 3, 2 };
        foreach (var p in pts) line.Points.Add(p);
        line.ZIndex = 1;
        _canvas!.Children.Add(line); visuals.Add(line);

        // Arrowhead: explicit per-connection override wins, else automatic (an arrow, except onto a line —
        // a T-piece carries no arrowhead, the meeting itself is the marker). A free tine gets a handle below.
        if (!freeTine && !annotationLink && (conn.Arrow ?? !toTap))
        {
            var arrow = BuildArrow(pts[^2], pts[^1], brush);
            arrow.ZIndex = 1;
            _canvas.Children.Add(arrow); visuals.Add(arrow);
        }

        // Open handle at a free tine's tip: a hollow grabbable circle. Drag it onto a node (wire) or a line
        // (tap); right-click for the line menu; Remove-mode deletes the tine.
        if (freeTine)
        {
            const double hr = 5.5;
            var tip = pts[^1];
            var handle = new Ellipse
            {
                Width = hr * 2, Height = hr * 2, Stroke = brush, StrokeThickness = 1.6,
                Fill = Brushes.Transparent, ZIndex = 4, Cursor = new Cursor(StandardCursorType.Hand),
            };
            Canvas.SetLeft(handle, tip.X - hr); Canvas.SetTop(handle, tip.Y - hr);
            var capTine = conn;
            handle.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(handle).Properties.IsRightButtonPressed) { ShowConnMenu(capTine, handle); e.Handled = true; return; }
                if (_mode == EditMode.Remove) { DeleteConnection(capTine); e.Handled = true; return; }
                // Connect mode: clicking a tine arms IT (not "next free"); the next node/line click wires it.
                if (ConnectMode)
                {
                    _armedTine = capTine; _connectFromId = capTine.FromId;
                    EnsureRubberBand();
                    if (_rubberBand is not null) { _rubberBand.StartPoint = tip; _rubberBand.EndPoint = tip; }
                    e.Handled = true; return;
                }
                _tineDrag = capTine;
                EnsureRubberBand();
                if (_rubberBand is not null) { _rubberBand.StartPoint = tip; _rubberBand.EndPoint = tip; }
                e.Pointer.Capture(_canvas);
                e.Handled = true;
            };
            _canvas.Children.Add(handle); visuals.Add(handle);
        }

        // DIN departure marker: a small filled dot where the flow line leaves the source edge. Comb teeth
        // start on the spine, not a node edge, so they get no dot.
        if (!_data.DiagonalLines && !combTine)
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
            // for a straight arrow at the same height as its target (it grows a fresh knick). Only a FREE
            // comb tine is locked (its open tip handle is the interaction, and it has no target to route to —
            // bending it would vanish it); a WIRED tine can be hand-routed, taking it out of the auto comb.
            // Lock only free tines (their tip handle is the interaction). A wired tooth — single comb or L —
            // is hand-draggable; an L tooth's drag is re-anchored to the bar (no stray line at the node).
            bool draggable = !_data.DiagonalLines && !freeTine;
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
                // Dragging the END segment of a tap (the one touching the target) slides its meeting point
                // along the target; other segments bend normally so an L-shaped tap stays adjustable. Works
                // in any non-Remove mode (an in-progress connection was handled just before).
                if (_mode != EditMode.Remove && !string.IsNullOrEmpty(capConn.ToTapConn)
                    && _connPts.TryGetValue(capConn.Id, out var tpts) && segIdx == tpts.Count - 2)
                { _tapDrag = capConn; e.Pointer.Capture(_canvas); e.Handled = true; return; }
                // A wired comb tooth has three grab zones: the arrowhead END re-targets onto another side;
                // the FIRST segment (touching the bar) slides the slot along the bar; everything in between —
                // the knick and the second half toward the tip — bends freely (stored as the tine's waypoints).
                if (combTine && !freeTine)
                {
                    bool wired = !string.IsNullOrEmpty(capConn.ToId)
                                 && _connPts.TryGetValue(capConn.Id, out var rp) && rp.Count >= 2;
                    if (wired)
                    {
                        var gp = e.GetPosition(_canvas); var rpts = _connPts[capConn.Id];
                        double gg = _data.GridSize >= 4 ? _data.GridSize : 10;
                        if (Dist(gp, rpts[^1]) < 1.6 * gg) _toothEndDrag = capConn;   // arrowhead → re-target
                        else if (segIdx == 0) { _toothDrag = capConn; _toothGrabCur = gp; _toothGrabOffset = capConn.TineOffset; }   // bar segment → slide slot
                        else { BeginSegmentDrag(capConn, segIdx, e); e.Handled = true; return; }   // middle/tip half → bend
                    }
                    else { _toothDrag = capConn; _toothGrabCur = e.GetPosition(_canvas); _toothGrabOffset = capConn.TineOffset; }
                    e.Pointer.Capture(_canvas); e.Handled = true; return;
                }
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

        // A comb tine (free or wired) labels its case at the TOP of its own tooth — so each label sits over
        // its tine, spread along the comb, and stays put when the tine gets wired (instead of piling up at
        // the shared spine or jumping to the target end).
        if (_data.Nodes.FirstOrDefault(n => n.Id == conn.FromId) is { Kind: FlowNodeKind.MultiDecision } mnode)
        {
            double g = _data.GridSize >= 4 ? _data.GridSize : 10;
            var comb = TineComb(mnode, conn);
            int idx = Math.Max(0, CombTines(mnode, comb).FindIndex(c => c.Id == conn.Id));
            var slot = CombSlot(mnode, comb, idx, conn.TineOffset, g).slot;   // label sits just off the slot
            return comb == CombDirection.Right
                ? (new Point(slot.X + 12, slot.Y), true)
                : (new Point(slot.X, slot.Y + 12), false);
        }

        Point sa, sb;
        bool fromDecision = _data.Nodes.FirstOrDefault(n => n.Id == conn.FromId) is { } fn && IsDecision(fn.Kind);
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
            var raw = e.GetPosition(_canvas);
            var q = new Point(Snap(raw.X), Snap(raw.Y));   // snap to the grid, so the label steps along the line
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
        bool toTap    = !string.IsNullOrEmpty(conn.ToTapConn);   // matches RenderConnection's default
        bool hasArrow = conn.Arrow ?? !toTap;
        var style = new MenuItem { Header = hasArrow ? Loc.S("Flow_StyleLine") : Loc.S("Flow_StyleArrow") };
        style.Click += (_, _) => { conn.Arrow = !hasArrow; Save(); RenderConnection(conn); };
        cm.Items.Add(style);

        // Flipping swaps the two node ends. It's meaningless (and would blank FromId, vanishing the line)
        // for a tap or a free comb tine (no ToId), and reversing a Multi-Verzweigung tine would break its
        // comb — so offer it only for plain node-to-node lines.
        bool fromMulti = _data.Nodes.FirstOrDefault(n => n.Id == conn.FromId)?.Kind == FlowNodeKind.MultiDecision;
        if (!toTap && !string.IsNullOrEmpty(conn.ToId) && !fromMulti)
        {
            var flip = new MenuItem { Header = Loc.S("Flow_FlipArrow") };
            flip.Click += (_, _) => { (conn.FromId, conn.ToId) = (conn.ToId, conn.FromId); Save(); RenderConnection(conn); };
            cm.Items.Add(flip);
        }
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
        // Any line tapping onto a re-routed line must follow its new path — and lines tapping onto THOSE
        // (nested taps), recursively, so a whole tap chain re-renders instead of leaving the deeper ones
        // stale/collapsed.
        foreach (var id in affected) RenderTapChain(id, 0);
        RenderTapDots();
        RenderCombHandles();
    }

    // Draws every rigidly-moving line as its pre-move route translated by the delta — a pure shift, no
    // re-routing, so the shape is identical to before the move.
    void TranslateRigid(double dx, double dy)
    {
        if (_dragRoute is null) return;
        foreach (var (cid, route) in _dragRoute)
        {
            if (_data.Connections.FirstOrDefault(x => x.Id == cid) is not { } c) continue;
            var tpts = route.Select(p => new Point(p.X + dx, p.Y + dy)).ToList();
            _connPts[cid] = tpts;
            RenderConnection(c, tpts);
        }
        RenderTapDots();
    }

    // Shifts the geometry that travels rigidly with a group move (from the drag-start snapshots) by the
    // cumulative delta — tap anchors and manual waypoints — exactly like paste offsets them.
    void ShiftDraggedGeometry(double dx, double dy)
    {
        if (_dragTapStart is not null)
            foreach (var (cid, a) in _dragTapStart)
                if (_data.Connections.FirstOrDefault(x => x.Id == cid) is { } tc) { tc.ToTapX = a.X + dx; tc.ToTapY = a.Y + dy; }
        if (_dragWpStart is not null)
            foreach (var (cid, wps) in _dragWpStart)
                if (_data.Connections.FirstOrDefault(x => x.Id == cid) is { } wc)
                    for (int i = 0; i < wc.Waypoints.Count && i < wps.Count; i++)
                    { wc.Waypoints[i].X = wps[i].X + dx; wc.Waypoints[i].Y = wps[i].Y + dy; }
    }

    // After all nodes are at their final spot, render the affected lines ONCE: lines touching a moved node,
    // plus the rigidly-moved lines (whose data was just shifted), plus their tap chains. Rendering only
    // after everything's placed avoids the half-moved, collapsing/jumping intermediate states.
    void RerouteAfterMove(HashSet<string> movedSet)
    {
        // 1) Which connections need re-rendering: those touching a moved node or whose geometry was shifted,
        //    plus everything tapping onto them (transitively).
        // Rigid lines are already drawn by TranslateRigid — exclude them here so they're never re-routed.
        bool Rigid(string id) => _dragRoute?.ContainsKey(id) ?? false;
        var need = new HashSet<string>();
        foreach (var c in _data.Connections)
            if (!Rigid(c.Id) && (movedSet.Contains(c.FromId) || movedSet.Contains(c.ToId)))
                need.Add(c.Id);
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var c in _data.Connections)
                if (!Rigid(c.Id) && !string.IsNullOrEmpty(c.ToTapConn) && need.Contains(c.ToTapConn) && need.Add(c.Id)) grew = true;
        }

        // 2) Render in dependency order: a line renders once its target is rendered (or its target isn't in
        //    the set / is a node), so a tap always sees its target's up-to-date points. Avoids the stale
        //    projection that wobbled deep tap chains.
        var done = new HashSet<string>();
        for (bool progress = true; progress;)
        {
            progress = false;
            foreach (var id in need)
            {
                if (done.Contains(id) || _data.Connections.FirstOrDefault(x => x.Id == id) is not { } c) continue;
                bool ready = string.IsNullOrEmpty(c.ToTapConn) || !need.Contains(c.ToTapConn) || done.Contains(c.ToTapConn);
                if (!ready) continue;
                if (c.Waypoints.Count > 0 && !(_dragWpStart?.ContainsKey(c.Id) ?? false)) AlignManualEnds(c);
                RenderConnection(c); done.Add(id); progress = true;
            }
        }
        foreach (var id in need)   // any leftover (e.g. a tap cycle) — render anyway
            if (!done.Contains(id) && _data.Connections.FirstOrDefault(x => x.Id == id) is { } c) RenderConnection(c);
        RenderTapDots();
        RenderCombHandles();   // keep the spine grab-bars on their moving diamonds
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
        var exit = EdgeMid(a.Value, new Point(w0.X, w0.Y));   // exit stays centred (see ManualRoute)
        if (Math.Abs(exit.Y - w0.Y) >= Math.Abs(exit.X - w0.X)) w0.X = exit.X;   // vertical stub → align X
        else                                                    w0.Y = exit.Y;   // horizontal stub → align Y

        var wl = c.Waypoints[^1];
        var entry = EdgeSlide(b.Value, new Point(wl.X, wl.Y));
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
        foreach (var c in _data.Connections)
        {
            foreach (var w in c.Waypoints) { w.X += dx; w.Y += dy; }
            if (!string.IsNullOrEmpty(c.ToTapConn)) { c.ToTapX += dx; c.ToTapY += dy; }   // taps move with the world too
        }
        if (_scroll is not null) _scroll.Offset = new Vector(Math.Max(0, _scroll.Offset.X + dx * _zoom), Math.Max(0, _scroll.Offset.Y + dy * _zoom));
        foreach (var (id, v) in _nodeViews)
            if (_data.Nodes.FirstOrDefault(n => n.Id == id) is { } nd) { Canvas.SetLeft(v, nd.X); Canvas.SetTop(v, nd.Y); }
        foreach (var c in _data.Connections) RenderConnection(c);
    }

    // Fits the canvas snugly around the content with a uniform margin — so it grows when you push to an
    // edge and shrinks back when you move away (all four sides). Called when an edit settles.
    // <param name="trim">When true (the "Crop" action), also pull content up/left to remove top/left
    // whitespace. The automatic call (false) only grows that side, preserving a centered layout.</param>
    void FitCanvas(bool trim = false, bool keepPosition = false)
    {
        if (_canvas is null) return;
        if (!_data.Nodes.Any(n => n.Page == _page)) { _canvas.Width = 320; _canvas.Height = 240; if (_gridRect is not null) { _gridRect.Width = 320; _gridRect.Height = 240; } return; }
        const double pad = 80;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Inc(double x, double y) { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); }
        foreach (var n in _data.Nodes.Where(n => n.Page == _page)) { Inc(n.X, n.Y); Inc(n.X + n.Width, n.Y + n.Height); }
        foreach (var c in _data.Connections.Where(c => NodePage(c.FromId) == _page)) foreach (var w in c.Waypoints) Inc(w.X, w.Y);

        // Shift by a whole number of grid cells, so everything stays aligned to the grid (which tiles from
        // 0,0) — a non-grid shift would knock all placements off the grid.
        // Only ever GROW toward the top-left (dx/dy >= 0). Never pull content leftward/upward to trim a
        // margin: a deliberately centered layout (e.g. a tree fanning down from a top-centre start) must
        // keep its left/top whitespace. Right/bottom still shrink, since that just resizes the surface.
        double g = _data.GridSize >= 1 ? _data.GridSize : 10;
        double dx = Math.Round((pad - minX) / g) * g, dy = Math.Round((pad - minY) / g) * g;
        if (keepPosition)
        {
            // After a user move: leave the content exactly where they put it; only shift to rescue content
            // that went off the top/left (negative coords a Canvas can't hold). The surface still shrinks on
            // the right/bottom below, so moving content left frees up space instead of shoving it back.
            dx = minX < 0 ? Math.Max(0, dx) : 0;
            dy = minY < 0 ? Math.Max(0, dy) : 0;
        }
        else if (!trim) { dx = Math.Max(0, dx); dy = Math.Max(0, dy); }   // auto-fit grows only; Crop also trims
        ShiftWorld(dx, dy);   // bring the top-left of the content near the margin

        // The surface hugs the content (+ a small working margin), with only a modest floor so it never
        // collapses to nothing. It deliberately does NOT fill the viewport — an empty chart shows a small page
        // with window background around it (dragging a node to the edge grows it again via GrowCanvasFor).
        const double minW = 320, minH = 240;
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

    // ── Off-page connectors / pages ────────────────────────────────────────

    static bool IsOffPage(FlowNode n) => n.Kind == FlowNodeKind.Connector && n.Symbol == FlowSymbol.OffPageConnector;

    // A readable default off-page label: "Page 2", "Page 3", … — the first number not already used by an
    // existing off-page exit (page 1 is the current/first page, so numbering starts at 2).
    string NextOffPageLabel()
    {
        var word = Loc.S("Flow_PageWord");
        var used = _data.Nodes.Where(n => IsOffPage(n) && !n.OffPageEntry)
            .Select(n => n.Text.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int n = 2; ; n++) { var s = $"{word} {n}"; if (!used.Contains(s)) return s; }
    }

    // Double-clicking an off-page EXIT jumps to (creating if needed) the page holding its ENTRY.
    void OpenOffPage(FlowNode exit)
    {
        var entry = _data.Nodes.FirstOrDefault(n => n.OffPageEntry && n.OffPagePair == exit.OffPagePair);
        if (entry is null)
        {
            entry = new FlowNode
            {
                Kind = FlowNodeKind.Connector, Symbol = FlowSymbol.OffPageConnector,
                OffPageEntry = true, OffPagePair = exit.OffPagePair, Text = exit.Text,
                Page = MaxPage() + 1, X = 80, Y = 80, Width = exit.Width, Height = exit.Height,
            };
            _data.Nodes.Add(entry);
            Save();
        }
        else if (entry.Text != exit.Text) { entry.Text = exit.Text; Save(); }
        GoToPage(entry.Page);
    }

    // Keeps a moved/edited exit's entry label mirrored, and enforces one unique label per exit.
    void SyncOffPageEntry(FlowNode exit)
    {
        var entry = _data.Nodes.FirstOrDefault(n => n.OffPageEntry && n.OffPagePair == exit.OffPagePair);
        if (entry is not null && entry.Text != exit.Text) { entry.Text = exit.Text; }
    }

    void GoToPage(int page)
    {
        _page = Math.Clamp(page, 0, MaxPage());
        RebuildAll();
        if (_scroll is not null) _scroll.Offset = default;
        UpdatePageLabel();
    }

    void UpdatePageLabel()
    {
        if (_pageLabel is not null) _pageLabel.Text = string.Format(Loc.S("Flow_PageOf"), _page + 1, MaxPage() + 1);
    }

    // Opens (or creates, in the Functions library) the function a subroutine node calls, then its diagram.
    async void ShowChartFlow(FlowNode node)
    {
        if (string.IsNullOrEmpty(node.RefId))
        {
            var r = await SubroutineLinkDialog.Show(this, _projFolder, "", _key);
            if (r is null || string.IsNullOrEmpty(r.Id)) return;
            node.RefId = r.Id; node.Text = SubroutineLinkDialog.CallText(_projFolder, r); Save();
            if (_nodeViews.TryGetValue(node.Id, out var v)) { _canvas!.Children.Remove(v); _nodeViews.Remove(node.Id); }
            RenderNode(node); UpdateConnectionsFor(node.Id);
        }
        _ = DiagramLauncher.ChooseAndOpen(this, _projFolder, node.RefId,
            SubroutineLinkDialog.RefName(_projFolder, node.RefId), _themePath);
    }

    // Re-points an already-linked subroutine node at a different target (or re-does its call form).
    async Task RelinkSubroutine(FlowNode node)
    {
        var r = await SubroutineLinkDialog.Show(this, _projFolder, "", _key);
        if (r is null || string.IsNullOrEmpty(r.Id)) return;
        node.RefId = r.Id; node.Text = SubroutineLinkDialog.CallText(_projFolder, r); Save();
        if (_nodeViews.TryGetValue(node.Id, out var v)) { _canvas!.Children.Remove(v); _nodeViews.Remove(node.Id); }
        RenderNode(node); UpdateConnectionsFor(node.Id);
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
        var gone = _data.Nodes.FirstOrDefault(n => n.Id == id);
        // An off-page ENTRY can't be deleted directly — it only goes away when its exit does.
        if (gone is { OffPageEntry: true }) return;
        _data.Nodes.RemoveAll(n => n.Id == id);
        // Deleting an off-page EXIT removes its paired entry too (possibly on another page).
        if (gone is not null && IsOffPage(gone) && !gone.OffPageEntry)
            _data.Nodes.RemoveAll(n => n.OffPageEntry && n.OffPagePair == gone.OffPagePair);
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
        // Lines that tapped onto this one lose their anchor — remove them, and anything tapping THOSE
        // (nested), transitively.
        var doomed = new List<FlowConnection> { conn };
        for (int i = 0; i < doomed.Count; i++)
            doomed.AddRange(_data.Connections.Where(c => c.ToTapConn == doomed[i].Id && !doomed.Contains(c)));
        foreach (var c in doomed)
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

    // DIN 66001 comment / Bemerkung: an open square bracket "[" — a full-height vertical with short ticks at
    // top and bottom — that hangs off an element via a dashed line. Mirrored = "]" (spine on the right), so the
    // bracket and its connection point face an element on the other side.
    static Grid AnnotationShape(Color fill, Color stroke, bool mirrored)
    {
        var sb = new SolidColorBrush(stroke);
        const double tick = 14, t = 1.5;
        var side = mirrored ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        var g = new Grid { Background = new SolidColorBrush(fill) };   // fill = hit-test surface for the whole area
        g.Children.Add(new Rectangle { Width = t,    Fill = sb, HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Stretch });
        g.Children.Add(new Rectangle { Width = tick, Height = t, Fill = sb, HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Top });
        g.Children.Add(new Rectangle { Width = tick, Height = t, Fill = sb, HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Bottom });
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
        FlowNodeKind.MultiDecision => (Color.FromRgb(0xFF, 0xF1, 0xC4), Color.FromRgb(0xF5, 0x7F, 0x17)),
        FlowNodeKind.InputOutput => (Color.FromRgb(0xBB, 0xDE, 0xFB), Color.FromRgb(0x15, 0x65, 0xC0)),
        FlowNodeKind.Subroutine  => (Color.FromRgb(0xD1, 0xC4, 0xE9), Color.FromRgb(0x51, 0x2D, 0xA8)),
        FlowNodeKind.Comment     => (Color.FromRgb(0xEC, 0xEF, 0xF1), Color.FromRgb(0x45, 0x5A, 0x64)),
        FlowNodeKind.Annotation  => (Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), Color.FromRgb(0x45, 0x5A, 0x64)),
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

    // The outward unit normal of the rectangle edge a border point sits on (for the straight exit stub).
    static Point Outward(Rect r, Point p)
    {
        if (Math.Abs(p.X - r.Left)  < 0.5) return new(-1, 0);
        if (Math.Abs(p.X - r.Right) < 0.5) return new(1, 0);
        if (Math.Abs(p.Y - r.Top)   < 0.5) return new(0, -1);
        return new(0, 1);   // bottom
    }

    List<Point> OrthoRoute(Rect s, Rect t, ISet<char>? srcBusy = null, ISet<char>? dstBusy = null)
    {
        double g = _data.GridSize >= 4 ? _data.GridSize : 10;
        var sc = s.Center; var tc = t.Center;
        double dx = tc.X - sc.X, dy = tc.Y - sc.Y, adx = Math.Abs(dx), ady = Math.Abs(dy);
        bool down = dy > 0, right = dx > 0;
        bool Busy(char c) => srcBusy?.Contains(c) ?? false;

        // Departure-edge rule (auto-routing only): prefer the forward edges BOTTOM / RIGHT. Leave LEFT only
        // when the target is left AND below (never above) and the left edge is free. Backward (upward)
        // targets — e.g. a loop — leave to the RIGHT and go around, not out the left/top where the incoming
        // arrows usually sit.
        char ex;
        if (right && down) ex = ady >= adx ? 'B' : 'R';                // down-right by dominance
        else if (right)    ex = 'R';                                   // up-right
        else if (down)     ex = (adx > ady && !Busy('L')) ? 'L' : 'B'; // down-left: left if free+dominant, else bottom
        else               ex = 'R';                                   // up / up-left / straight up → loop to the right
        if (Busy(ex)) foreach (var c in new[] { 'R', 'B', 'T', 'L' }) { if (!Busy(c)) { ex = c; break; } }

        bool exitVert = ex is 'B' or 'T';
        var exit = ex switch
        {
            'B' => new Point(sc.X, s.Bottom),
            'T' => new Point(sc.X, s.Top),
            'R' => new Point(s.Right, sc.Y),
            _   => new Point(s.Left, sc.Y),
        };
        // Entry edge of the target: the side actually FACING the source, chosen by the dominant axis of the
        // source→target vector — NOT by the exit edge's orientation. (Tying it to the exit made a line that
        // left sideways enter the target's bottom/top even when the source sat above/beside it — landing on
        // the bottom rim that's normally reserved for the target's own outgoing flow.)
        bool entryVert = ady >= adx;
        var entry = entryVert ? new Point(tc.X, dy >= 0 ? t.Top : t.Bottom)
                              : new Point(dx >= 0 ? t.Left : t.Right, tc.Y);

        // One grid-step straight stub out of each symbol, then turn — so lines always leave (and enter)
        // perpendicular before bending toward the target.
        var exitStub  = ex switch { 'B' => new Point(exit.X, exit.Y + g), 'T' => new Point(exit.X, exit.Y - g),
                                    'R' => new Point(exit.X + g, exit.Y), _ => new Point(exit.X - g, exit.Y) };
        var entryStub = entryVert ? new Point(entry.X, entry.Y + (dy >= 0 ? -g : g))   // outside the target edge
                                  : new Point(entry.X + (dx >= 0 ? -g : g), entry.Y);
        var mid = exitVert ? new Point(entryStub.X, exitStub.Y)    // after a vertical exit stub, turn horizontal
                           : new Point(exitStub.X, entryStub.Y);   // after a horizontal exit stub, turn vertical
        return Simplify(Orthogonalize(new() { exit, exitStub, mid, entryStub, entry }));
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
        // Also detect a route that runs straight THROUGH its own end node (they're excluded from obstacles
        // so it may start/end at their edge — but it must not cut across the body). Shrink the body so a
        // mere endpoint-touch doesn't count, only a genuine pass-through.
        var ownBodies = new List<Rect>();
        if (NodeRect(fromId) is { } frO) ownBodies.Add(frO.Inflate(-2));
        if (NodeRect(toId)   is { } toO) ownBodies.Add(toO.Inflate(-2));
        bool throughOwn = PolyHitsAny(simple, ownBodies);
        if (!hitsNodes && !onLines && !throughOwn) return simple;

        // Route A* between the STUB points (one grid straight out of each edge), keeping the exact edge
        // points as the literal ends — so the line still leaves and enters perpendicular even when it has
        // to detour around obstacles.
        var exitEdge = simple[0]; var entryEdge = simple[^1];
        var startPt = exitEdge; var goalPt = entryEdge;
        if (s.Width > 0 || s.Height > 0) { var od = Outward(s, exitEdge);  startPt = new(exitEdge.X + od.X * g, exitEdge.Y + od.Y * g); }
        if (t.Width > 0 || t.Height > 0) { var id = Outward(t, entryEdge); goalPt  = new(entryEdge.X + id.X * g, entryEdge.Y + id.Y * g); }

        // The search area must be wide/tall enough to actually route AROUND obstacles, not just span the
        // endpoints — so include every obstacle's extent, then a margin. (A narrow band left no room to go
        // around boxes wider than the band, and A* failed → fell back to the straight-through route.)
        double pad = 3 * g;
        double minX = Math.Min(startPt.X, goalPt.X), minY = Math.Min(startPt.Y, goalPt.Y);
        double maxX = Math.Max(startPt.X, goalPt.X), maxY = Math.Max(startPt.Y, goalPt.Y);
        foreach (var o in obstacles)
        {
            minX = Math.Min(minX, o.X); minY = Math.Min(minY, o.Y);
            maxX = Math.Max(maxX, o.Right); maxY = Math.Max(maxY, o.Bottom);
        }
        minX = Math.Max(0, minX - pad); minY = Math.Max(0, minY - pad);
        maxX += pad; maxY += pad;

        int cols = (int)Math.Round((maxX - minX) / g) + 1;
        int rows = (int)Math.Round((maxY - minY) / g) + 1;
        if (cols < 2 || rows < 2 || (long)cols * rows > 40000) return simple;

        // Block cells inside every obstacle AND inside the source/target BODIES (un-inflated) — otherwise
        // a line could run straight back through its own end node (they're excluded from `obstacles` only
        // so the route may start/end at their edge). The start/goal cells are freed again below.
        var blockRects = new List<Rect>(obstacles);
        if (NodeRect(fromId) is { } frB) blockRects.Add(frB);
        if (NodeRect(toId)   is { } toB) blockRects.Add(toB);

        var blk = new bool[cols, rows];
        for (int c = 0; c < cols; c++)
            for (int r = 0; r < rows; r++)
            {
                var p = new Point(minX + c * g, minY + r * g);
                foreach (var o in blockRects) if (o.Contains(p)) { blk[c, r] = true; break; }
            }

        // Soft penalty on cells lying on another line, so A* prefers a clear corridor but never fails.
        var pen = new double[cols, rows];
        const double linePenalty = 4.0;
        foreach (var seg in otherSegs) MarkSegment(pen, seg, minX, minY, g, cols, rows, linePenalty);

        int Cc(double v, int max) => Math.Clamp((int)Math.Round(v), 0, max);
        int sc = Cc((startPt.X - minX) / g, cols - 1), sr = Cc((startPt.Y - minY) / g, rows - 1);
        int gc = Cc((goalPt.X  - minX) / g, cols - 1), gr = Cc((goalPt.Y  - minY) / g, rows - 1);
        blk[sc, sr] = false; blk[gc, gr] = false;   // endpoints must be enterable
        pen[sc, sr] = 0; pen[gc, gr] = 0;            // don't punish entering/leaving at the endpoints

        var cells = AStar(blk, cols, rows, sc, sr, gc, gr, pen);
        if (cells is null) return simple;

        // Edge point → (stub via the first cell) → … → stub → edge point; drop redundant collinear points.
        var pts = new List<Point> { exitEdge };
        foreach (var (c, r) in cells) pts.Add(new Point(minX + c * g, minY + r * g));
        pts.Add(entryEdge);
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
        _armedTine = null;
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
        var juncSrc = _data.Nodes.FirstOrDefault(n => n.Id == from);
        if (juncSrc is not null && IsDecision(juncSrc.Kind))
        {
            var lbl = await PromptBranchLabel(juncSrc, "", null);
            if (lbl is null && juncSrc.Kind == FlowNodeKind.MultiDecision) return;
            fromLabel = lbl ?? "";
        }

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
