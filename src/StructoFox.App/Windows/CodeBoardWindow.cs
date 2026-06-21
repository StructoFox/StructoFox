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
using Avalonia.Reactive;
using Avalonia.Threading;
using OXSUIT.Loaders.Avalonia;
using StructoFox.Core;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// Canvas board for code structure (Classes, Functions, Interfaces, …) with typed input/output
/// ports and port-to-port connections. Cards are dragged, wired, selected and edited in place.
/// Avalonia port of ClaudetRelay's CodeBoardWindow — the fox's drafting table for whole programs.
/// </summary>
public class CodeBoardWindow : Window
{
    // ── State ────────────────────────────────────────────────────────────────
    readonly string    _projFolder;
    readonly CodeBoard _board;
    readonly string?   _themePath;
    readonly Action<IEnumerable<CodeEntity>>? _onExport;
    readonly string?   _bodyTargetKey;   // when set, this board defines a function/method body (Generate)
    CodeBoardData      _boardData;

    Canvas?       _canvas;
    LayoutTransformControl? _zoomHost;   // wraps the canvas so zoom scales the scrollable extent
    ScrollViewer? _scroll;

    readonly Dictionary<string, CodeEntity> _entities  = new();   // entity id → entity
    readonly Dictionary<string, Border>     _cards     = new();   // entity id → card
    readonly Dictionary<string, Ellipse>    _portDots  = new();   // "{entityId}:{portId}" → dot
    readonly Dictionary<string, List<Control>> _relViews = new(); // relation id → line/arrow/hit visuals

    // Snapshot-based undo/redo of the board (JSON of _boardData), recorded at each Save() boundary.
    readonly List<string> _undo = new();
    readonly List<string> _redo = new();
    string _snapshot = "";
    static readonly System.Text.Json.JsonSerializerOptions _undoJson = new() { PropertyNameCaseInsensitive = true };

    // Connect mode
    bool    _connectMode;
    string? _connectFromEntityId;
    string? _connectFromPortId;
    Line?   _rubberBand;
    readonly List<Control> _dragMarkers = new();   // ✕ overlays shown on invalid target ports while wiring

    // How well a candidate target port matches the source: same type + convention, type only, or neither.
    enum PortMatch { Exact, ConvMismatch, TypeMismatch }

    // Selection
    readonly HashSet<string> _selectedIds = new();
    string? _selectionAnchor;
    Point?  _mousePos;                          // last pointer pos over the canvas (null when outside)
    Dictionary<string, Point>? _dragStart;      // start positions of all selected cards during a multi-drag

    double _zoom = 1.0;

    Rectangle? _gridRect;                              // tiled alignment grid behind the cards
    bool   _panning;  Point _panStart;  Vector _panOrigin;   // right-drag canvas pan
    bool   _rightMaybeMenu;                            // a right press that opens the add-menu if it wasn't a drag
    bool   _rightCancelConnect;                        // a right press (connect mode) that cancels if it wasn't a drag

    const double PortRadius  = 6;
    const double DefaultCardW = 180;

    Button? _connectBtn;
    ContextMenu? _menu;   // the single open context menu, so opening a new one closes the old

    static readonly FontFamily Mono = new("Consolas, Cascadia Mono, monospace");
    static readonly BoxShadows SelGlow   = BoxShadows.Parse("0 0 12 0 #CC2196F3");
    static readonly BoxShadows HoverGlow = BoxShadows.Parse("0 0 8 0 #66FFFFFF");

    void OpenMenu(ContextMenu cm, Control anchor) { _menu?.Close(); _menu = cm; cm.Open(anchor); }

    public CodeBoardWindow(string projFolder, CodeBoard board, string? themePath,
        Action<IEnumerable<CodeEntity>>? onExport = null)
    {
        _projFolder = projFolder;
        _board      = board;
        _themePath  = themePath;
        _onExport   = onExport;
        // A board authors a body when it carries an assignment — the single source of truth.
        _bodyTargetKey = string.IsNullOrWhiteSpace(board.TargetKey) ? null : board.TargetKey;
        _boardData  = CodeBoardDataService.Load(projFolder, board.Id);
        _snapshot   = System.Text.Json.JsonSerializer.Serialize(_boardData);   // baseline for undo

        Title                 = board.Symbol + "  " + board.Name;
        Width                 = 1280;
        Height                = 800;
        MinWidth              = 640;
        MinHeight             = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        if (!string.IsNullOrWhiteSpace(themePath))
            try { Resources.MergedDictionaries.Add(OxsuitLoader.Load(themePath)); } catch { /* unthemed is fine */ }
        Ui.ThemeWindow(this);
        ThemeManager.FixFluentBrushes(this);   // theme popups (context menus) at window scope

        BuildContent();
    }

    void Save()
    {
        var cur = System.Text.Json.JsonSerializer.Serialize(_boardData);
        if (cur != _snapshot)
        {
            _undo.Add(_snapshot);
            if (_undo.Count > 100) _undo.RemoveAt(0);
            _redo.Clear();
            _snapshot = cur;
        }
        CodeBoardDataService.Save(_projFolder, _board.Id, _boardData);
    }

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

    // Restores a serialized board state, persists it and rebuilds the canvas.
    void ApplySnapshot(string json)
    {
        CodeBoardData? d;
        try { d = System.Text.Json.JsonSerializer.Deserialize<CodeBoardData>(json, _undoJson); } catch { return; }
        if (d is null) return;
        _boardData = d;
        CodeBoardDataService.Save(_projFolder, _board.Id, _boardData);
        RebuildBoard();
    }

    // Clears and re-renders the whole board from _boardData (after an undo/redo).
    void RebuildBoard()
    {
        if (_canvas is null) return;
        _canvas.Children.Clear();
        _cards.Clear(); _portDots.Clear(); _relViews.Clear();
        _gridRect = null;
        _selectedIds.Clear(); _selectionAnchor = null;
        RenderGrid();
        foreach (var kv in _boardData.Positions.ToList())
        {
            if (_entities.TryGetValue(kv.Key, out var entity)) RenderCard(entity, kv.Value);
            else _boardData.Positions.Remove(kv.Key);
        }
        Dispatcher.UIThread.Post(RenderAllRelations, DispatcherPriority.Loaded);
    }

    // ── Build ──────────────────────────────────────────────────────────────

    void BuildContent()
    {
        // Load every entity so cards, relations and editor dropdowns can resolve ids.
        foreach (var t in CodeEntityService.EntityTypes)
            foreach (var e in CodeEntityService.LoadAll(_projFolder, t))
                _entities[e.Id] = e;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        Content = root;

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

        _canvas = new Canvas { Width = 3000, Height = 2000, ClipToBounds = false };
        Ui.Theme(_canvas, Canvas.BackgroundProperty, "ContentBgBrush");
        // Wrap the canvas so zoom can use a LayoutTransform (scales the scrollable extent too). Pin it
        // top-left so a zoomed-out / shrunk canvas stays anchored there instead of drifting to the edge.
        _zoomHost = new LayoutTransformControl
        {
            Child = _canvas,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Top,
        };
        _scroll.Content = _zoomHost;

        KeyDown += (_, e) => HandleKey(e);
        Focusable = true;

        _canvas.PointerExited   += (_, _) => _mousePos = null;
        _canvas.PointerPressed  += Canvas_PointerPressed;
        _canvas.PointerMoved    += Canvas_PointerMoved;
        _canvas.PointerReleased += Canvas_PointerReleased;

        // Accept entities dragged in from the cockpit's entity lists.
        DragDrop.SetAllowDrop(_canvas, true);
        _canvas.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        _canvas.AddHandler(DragDrop.DropEvent, OnDrop);

        // Ctrl + wheel zooms toward the pointer. Tunnel handler on the scroll viewer so it fires before
        // anything under the cursor and in every mode, and before the viewer's own scroll.
        _scroll.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            e.Handled = true;
            ZoomAt(_zoom + (e.Delta.Y > 0 ? 0.1 : -0.1), e.GetPosition(_scroll));
        }, RoutingStrategies.Tunnel);

        RenderGrid();

        // Render saved cards (dropping any whose entity vanished from disk).
        foreach (var kv in _boardData.Positions.ToList())
        {
            if (_entities.TryGetValue(kv.Key, out var entity)) RenderCard(entity, kv.Value);
            else _boardData.Positions.Remove(kv.Key);
        }

        // Relations need card sizes; draw them once layout has settled.
        Dispatcher.UIThread.Post(RenderAllRelations, DispatcherPriority.Loaded);
    }

    Border BuildToolbar()
    {
        var bar = new Border { Padding = new(12, 8, 12, 8) };
        Ui.Theme(bar, Border.BackgroundProperty, "SidebarBgBrush");

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        bar.Child = row;

        var addBtn = Btn(Loc.S("Code_AddToBoard"), Loc.S("Code_AddToBoardTip"));
        addBtn.Click += (_, _) => ShowAddEntityMenu(addBtn);
        row.Children.Add(addBtn);

        _connectBtn = Btn(Loc.S("Code_ConnectPorts"), Loc.S("Code_ConnectPortsTip"));
        _connectBtn.Click += (_, _) => ToggleConnectMode();
        row.Children.Add(_connectBtn);

        var delBtn = Btn(Loc.S("Code_RemoveCards"), Loc.S("Code_RemoveCardsTip"));
        delBtn.Click += (_, _) => RemoveSelectedFromBoard();
        row.Children.Add(delBtn);

        // When this board authors a function/method body, offer to generate it from the wiring.
        if (_bodyTargetKey is not null)
        {
            row.Children.Add(new Border { Width = 8 });
            var genBtn = Btn(Loc.S("Code_GenBody"), Loc.S("Code_GenBodyTip"));
            genBtn.Click += async (_, _) => await GenerateBody();
            row.Children.Add(genBtn);
        }

        if (_onExport is not null)
        {
            row.Children.Add(new Border { Width = 8 });
            var exportAllBtn = Btn(Loc.S("Code_ExportAll"), Loc.S("Code_ExportAllTip"));
            exportAllBtn.Click += (_, _) => _onExport!.Invoke(AllBoardEntities());
            row.Children.Add(exportAllBtn);

            var exportSelBtn = Btn(Loc.S("Code_ExportSelected"), Loc.S("Code_ExportSelectedTip"));
            exportSelBtn.Click += async (_, _) =>
            {
                var sel = SelectedEntities();
                if (sel.Count == 0) { await MessageDialog.Show(this, Loc.S("Code_NoSelection"), Loc.S("Code_ExportSelected")); return; }
                _onExport!.Invoke(sel);
            };
            row.Children.Add(exportSelBtn);
        }

        row.Children.Add(new Border { Width = 8 });
        var viewBtn = Btn(Loc.S("Flow_View"), Loc.S("Flow_ViewTip"));
        viewBtn.Flyout = BuildViewFlyout();
        row.Children.Add(viewBtn);

        return bar;
    }

    // The "View" flyout: zoom reset + grid controls (show / snap / style / colour / opacity).
    Flyout BuildViewFlyout()
    {
        var panel = new StackPanel { Spacing = 10, MinWidth = 230, Margin = new(4) };
        Ui.Theme(panel, TextElement.ForegroundProperty, "ContentTextBrush");

        var zoom = Btn(Loc.S("Common_ResetZoomTip"));
        zoom.Click += (_, _) => SetZoom(1.0);
        panel.Children.Add(zoom);

        panel.Children.Add(new Separator());
        panel.Children.Add(new TextBlock { Text = Loc.S("Grid_Header"), FontWeight = FontWeight.Bold });

        var show = new CheckBox { Content = Loc.S("Grid_Show"), IsChecked = _boardData.GridVisible };
        show.IsCheckedChanged += (_, _) => { _boardData.GridVisible = show.IsChecked == true; RenderGrid(); Save(); };
        panel.Children.Add(show);

        var snap = new CheckBox { Content = Loc.S("Grid_Snap"), IsChecked = _boardData.SnapToGrid };
        snap.IsCheckedChanged += (_, _) =>
        {
            _boardData.SnapToGrid = snap.IsChecked == true;
            // Re-place every card's ports so they (un)snap to the grid immediately, then redraw links.
            foreach (var id in _cards.Keys.ToList()) UpdatePortPositions(id);
            RenderAllRelations();
            Save();
        };
        panel.Children.Add(snap);

        var styleCombo = Ui.Combo();
        styleCombo.Items.Add(new ComboItem(Loc.S("Grid_Lines"),  nameof(GridLineStyle.Lines)));
        styleCombo.Items.Add(new ComboItem(Loc.S("Grid_Dashed"), nameof(GridLineStyle.Dashed)));
        styleCombo.Items.Add(new ComboItem(Loc.S("Grid_Dots"),   nameof(GridLineStyle.Dots)));
        styleCombo.SelectedItem = styleCombo.Items.OfType<ComboItem>().FirstOrDefault(c => c.Id == _boardData.GridStyle.ToString()) ?? styleCombo.Items[0];
        styleCombo.SelectionChanged += (_, _) =>
        {
            if ((styleCombo.SelectedItem as ComboItem)?.Id is { } id && Enum.TryParse<GridLineStyle>(id, out var gs))
            { _boardData.GridStyle = gs; RenderGrid(); Save(); }
        };
        panel.Children.Add(styleCombo);

        var color = Btn(Loc.S("Grid_Color"));
        color.Click += async (_, _) =>
        {
            var hex = await ColorPickDialog.Pick(this, Loc.S("Grid_Color"), _boardData.GridColor);
            if (hex is null) return;
            _boardData.GridColor = hex; RenderGrid(); Save();
        };
        panel.Children.Add(color);

        panel.Children.Add(new TextBlock { Text = Loc.S("Grid_Opacity"), FontSize = 11, Opacity = 0.8 });
        var op = new Slider { Minimum = 0, Maximum = 1, Value = _boardData.GridOpacity, SmallChange = 0.05, LargeChange = 0.1 };
        op.PropertyChanged += (_, ev) => { if (ev.Property == Slider.ValueProperty) { _boardData.GridOpacity = op.Value; RenderGrid(); } };
        op.PointerCaptureLost += (_, _) => Save();
        panel.Children.Add(op);

        return new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
    }

    static string Inv(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    // Keyboard: Delete removes the selection; Ctrl+Z/Y undo/redo; Ctrl+0 resets zoom, Ctrl +/- and
    // Ctrl+Up/Down zoom.
    // ── Copy / paste: duplicates the selected entities (new ids + new port ids) and the relations among
    // them, so pasted cards are independent copies wired up correctly. Shared across board windows. ──
    sealed class BoardClip
    {
        public List<CodeEntity>                     Entities  { get; set; } = new();
        public Dictionary<string, CodeCardPosition> Positions { get; set; } = new();   // keyed by old entity id
        public List<CodeRelation>                   Relations { get; set; } = new();
    }
    static string? _boardClip;

    void CopySelection()
    {
        if (_selectedIds.Count == 0) return;
        var clip = new BoardClip
        {
            Entities  = _selectedIds.Where(_entities.ContainsKey).Select(id => _entities[id]).ToList(),
            Positions = _selectedIds.Where(_boardData.Positions.ContainsKey).ToDictionary(id => id, id => _boardData.Positions[id]),
            Relations = _boardData.Relations.Where(r => _selectedIds.Contains(r.FromId) && _selectedIds.Contains(r.ToId)).ToList(),
        };
        _boardClip = System.Text.Json.JsonSerializer.Serialize(clip);
    }

    void PasteClipboard()
    {
        if (_boardClip is null) return;
        BoardClip? p;
        try { p = System.Text.Json.JsonSerializer.Deserialize<BoardClip>(_boardClip, _undoJson); } catch { return; }
        if (p is null || p.Entities.Count == 0) return;

        // Offset the group so its top-left lands at the cursor (if inside the canvas), else cascade. The
        // offset is snapped ONCE (a grid-multiple delta) and applied uniformly — relative layout is kept
        // exactly, so no card drifts by a cell relative to the others.
        var poss = p.Positions.Values.ToList();
        double minX = poss.Count > 0 ? poss.Min(q => q.X) : 0, minY = poss.Count > 0 ? poss.Min(q => q.Y) : 0;
        double offX, offY;
        if (_mousePos is { } m) { offX = Snap(m.X - minX); offY = Snap(m.Y - minY); }
        else                    { offX = Snap(24); offY = Snap(24); }

        string Nid() => Guid.NewGuid().ToString("N")[..8];
        var entMap = new Dictionary<string, string>();
        var portMap = new Dictionary<string, string>();
        _selectedIds.Clear();

        foreach (var e in p.Entities)
        {
            // Only the entity itself + its ports get fresh ids. Cross-references are deliberately NOT
            // remapped, so they stay pointing at the originals: an Object keeps InstanceOfId (stays an
            // instance of the SAME class — many objects of one class is normal), and BaseClassId /
            // ImplementsIds / Namespace stay as-is too.
            var oldE = e.Id; e.Id = Nid(); entMap[oldE] = e.Id;
            foreach (var port in e.Ports) { var oldP = port.Id; port.Id = Nid(); portMap[oldP] = port.Id; }
            CodeEntityService.Save(_projFolder, e.EntityType.ToString(), e);   // a copy is a new entity
            _entities[e.Id] = e;

            var pos = p.Positions.TryGetValue(oldE, out var pp) ? pp : new CodeCardPosition();
            pos.X = Math.Max(0, pos.X + offX); pos.Y = Math.Max(0, pos.Y + offY);
            _boardData.Positions[e.Id] = pos;
            RenderCard(e, pos);
            _selectedIds.Add(e.Id);
        }
        foreach (var r in p.Relations)
        {
            if (!entMap.TryGetValue(r.FromId, out var nf) || !entMap.TryGetValue(r.ToId, out var nt)) continue;
            r.Id = Nid(); r.FromId = nf; r.ToId = nt;
            if (portMap.TryGetValue(r.FromPortId, out var fp)) r.FromPortId = fp;
            if (portMap.TryGetValue(r.ToPortId, out var tp)) r.ToPortId = tp;
            _boardData.Relations.Add(r);
        }
        Save();
        Dispatcher.UIThread.Post(() => { RenderAllRelations(); RefreshSelectionVisuals(); }, DispatcherPriority.Loaded);
    }

    void HandleKey(KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _selectedIds.Count > 0) { RemoveSelectedFromBoard(); return; }
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.C:                                    CopySelection(); e.Handled = true; break;
            case Key.V:                                    PasteClipboard(); e.Handled = true; break;
            case Key.Z when !shift:                        Undo(); e.Handled = true; break;
            case Key.Y or Key.Z:                           Redo(); e.Handled = true; break;
            case Key.D0 or Key.NumPad0:                    SetZoom(1.0); e.Handled = true; break;
            case Key.OemPlus or Key.Add or Key.Up:         SetZoom(_zoom + 0.1); e.Handled = true; break;
            case Key.OemMinus or Key.Subtract or Key.Down: SetZoom(_zoom - 0.1); e.Handled = true; break;
        }
    }

    // Applies the current zoom as a LayoutTransform so the scroll viewer can scroll the whole zoomed
    // canvas (a RenderTransform would leave the scrollable extent unscaled).
    void ApplyZoom()
    {
        if (_zoomHost is not null)
            _zoomHost.LayoutTransform = Math.Abs(_zoom - 1.0) < 0.001 ? null : new ScaleTransform(_zoom, _zoom);
    }

    // Keyboard / button zoom: anchor on the viewport centre.
    void SetZoom(double z)
    {
        if (_scroll is null) { _zoom = Math.Clamp(z, 0.25, 3.0); ApplyZoom(); return; }
        ZoomAt(z, new Point(_scroll.Viewport.Width / 2, _scroll.Viewport.Height / 2));
    }

    // Zooms to a clamped level while keeping the content point under the given viewport position fixed.
    void ZoomAt(double z, Point viewportPos)
    {
        z = Math.Clamp(z, 0.25, 3.0);
        if (_scroll is null || _canvas is null || Math.Abs(z - _zoom) < 0.0001) { _zoom = z; ApplyZoom(); return; }

        var off = _scroll.Offset;
        double cx = (off.X + viewportPos.X) / _zoom;
        double cy = (off.Y + viewportPos.Y) / _zoom;

        _zoom = z;
        ApplyZoom();

        Dispatcher.UIThread.Post(() =>
        {
            if (_scroll is null) return;
            _scroll.Offset = new Vector(Math.Max(0, cx * _zoom - viewportPos.X), Math.Max(0, cy * _zoom - viewportPos.Y));
        }, DispatcherPriority.Render);
    }

    // (Re)draws the alignment grid behind the cards as a single tiled brush.
    void RenderGrid()
    {
        if (_canvas is null) return;
        if (_gridRect is not null) { _canvas.Children.Remove(_gridRect); _gridRect = null; }
        if (!_boardData.GridVisible) return;

        double g = Math.Max(4, _boardData.GridSize);
        Color c; try { c = Color.Parse(_boardData.GridColor); } catch { c = Colors.Gray; }
        var brush = new SolidColorBrush(c, Math.Clamp(_boardData.GridOpacity, 0, 1));

        Drawing drawing;
        if (_boardData.GridStyle == GridLineStyle.Dots)
            drawing = new GeometryDrawing { Brush = brush, Geometry = new EllipseGeometry(new Rect(0, 0, 1.6, 1.6)) };
        else
        {
            var pen = new Pen(brush, 1);
            if (_boardData.GridStyle == GridLineStyle.Dashed) pen.DashStyle = new DashStyle(new double[] { 2, 2 }, 0);
            drawing = new GeometryDrawing { Pen = pen, Geometry = Geometry.Parse($"M0,0 L{Inv(g)},0 M0,0 L0,{Inv(g)}") };
        }

        _gridRect = new Rectangle
        {
            Width = _canvas.Width, Height = _canvas.Height, IsHitTestVisible = false, ZIndex = -1,
            Fill = new DrawingBrush(drawing)
            {
                TileMode = TileMode.Tile, Stretch = Stretch.None,
                DestinationRect = new RelativeRect(0, 0, g, g, RelativeUnit.Absolute),
            },
        };
        Canvas.SetLeft(_gridRect, 0); Canvas.SetTop(_gridRect, 0);
        _canvas.Children.Add(_gridRect);
    }


    void ToggleConnectMode()
    {
        _connectMode = !_connectMode;
        _connectFromEntityId = null;
        _connectFromPortId   = null;
        RemoveRubberBand();
        ClearDragHighlights();
        if (_connectBtn is not null)
        {
            _connectBtn.FontWeight = _connectMode ? FontWeight.Bold : FontWeight.Normal;
            Ui.Theme(_connectBtn, TemplatedControl.BackgroundProperty, _connectMode ? "AccentBgBrush"  : "ControlBgBrush");
            Ui.Theme(_connectBtn, TemplatedControl.ForegroundProperty, _connectMode ? "AccentTextBrush" : "SidebarTextBrush");
        }
        if (_canvas is not null)
            _canvas.Cursor = new Cursor(_connectMode ? StandardCursorType.Cross : StandardCursorType.Arrow);
    }

    // ── Card rendering ─────────────────────────────────────────────────────

    void RenderCard(CodeEntity entity, CodeCardPosition pos)
    {
        if (_cards.ContainsKey(entity.Id)) return;

        var card = BuildCard(entity);
        Canvas.SetLeft(card, pos.X);
        Canvas.SetTop(card,  pos.Y);
        card.ZIndex = 2;
        _canvas!.Children.Add(card);
        _cards[entity.Id] = card;

        WireCard(card, entity);

        // Re-place ports (and any touching relations) whenever the card's measured size changes.
        card.GetObservable(Visual.BoundsProperty).Subscribe(new AnonymousObserver<Rect>(_ =>
        {
            UpdatePortPositions(entity.Id);
            UpdateRelationsForEntity(entity.Id);
        }));
    }

    void WireCard(Border card, CodeEntity entity)
    {
        var dragging = false;
        var offset   = default(Point);

        card.PointerPressed += (_, e) =>
        {
            var props = e.GetCurrentPoint(card).Properties;
            if (props.IsRightButtonPressed) { ShowCardContextMenu(entity, card); e.Handled = true; return; }
            if (_connectMode) { e.Handled = true; return; }   // port dots handle connecting
            if (e.ClickCount >= 2) { _ = ShowEntityEditor(entity); e.Handled = true; return; }

            var mods = e.KeyModifiers;
            if (mods.HasFlag(KeyModifiers.Shift))
            {
                SelectRangeTo(entity.Id); RefreshSelectionVisuals(); e.Handled = true; return;
            }
            if (mods.HasFlag(KeyModifiers.Control))
            {
                if (!_selectedIds.Add(entity.Id)) _selectedIds.Remove(entity.Id);
                _selectionAnchor = entity.Id; RefreshSelectionVisuals(); e.Handled = true; return;
            }

            // Pressing a card that's part of a multi-selection keeps it (drag the whole group);
            // pressing an unselected card selects it exclusively.
            if (!_selectedIds.Contains(entity.Id)) { _selectedIds.Clear(); _selectedIds.Add(entity.Id); }
            _selectionAnchor = entity.Id;
            RefreshSelectionVisuals();

            dragging = true;
            offset   = e.GetPosition(card);
            // Snapshot start positions of every selected card, so the whole group moves together.
            _dragStart = _selectedIds.Where(_boardData.Positions.ContainsKey)
                .ToDictionary(id => id, id => new Point(_boardData.Positions[id].X, _boardData.Positions[id].Y));
            e.Pointer.Capture(card);
            e.Handled = true;
        };

        card.PointerMoved += (_, e) =>
        {
            if (!dragging || _dragStart is null) return;
            var pt = e.GetPosition(_canvas);
            // Corner-snap the dragged card; shift the rest of the selection by the same delta.
            var nx = Snap(Math.Max(0, pt.X - offset.X));
            var ny = Snap(Math.Max(0, pt.Y - offset.Y));
            double dx = nx - (_dragStart.TryGetValue(entity.Id, out var s0) ? s0.X : nx);
            double dy = ny - (_dragStart.TryGetValue(entity.Id, out s0) ? s0.Y : ny);
            foreach (var (id, start) in _dragStart)
            {
                if (!_cards.TryGetValue(id, out var cc)) continue;
                double x = Math.Max(0, start.X + dx), y = Math.Max(0, start.Y + dy);
                Canvas.SetLeft(cc, x); Canvas.SetTop(cc, y);
                GrowCanvasFor(x, y, cc.Bounds.Width, cc.Bounds.Height);
                UpdatePortPositions(id);
                UpdateRelationsForEntity(id);
            }
            e.Handled = true;
        };

        card.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
            foreach (var id in _dragStart?.Keys ?? Enumerable.Empty<string>())
                if (_cards.TryGetValue(id, out var cc) && _boardData.Positions.TryGetValue(id, out var p))
                { p.X = Canvas.GetLeft(cc); p.Y = Canvas.GetTop(cc); }
            _dragStart = null;
            FitCanvas();   // fit the canvas to the content (grow/shrink, anchor top-left) after the move
            Save();
            e.Handled = true;
        };

        card.PointerEntered += (_, _) => { if (!_selectedIds.Contains(entity.Id)) card.BoxShadow = HoverGlow; };
        card.PointerExited  += (_, _) => { if (!_selectedIds.Contains(entity.Id)) card.BoxShadow = default; };
    }

    Border BuildCard(CodeEntity entity)
    {
        var (typeColor, typeSymbol) = EntityTypeStyle(entity.EntityType);

        var card = new Border
        {
            CornerRadius    = new(6),
            BorderThickness = new(1),
            MinWidth        = DefaultCardW,
            Cursor          = new Cursor(StandardCursorType.SizeAll),
        };
        Ui.Theme(card, Border.BackgroundProperty,  "ControlBgBrush");
        Ui.Theme(card, Border.BorderBrushProperty, "ControlBorderBrush");

        var stack = new StackPanel();
        card.Child = stack;

        // Header (type-coloured bar with symbol + name).
        var header = new Border
        {
            Background   = new SolidColorBrush(typeColor),
            CornerRadius = new(5, 5, 0, 0),
            Padding      = new(8, 5),
        };
        header.Child = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4,
            Children =
            {
                new TextBlock { Text = typeSymbol, FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = entity.Name, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        stack.Children.Add(header);

        // Type badge.
        var badge = new Border { Background = new SolidColorBrush(Color.FromArgb(30, typeColor.R, typeColor.G, typeColor.B)), Padding = new(8, 2) };
        badge.Child = new TextBlock { Text = entity.EntityType.ToString(), FontSize = 10, Foreground = new SolidColorBrush(typeColor), FontStyle = FontStyle.Italic };
        stack.Children.Add(badge);

        // Inheritance / implements (Class / Struct).
        if (entity.EntityType is CodeEntityType.Class or CodeEntityType.Struct)
        {
            var rel = new List<string>();
            if (!string.IsNullOrEmpty(entity.BaseClassId) && _entities.TryGetValue(entity.BaseClassId, out var baseE))
                rel.Add($"⊳ {baseE.Name}");
            var ifaces = entity.ImplementsIds.Where(_entities.ContainsKey).Select(id => _entities[id].Name).ToList();
            if (ifaces.Count > 0) rel.Add($"◁ {string.Join(", ", ifaces)}");
            if (rel.Count > 0) stack.Children.Add(SectionText(string.Join("   ", rel), italic: true, opacity: 0.85));
        }

        // Object: instance-of.
        if (entity.EntityType == CodeEntityType.Object && !string.IsNullOrEmpty(entity.InstanceOfId)
            && _entities.TryGetValue(entity.InstanceOfId, out var cls))
            stack.Children.Add(SectionText($": {cls.Name}", italic: true, opacity: 0.85));

        // Description.
        if (!string.IsNullOrWhiteSpace(entity.Description))
        {
            var d = new TextBlock { Text = entity.Description, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 260, Opacity = 0.75, Margin = new(8, 4) };
            Ui.Theme(d, TextBlock.ForegroundProperty, "SidebarTextBrush");
            stack.Children.Add(d);
        }

        // Fields.
        if (entity.Fields.Count > 0)
        {
            stack.Children.Add(Divider());
            var fs = new StackPanel { Margin = new(8, 4) };
            foreach (var f in entity.Fields)
                fs.Children.Add(MemberLine($"{VisSymbol(f.Visibility)} {(f.IsStatic ? "static " : "")}{f.Name}: {f.DataType}"));
            stack.Children.Add(fs);
        }

        // Methods.
        if (entity.Methods.Count > 0)
        {
            stack.Children.Add(Divider());
            var ms = new StackPanel { Margin = new(8, 4) };
            foreach (var m in entity.Methods)
            {
                var ps = string.Join(", ", m.Parameters.Select(p => $"{ConvSymbol(p.Convention)}{p.DataType} {p.Name}"));
                var line = m.Kind switch
                {
                    MethodKind.Constructor => $"{VisSymbol(m.Visibility)} {entity.Name}({ps})",
                    MethodKind.Destructor  => $"~{entity.Name}()",
                    _                      => $"{VisSymbol(m.Visibility)} {(m.IsStatic ? "static " : "")}{m.Name}({ps}): {m.ReturnType}",
                };
                ms.Children.Add(MemberLine(line, bold: true));
            }
            stack.Children.Add(ms);
        }

        // Enum values.
        if (entity.EntityType == CodeEntityType.Enum && entity.EnumValues.Count > 0)
        {
            stack.Children.Add(Divider());
            var es = new StackPanel { Margin = new(8, 4) };
            foreach (var v in entity.EnumValues) es.Children.Add(MemberLine($"• {v}"));
            stack.Children.Add(es);
        }

        // Ports list.
        var inputs  = entity.Ports.Where(p => p.Direction == PortDirection.Input).ToList();
        var outputs = entity.Ports.Where(p => p.Direction == PortDirection.Output).ToList();
        if (inputs.Count > 0 || outputs.Count > 0)
        {
            var pb = new Border { Padding = new(8, 4, 8, 6) };
            var ps = new StackPanel();
            foreach (var port in inputs)  ps.Children.Add(PortLabel(port, isInput: true));
            foreach (var port in outputs) ps.Children.Add(PortLabel(port, isInput: false));
            pb.Child = ps;
            stack.Children.Add(pb);
        }

        return card;
    }

    static TextBlock PortLabel(CodePort port, bool isInput)
    {
        var conv = port.Convention switch { PassingConvention.Reference => "&", PassingConvention.Pointer => "*", _ => "" };
        var text = isInput ? $"→ {port.Name}: {conv}{port.DataType}" : $"{port.Name}: {conv}{port.DataType} →";
        return new TextBlock
        {
            Text = text, FontSize = 10, Opacity = 0.85, Margin = new(0, 1),
            Foreground = new SolidColorBrush(isInput ? Color.FromRgb(0x42, 0xA5, 0xF5) : Color.FromRgb(0x66, 0xBB, 0x6A)),
        };
    }

    // ── UML section helpers ──────────────────────────────────────────────────

    static string VisSymbol(CodeVisibility v) => v switch
    {
        CodeVisibility.Public => "+", CodeVisibility.Private => "−", CodeVisibility.Protected => "#", CodeVisibility.Internal => "~", _ => " ",
    };

    static string ConvSymbol(PassingConvention c) => c switch
    {
        PassingConvention.Reference => "&", PassingConvention.Pointer => "*", _ => "",
    };

    TextBlock SectionText(string text, bool italic = false, double opacity = 1.0)
    {
        var tb = new TextBlock { Text = text, FontSize = 10, FontStyle = italic ? FontStyle.Italic : FontStyle.Normal, Opacity = opacity, TextWrapping = TextWrapping.Wrap, MaxWidth = 260, Margin = new(8, 2) };
        Ui.Theme(tb, TextBlock.ForegroundProperty, "SidebarTextBrush");
        return tb;
    }

    TextBlock MemberLine(string text, bool bold = false)
    {
        var tb = new TextBlock { Text = text, FontSize = 11, FontFamily = Mono, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap, MaxWidth = 260, Margin = new(0, 1) };
        Ui.Theme(tb, TextBlock.ForegroundProperty, "SidebarTextBrush");
        return tb;
    }

    Border Divider()
    {
        var b = new Border { Height = 1, Margin = new(0, 1) };
        Ui.Theme(b, Border.BackgroundProperty, "ControlBorderBrush");
        return b;
    }

    // ── Port dots ────────────────────────────────────────────────────────────

    void GrowCanvasFor(double x, double y, double w, double h)
    {
        if (_canvas is null) return;
        const double margin = 400;
        bool grew = false;
        if (x + w + margin > _canvas.Width)  { _canvas.Width  = x + w + margin; grew = true; }
        if (y + h + margin > _canvas.Height) { _canvas.Height = y + h + margin; grew = true; }
        if (grew && _gridRect is not null) { _gridRect.Width = _canvas.Width; _gridRect.Height = _canvas.Height; }
    }

    // Fits the canvas size to its content with an even margin, growing AND shrinking on every side, and
    // re-anchors the content's top-left near the margin. Called on drag-release / removal.
    void FitCanvas()
    {
        if (_canvas is null || _cards.Count == 0) return;
        const double pad = 80;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (id, pos) in _boardData.Positions)
        {
            if (!_cards.TryGetValue(id, out var card)) continue;
            double w = card.Bounds.Width  > 0 ? card.Bounds.Width  : DefaultCardW;
            double h = card.Bounds.Height > 0 ? card.Bounds.Height : 80;
            minX = Math.Min(minX, pos.X); minY = Math.Min(minY, pos.Y);
            maxX = Math.Max(maxX, pos.X + w); maxY = Math.Max(maxY, pos.Y + h);
        }
        foreach (var rel in _boardData.Relations)
            foreach (var wp in rel.Waypoints)
            {
                minX = Math.Min(minX, wp.X); minY = Math.Min(minY, wp.Y);
                maxX = Math.Max(maxX, wp.X); maxY = Math.Max(maxY, wp.Y);
            }
        if (minX == double.MaxValue) return;

        // Shift by a whole number of grid cells, so everything stays aligned to the grid (which tiles
        // from 0,0) — a non-grid shift would knock all placements off the grid.
        double g = _boardData.GridSize >= 1 ? _boardData.GridSize : 10;
        double dx = Math.Round((pad - minX) / g) * g, dy = Math.Round((pad - minY) / g) * g;
        ShiftWorld(dx, dy);

        // Never shrink below the visible viewport (so a near-empty board doesn't collapse to a tiny box).
        double minW = Math.Max(800, _scroll?.Viewport.Width  / (_zoom <= 0 ? 1 : _zoom) ?? 800);
        double minH = Math.Max(600, _scroll?.Viewport.Height / (_zoom <= 0 ? 1 : _zoom) ?? 600);
        _canvas.Width  = Math.Max(minW, maxX + dx + pad);
        _canvas.Height = Math.Max(minH, maxY + dy + pad);
        if (_gridRect is not null) { _gridRect.Width = _canvas.Width; _gridRect.Height = _canvas.Height; }
    }

    // Translates every card + waypoint by (dx,dy) and compensates the scroll offset, so the view stays put.
    void ShiftWorld(double dx, double dy)
    {
        if (_canvas is null || (dx == 0 && dy == 0)) return;
        foreach (var (id, pos) in _boardData.Positions)
        {
            pos.X += dx; pos.Y += dy;
            if (_cards.TryGetValue(id, out var card)) { Canvas.SetLeft(card, pos.X); Canvas.SetTop(card, pos.Y); }
            UpdatePortPositions(id);
        }
        foreach (var rel in _boardData.Relations)
            foreach (var wp in rel.Waypoints) { wp.X += dx; wp.Y += dy; }
        RenderAllRelations();
        if (_scroll is not null)
            _scroll.Offset = new Vector(Math.Max(0, _scroll.Offset.X + dx * _zoom), Math.Max(0, _scroll.Offset.Y + dy * _zoom));
    }

    void UpdatePortPositions(string entityId)
    {
        if (!_cards.TryGetValue(entityId, out var card)) return;
        if (!_entities.TryGetValue(entityId, out var entity)) return;
        if (!_boardData.Positions.TryGetValue(entityId, out var pos)) return;

        double x = Canvas.GetLeft(card), y = Canvas.GetTop(card);
        double w = card.Bounds.Width  > 0 ? card.Bounds.Width  : DefaultCardW;
        double h = card.Bounds.Height > 0 ? card.Bounds.Height : 80;

        var inputs  = entity.Ports.Where(p => p.Direction == PortDirection.Input).ToList();
        var outputs = entity.Ports.Where(p => p.Direction == PortDirection.Output).ToList();

        PlacePortDots(entity, inputs,  x, y, w, h, pos.PortOrientation, isInput: true);
        PlacePortDots(entity, outputs, x, y, w, h, pos.PortOrientation, isInput: false);
    }

    void PlacePortDots(CodeEntity entity, List<CodePort> ports, double cardX, double cardY, double cardW, double cardH, PortOrientation orientation, bool isInput)
    {
        bool snap = _boardData.SnapToGrid && _boardData.GridSize >= 1;
        double g = _boardData.GridSize;
        int n = ports.Count;
        double lastVar = double.NegativeInfinity;   // keeps snapped ports on distinct grid lines
        for (int i = 0; i < n; i++)
        {
            var dot = GetOrCreatePortDot(entity.Id, ports[i]);
            // Port CENTRE: fixed on the edge across one axis, evenly distributed along the other.
            double centerX, centerY;
            if (orientation == PortOrientation.Horizontal)
            {
                centerX = isInput ? cardX : cardX + cardW;
                centerY = cardY + cardH * (i + 1.0) / (n + 1.0);
            }
            else
            {
                centerX = cardX + cardW * (i + 1.0) / (n + 1.0);
                centerY = isInput ? cardY : cardY + cardH;
            }

            // Snap the centre onto a grid intersection; nudge the distributing axis to the next line if a
            // collision would stack two ports, so they stay on distinct grid lines.
            if (snap)
            {
                centerX = Snap(centerX);
                centerY = Snap(centerY);
                if (orientation == PortOrientation.Horizontal)
                {
                    if (centerY <= lastVar) centerY = lastVar + g;
                    lastVar = centerY;
                }
                else
                {
                    if (centerX <= lastVar) centerX = lastVar + g;
                    lastVar = centerX;
                }
            }

            Canvas.SetLeft(dot, centerX - PortRadius);
            Canvas.SetTop(dot,  centerY - PortRadius);
        }
    }

    Ellipse GetOrCreatePortDot(string entityId, CodePort port)
    {
        var key = $"{entityId}:{port.Id}";
        if (_portDots.TryGetValue(key, out var existing)) return existing;

        var isInput = port.Direction == PortDirection.Input;
        var dot = new Ellipse
        {
            Width = PortRadius * 2, Height = PortRadius * 2,
            Fill = new SolidColorBrush(isInput ? Color.FromRgb(0x42, 0xA5, 0xF5) : Color.FromRgb(0x66, 0xBB, 0x6A)),
            Stroke = Brushes.White, StrokeThickness = 1.5,
            Cursor = new Cursor(StandardCursorType.Cross),
        };
        ToolTip.SetTip(dot, BuildPortTooltip(port));
        dot.ZIndex = 5;
        _canvas!.Children.Add(dot);
        _portDots[key] = dot;

        dot.PointerPressed += (_, e) =>
        {
            if (!_connectMode) return;
            HandlePortClick(entityId, port.Id, port.Direction);
            e.Handled = true;
        };
        return dot;
    }

    static string BuildPortTooltip(CodePort port)
    {
        var conv = port.Convention switch { PassingConvention.Reference => "ref ", PassingConvention.Pointer => "ptr ", _ => "" };
        return $"{port.Direction}: {port.Name} ({conv}{port.DataType})";
    }

    // ── Connect mode ───────────────────────────────────────────────────────

    void HandlePortClick(string entityId, string portId, PortDirection direction)
    {
        // A connection starts at an output port; arming it lights up the valid targets across the board.
        if (_connectFromEntityId is null)
        {
            if (direction != PortDirection.Output) return;
            _connectFromEntityId = entityId;
            _connectFromPortId   = portId;
            EnsureRubberBand();
            ApplyDragHighlights();
            return;
        }

        if (entityId == _connectFromEntityId) { CancelConnect(); return; }   // back on the source → cancel
        if (direction != PortDirection.Input) return;                       // must land on an input

        var src = FindPort(_connectFromEntityId!, _connectFromPortId!);
        var dst = FindPort(entityId, portId);
        // Only an exact match wires up — the red/amber ✕ already shows why the others can't.
        if (src is null || dst is null || PortMatchOf(src, dst) != PortMatch.Exact) return;

        var rel = new CodeRelation { FromId = _connectFromEntityId!, FromPortId = _connectFromPortId!, ToId = entityId, ToPortId = portId };
        _boardData.Relations.Add(rel);
        Save();
        RenderRelation(rel);
        CancelConnect();
    }

    // Drops the in-progress connection and clears its rubber band + target highlights.
    void CancelConnect()
    {
        _connectFromEntityId = null;
        _connectFromPortId   = null;
        RemoveRubberBand();
        ClearDragHighlights();
    }

    CodePort? FindPort(string entityId, string portId) =>
        _entities.TryGetValue(entityId, out var e) ? e.Ports.FirstOrDefault(p => p.Id == portId) : null;

    static string NormType(string t) => (t ?? "").Trim().TrimEnd('*', '&', ' ');

    // Same data type AND convention = Exact; same type, different convention = ConvMismatch; else TypeMismatch.
    static PortMatch PortMatchOf(CodePort src, CodePort dst)
    {
        if (!NormType(src.DataType).Equals(NormType(dst.DataType), StringComparison.OrdinalIgnoreCase)) return PortMatch.TypeMismatch;
        if (src.Convention != dst.Convention) return PortMatch.ConvMismatch;
        return PortMatch.Exact;
    }

    // While wiring, recolour every port: green = connectable, amber ✕ = type ok but wrong passing
    // convention, red ✕ = wrong type; non-targets (outputs, same card) are dimmed.
    void ApplyDragHighlights()
    {
        ClearDragHighlights();
        if (_connectFromEntityId is null || _connectFromPortId is null) return;
        var src = FindPort(_connectFromEntityId, _connectFromPortId);
        if (src is null) return;

        foreach (var (key, dot) in _portDots)
        {
            var sep    = key.IndexOf(':');
            var entId  = key[..sep];
            var portId = key[(sep + 1)..];

            if (entId == _connectFromEntityId)            // the source card: keep the chosen dot lit, dim the rest
            {
                dot.Opacity = portId == _connectFromPortId ? 1 : 0.3;
                continue;
            }
            var port = FindPort(entId, portId);
            if (port is null || port.Direction != PortDirection.Input) { dot.Opacity = 0.3; continue; }

            switch (PortMatchOf(src, port))
            {
                case PortMatch.Exact:
                    dot.Stroke = Brushes.LimeGreen; dot.StrokeThickness = 3; dot.Opacity = 1;
                    break;
                case PortMatch.ConvMismatch:
                    dot.Opacity = 0.55; AddCross(dot, Color.FromRgb(0xF5, 0xC2, 0x00));  // amber
                    break;
                case PortMatch.TypeMismatch:
                    dot.Opacity = 0.45; AddCross(dot, Color.FromRgb(0xE5, 0x39, 0x35));  // red
                    break;
            }
        }
    }

    // Places a small ✕ over a port dot (the marker is purely decorative — clicks pass through).
    void AddCross(Ellipse dot, Color color)
    {
        var x = new TextBlock { Text = "✕", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(color), IsHitTestVisible = false };
        Canvas.SetLeft(x, Canvas.GetLeft(dot) + PortRadius - 5);
        Canvas.SetTop(x,  Canvas.GetTop(dot)  + PortRadius - 8);
        x.ZIndex = 6;
        _canvas!.Children.Add(x);
        _dragMarkers.Add(x);
    }

    // Removes the ✕ overlays and restores every dot to its resting look.
    void ClearDragHighlights()
    {
        foreach (var m in _dragMarkers) _canvas?.Children.Remove(m);
        _dragMarkers.Clear();
        foreach (var dot in _portDots.Values) { dot.Stroke = Brushes.White; dot.StrokeThickness = 1.5; dot.Opacity = 1; }
    }

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

    void RemoveRubberBand()
    {
        if (_rubberBand is null) return;
        _canvas?.Children.Remove(_rubberBand);
        _rubberBand = null;
    }

    // ── Relation rendering ───────────────────────────────────────────────────

    void RenderAllRelations()
    {
        foreach (var rel in _boardData.Relations) RenderRelation(rel);
    }

    void RenderRelation(CodeRelation rel)
    {
        if (_relViews.TryGetValue(rel.Id, out var old))
            foreach (var v in old) _canvas!.Children.Remove(v);
        _relViews.Remove(rel.Id);

        var p1 = GetPortCenter(rel.FromId, rel.FromPortId);
        var p2 = GetPortCenter(rel.ToId,   rel.ToPortId);
        if (p1 is null || p2 is null) return;

        var color   = ParseColor(rel.LineColor);
        var brush   = new SolidColorBrush(color);
        var visuals = new List<Control>();

        var points = new List<Point> { p1.Value };
        foreach (var wp in rel.Waypoints) points.Add(new Point(wp.X, wp.Y));
        points.Add(p2.Value);

        for (int i = 0; i < points.Count - 1; i++)
        {
            var line = new Line { StartPoint = points[i], EndPoint = points[i + 1], Stroke = brush, StrokeThickness = rel.Thickness, IsHitTestVisible = false };
            ApplyLineStyle(line, rel.LineStyle);
            line.ZIndex = 1;
            _canvas!.Children.Add(line); visuals.Add(line);
        }

        // Fat transparent hit-zone for right-click delete.
        var hit = new Line { StartPoint = p1.Value, EndPoint = p2.Value, Stroke = Brushes.Transparent, StrokeThickness = 12, Cursor = new Cursor(StandardCursorType.Hand) };
        hit.ZIndex = 3;
        var capRel = rel;
        hit.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(hit).Properties.IsRightButtonPressed) { ShowRelationContextMenu(capRel, hit); e.Handled = true; }
        };
        _canvas!.Children.Add(hit); visuals.Add(hit);

        if (rel.HasArrow)
        {
            var arrow = BuildArrow(points[^2], points[^1], brush);
            arrow.ZIndex = 2;
            _canvas.Children.Add(arrow); visuals.Add(arrow);
        }

        _relViews[rel.Id] = visuals;
    }

    Point? GetPortCenter(string entityId, string portId)
    {
        var key = $"{entityId}:{portId}";
        if (!_portDots.TryGetValue(key, out var dot)) return null;
        return new Point(Canvas.GetLeft(dot) + PortRadius, Canvas.GetTop(dot) + PortRadius);
    }

    void UpdateRelationsForEntity(string entityId)
    {
        foreach (var rel in _boardData.Relations)
            if (rel.FromId == entityId || rel.ToId == entityId) RenderRelation(rel);
    }

    static Polygon BuildArrow(Point from, Point to, IBrush brush)
    {
        var poly = new Polygon { Fill = brush, IsHitTestVisible = false };
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.001) return poly;
        double ux = dx / len, uy = dy / len, px = -uy, py = ux;
        const double aw = 6, al = 10;
        poly.Points.Add(to);
        poly.Points.Add(new(to.X - ux * al + px * aw, to.Y - uy * al + py * aw));
        poly.Points.Add(new(to.X - ux * al - px * aw, to.Y - uy * al - py * aw));
        return poly;
    }

    static void ApplyLineStyle(Line line, BoardLineStyle style)
    {
        line.StrokeDashArray = style switch
        {
            BoardLineStyle.Dotted  => new AvaloniaList<double> { 2, 3 },
            BoardLineStyle.Dashed  => new AvaloniaList<double> { 6, 3 },
            BoardLineStyle.DotDash => new AvaloniaList<double> { 6, 3, 2, 3 },
            _                      => null,
        };
    }

    // ── Canvas mouse (rubber-band select / deselect / add menu) ──────────────

    bool   _rubberSelecting;
    Point  _rubberStart;
    Rectangle? _rubberRect;

    void Canvas_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _menu?.Close();
        var props = e.GetCurrentPoint(_canvas).Properties;

        if (props.IsRightButtonPressed)
        {
            // Right-drag pans (also while connecting). A plain right-click cancels the arrow (connect
            // mode) or opens the add-menu (otherwise).
            _panning = true; _rightMaybeMenu = !_connectMode; _rightCancelConnect = _connectMode;
            _panStart = e.GetPosition(_scroll); _panOrigin = _scroll!.Offset;
            e.Pointer.Capture(_canvas);
            e.Handled = true;
            return;
        }

        if (_connectMode) return;

        _selectedIds.Clear();
        _selectionAnchor = null;
        RefreshSelectionVisuals();
        _rubberStart = e.GetPosition(_canvas);
        _rubberSelecting = true;
        e.Pointer.Capture(_canvas);
        e.Handled = true;
    }

    void Canvas_PointerMoved(object? sender, PointerEventArgs e)
    {
        _mousePos = e.GetPosition(_canvas);   // remembered for paste-at-cursor
        if (_connectMode && _connectFromEntityId is not null && _rubberBand is not null)
        {
            if (GetPortCenter(_connectFromEntityId, _connectFromPortId!) is { } c) _rubberBand.StartPoint = c;
            _rubberBand.EndPoint = e.GetPosition(_canvas);
            return;
        }

        if (_panning)
        {
            var d = e.GetPosition(_scroll) - _panStart;
            if ((_rightMaybeMenu || _rightCancelConnect) && (Math.Abs(d.X) > 4 || Math.Abs(d.Y) > 4))
            { _rightMaybeMenu = false; _rightCancelConnect = false; if (_canvas is not null) _canvas.Cursor = new Cursor(StandardCursorType.SizeAll); }
            _scroll!.Offset = new Vector(_panOrigin.X - d.X, _panOrigin.Y - d.Y);
            return;
        }

        if (_rubberSelecting)
        {
            var cur = e.GetPosition(_canvas);
            if (_rubberRect is null)
            {
                _rubberRect = new Rectangle
                {
                    Stroke = Brushes.DodgerBlue, StrokeDashArray = new AvaloniaList<double> { 4, 2 }, StrokeThickness = 1,
                    Fill = new SolidColorBrush(Color.FromArgb(30, 30, 144, 255)), IsHitTestVisible = false,
                };
                _rubberRect.ZIndex = 50;
                _canvas!.Children.Add(_rubberRect);
            }
            double x = Math.Min(_rubberStart.X, cur.X), y = Math.Min(_rubberStart.Y, cur.Y);
            Canvas.SetLeft(_rubberRect, x); Canvas.SetTop(_rubberRect, y);
            _rubberRect.Width  = Math.Abs(cur.X - _rubberStart.X);
            _rubberRect.Height = Math.Abs(cur.Y - _rubberStart.Y);
        }
    }

    void Canvas_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_panning)
        {
            _panning = false;
            e.Pointer.Capture(null);
            if (_canvas is not null) _canvas.Cursor = new Cursor(StandardCursorType.Arrow);
            if (_rightMaybeMenu) { _rightMaybeMenu = false; ShowCanvasAddMenu(e.GetPosition(_canvas)); }       // plain right-click → add menu
            else if (_rightCancelConnect) { _rightCancelConnect = false; CancelConnect(); }                   // plain right-click (connect) → cancel
            return;
        }
        if (!_rubberSelecting) return;
        _rubberSelecting = false;
        e.Pointer.Capture(null);
        if (_rubberRect is null) return;

        double rx = Canvas.GetLeft(_rubberRect), ry = Canvas.GetTop(_rubberRect);
        double rw = _rubberRect.Width, rh = _rubberRect.Height;
        foreach (var (id, card) in _cards)
        {
            double cx = Canvas.GetLeft(card), cy = Canvas.GetTop(card);
            if (cx >= rx && cy >= ry && cx + card.Bounds.Width <= rx + rw && cy + card.Bounds.Height <= ry + rh)
                _selectedIds.Add(id);
        }
        _canvas!.Children.Remove(_rubberRect);
        _rubberRect = null;
        RefreshSelectionVisuals();
    }

    void ShowCanvasAddMenu(Point dropPoint)
    {
        var cm = new ContextMenu();
        foreach (var t in CodeEntityService.EntityTypes)
        {
            var capType = t;
            var mi = new MenuItem { Header = string.Format(Loc.S("Code_AddType"), capType) };
            mi.Click += async (_, _) => await AddEntityOfType(capType, dropPoint);
            cm.Items.Add(mi);
        }
        OpenMenu(cm, _canvas!);
    }

    // ── Context menus ────────────────────────────────────────────────────────

    void ShowCardContextMenu(CodeEntity entity, Control anchor)
    {
        var cm = new ContextMenu();

        var editMi = new MenuItem { Header = Loc.S("Code_Edit") };
        editMi.Click += async (_, _) => await ShowEntityEditor(entity);
        cm.Items.Add(editMi);

        if (entity.EntityType == CodeEntityType.Function)
        {
            var flowMi = new MenuItem { Header = Loc.S("Code_SketchFlow") };
            flowMi.Click += (_, _) => _ = DiagramLauncher.ChooseAndOpen(this, _projFolder, entity.Id, entity.Name, _themePath);
            cm.Items.Add(flowMi);
        }

        if (_onExport is not null)
        {
            var exportMi = new MenuItem { Header = Loc.S("Code_ExportThis") };
            exportMi.Click += (_, _) => _onExport!.Invoke(new[] { entity });
            cm.Items.Add(exportMi);
        }

        cm.Items.Add(new Separator());

        if (_boardData.Positions.TryGetValue(entity.Id, out var pos))
        {
            var orientMi = new MenuItem
            {
                Header = pos.PortOrientation == PortOrientation.Horizontal ? Loc.S("Code_SwitchVertical") : Loc.S("Code_SwitchHorizontal"),
            };
            orientMi.Click += (_, _) =>
            {
                pos.PortOrientation = pos.PortOrientation == PortOrientation.Horizontal ? PortOrientation.Vertical : PortOrientation.Horizontal;
                Save();
                UpdatePortPositions(entity.Id);
                UpdateRelationsForEntity(entity.Id);
            };
            cm.Items.Add(orientMi);
            cm.Items.Add(new Separator());
        }

        var removeMi = new MenuItem { Header = Loc.S("Code_RemoveFromBoard") };
        removeMi.Click += (_, _) => RemoveFromBoard(new[] { entity.Id });
        cm.Items.Add(removeMi);

        var deleteMi = new MenuItem { Header = Loc.S("Code_DeletePerm") };
        deleteMi.Click += async (_, _) =>
        {
            var res = await MessageDialog.Show(this, string.Format(Loc.S("Code_DeletePermConfirm"), entity.Name), Loc.S("Code_DeleteEntityTitle"), DialogButtons.YesNo);
            if (res != DialogResult.Yes) return;
            CodeEntityService.Delete(_projFolder, entity.EntityType.ToString(), entity.Id);
            _entities.Remove(entity.Id);
            RemoveFromBoard(new[] { entity.Id });
        };
        cm.Items.Add(deleteMi);

        OpenMenu(cm, anchor);
    }

    void ShowRelationContextMenu(CodeRelation rel, Control anchor)
    {
        var cm = new ContextMenu();
        var delMi = new MenuItem { Header = Loc.S("Code_DeleteConnection") };
        delMi.Click += (_, _) =>
        {
            _boardData.Relations.Remove(rel);
            Save();
            if (_relViews.TryGetValue(rel.Id, out var vs)) foreach (var v in vs) _canvas!.Children.Remove(v);
            _relViews.Remove(rel.Id);
        };
        cm.Items.Add(delMi);
        OpenMenu(cm, anchor);
    }

    // ── Add entity ─────────────────────────────────────────────────────────

    void ShowAddEntityMenu(Button anchor)
    {
        var cm = new ContextMenu();
        foreach (var t in CodeEntityService.EntityTypes)
        {
            var capType = t;
            var mi = new MenuItem { Header = capType };
            mi.Click += async (_, _) => await AddEntityOfType(capType, new Point(60, 60));
            cm.Items.Add(mi);
        }
        cm.Items.Add(new Separator());
        var existMi = new MenuItem { Header = Loc.S("Code_AddExisting") };
        existMi.Click += async (_, _) => await ShowAddExistingEntityDialog();
        cm.Items.Add(existMi);
        OpenMenu(cm, anchor);
    }

    async Task AddEntityOfType(string entityTypeName, Point dropPoint)
    {
        if (!Enum.TryParse<CodeEntityType>(entityTypeName, out var et)) return;
        // A body-authoring board only wires functions — keep classes/objects off it (avoids loops).
        if (_bodyTargetKey is not null && et != CodeEntityType.Function)
        { await MessageDialog.Show(this, Loc.S("Code_BodyFuncOnly"), Loc.S("Code_AddEntityTitle")); return; }
        var name = await PromptDialog.Show(this, Loc.S("Common_NameColon"), "", string.Format(Loc.S("Code_NewTypeTitle"), entityTypeName));
        if (string.IsNullOrWhiteSpace(name)) return;

        var entity = new CodeEntity { Name = name.Trim(), EntityType = et };
        _entities[entity.Id] = entity;
        CodeEntityService.Save(_projFolder, entityTypeName, entity);

        var pos = new CodeCardPosition { X = dropPoint.X, Y = dropPoint.Y };
        _boardData.Positions[entity.Id] = pos;
        Save();
        RenderCard(entity, pos);
    }

    async Task ShowAddExistingEntityDialog()
    {
        var onBoard = _boardData.Positions.Keys.ToHashSet();

        // Build the list from a FRESH disk scan, so entities deleted elsewhere don't linger (and the
        // render cache stays current). Anything already on the board is excluded.
        var available = new List<CodeEntity>();
        foreach (var t in CodeEntityService.EntityTypes)
            foreach (var e in CodeEntityService.LoadAll(_projFolder, t))
            {
                _entities[e.Id] = e;
                if (onBoard.Contains(e.Id)) continue;
                if (_bodyTargetKey is not null && e.EntityType != CodeEntityType.Function) continue;   // body board = functions only
                available.Add(e);
            }
        available = available.OrderBy(e => e.EntityType.ToString()).ThenBy(e => e.Name).ToList();

        if (available.Count == 0)
        {
            await MessageDialog.Show(this, Loc.S("Code_AllOnBoard"), Loc.S("Code_AddEntityTitle"));
            return;
        }

        var picked = await PickExistingEntity(available);
        if (picked is null) return;

        var at = SpawnPoint();
        var pos = new CodeCardPosition { X = at.X, Y = at.Y };
        _boardData.Positions[picked.Id] = pos;
        Save();
        RenderCard(picked, pos);
    }

    // A spawn position inside the currently visible viewport (accounts for scroll + zoom), so newly
    // added cards always appear in view rather than at a fixed off-screen corner.
    Point SpawnPoint()
    {
        if (_scroll is null) return new Point(80, 80);
        double z = _zoom <= 0 ? 1 : _zoom;
        double x = _scroll.Offset.X / z + 40 + _boardData.Positions.Count % 6 * 30;
        double y = _scroll.Offset.Y / z + 40 + _boardData.Positions.Count % 6 * 30;
        return new Point(Snap(x), Snap(y));
    }

    // A small modal list picker for entities not yet on the board.
    Task<CodeEntity?> PickExistingEntity(List<CodeEntity> available)
    {
        var dlg = new Window
        {
            Title = Loc.S("Code_AddExistingTitle"), Width = 360, Height = 440,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        Ui.ThemeWindow(dlg);

        var list = new ListBox { SelectionMode = SelectionMode.Single };
        Ui.Theme(list, TemplatedControl.BackgroundProperty,  "ControlBgBrush");
        Ui.Theme(list, TemplatedControl.ForegroundProperty,  "SidebarTextBrush");
        Ui.Theme(list, TemplatedControl.BorderBrushProperty, "ControlBorderBrush");

        // A type filter keeps the list usable when a project has dozens of each kind.
        var typeCombo = Ui.Combo();
        typeCombo.Items.Add(Loc.S("Code_AllTypes"));
        foreach (var t in CodeEntityService.EntityTypes) typeCombo.Items.Add(t);
        typeCombo.SelectedIndex = 0;

        void Rebuild()
        {
            var sel = typeCombo.SelectedItem as string;
            var all = sel is null || sel == Loc.S("Code_AllTypes");
            list.Items.Clear();
            foreach (var e in available)
                if (all || e.EntityType.ToString() == sel)
                    list.Items.Add(new ListBoxItem { Content = $"{e.EntityType}  {e.Name}", Tag = e });
        }
        typeCombo.SelectionChanged += (_, _) => Rebuild();
        Rebuild();

        CodeEntity? result = null;
        void Commit() { if (list.SelectedItem is ListBoxItem { Tag: CodeEntity e }) { result = e; dlg.Close(); } }

        var add = Ui.Btn(Loc.S("Common_Add")); add.IsDefault = true; add.Click += (_, _) => Commit();
        var cancel = Ui.Btn(Loc.S("Common_Cancel")); cancel.IsCancel = true; cancel.Click += (_, _) => dlg.Close();
        list.DoubleTapped += (_, _) => Commit();

        var filterRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(0, 0, 0, 8),
            Children = { new TextBlock { Text = Loc.S("CodeEdit_Type"), VerticalAlignment = VerticalAlignment.Center }, typeCombo },
        };

        var grid = new Grid { Margin = new(12), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(filterRow, 0); grid.Children.Add(filterRow);
        Grid.SetRow(list, 1); grid.Children.Add(list);
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new(0, 8, 0, 0), Children = { cancel, add } };
        Grid.SetRow(btnRow, 2); grid.Children.Add(btnRow);
        dlg.Content = grid;

        return dlg.ShowDialog<CodeEntity?>(this).ContinueWith(_ => result, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ── Drag-and-drop from the cockpit entity lists ──────────────────────────

    /// <summary>Clipboard/drag format: "{projectFolder}{id,id,…}". The cockpit packs the selected
    /// entity ids; the board only accepts a drop from the SAME project.</summary>
    public static readonly DataFormat<string> EntityDragFormat = DataFormat.CreateInProcessFormat<string>("structofox-entities");
    public const char DragSep = '\n';   // separates the project folder from the id list in the payload

    void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(EntityDragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetValue(EntityDragFormat) is not string payload) return;
        var sep = payload.IndexOf(DragSep);
        if (sep < 0) return;
        var proj = payload[..sep];
        if (!string.Equals(proj, _projFolder, StringComparison.OrdinalIgnoreCase)) return;   // different project
        var ids = payload[(sep + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries);
        AddEntitiesById(ids, e.GetPosition(_canvas!));
        e.Handled = true;
    }

    // Places dropped entities as cards from the drop point, cascading so they don't stack exactly.
    void AddEntitiesById(IEnumerable<string> ids, Point at)
    {
        foreach (var t in CodeEntityService.EntityTypes)
            foreach (var ent in CodeEntityService.LoadAll(_projFolder, t))
                _entities[ent.Id] = ent;

        double off = 0;
        foreach (var id in ids)
        {
            if (_boardData.Positions.ContainsKey(id)) continue;       // already on the board
            if (!_entities.TryGetValue(id, out var ent)) continue;
            if (_bodyTargetKey is not null && ent.EntityType != CodeEntityType.Function) continue;   // body board = functions only
            var pos = new CodeCardPosition { X = Math.Max(0, at.X + off), Y = Math.Max(0, at.Y + off) };
            _boardData.Positions[id] = pos;
            RenderCard(ent, pos);
            off += 26;
        }
        Save();
    }

    // ── Generate the target function/method body from the wiring ─────────────

    // Translates the board's dataflow into the target's structogram (topologically ordered calls),
    // then confirms. The structogram drives the normal code export afterwards.
    async Task GenerateBody()
    {
        if (_bodyTargetKey is null) return;
        // Make sure every entity is known (names/ports) before wiring them into a call sequence.
        foreach (var t in CodeEntityService.EntityTypes)
            foreach (var ent in CodeEntityService.LoadAll(_projFolder, t))
                _entities[ent.Id] = ent;

        var sd = CodeBoardCodeGen.GenerateBody(_board.Name, _boardData, _entities);
        StructogramService.Save(_projFolder, _bodyTargetKey, sd);
        await MessageDialog.Show(this, string.Format(Loc.S("Code_GenDone"), sd.Root.Count), Loc.S("Code_GenTitle"));
    }

    // ── Entity editor (shared standalone dialog) ─────────────────────────────

    async Task ShowEntityEditor(CodeEntity entity)
    {
        // Make sure dropdowns can see every entity (base class / interface / instance-of).
        foreach (var t in CodeEntityService.EntityTypes)
            foreach (var e in CodeEntityService.LoadAll(_projFolder, t))
                _entities.TryAdd(e.Id, e);

        var saved = await CodeEntityEditorDialog.Edit(this, _projFolder, entity, _entities, _themePath);
        if (!saved) return;

        _entities[entity.Id] = entity;

        // Rebuild the card (and its ports) from scratch.
        if (_cards.TryGetValue(entity.Id, out var oldCard)) { _canvas!.Children.Remove(oldCard); _cards.Remove(entity.Id); }
        foreach (var key in _portDots.Keys.Where(k => k.StartsWith(entity.Id + ":")).ToList())
        {
            _canvas!.Children.Remove(_portDots[key]);
            _portDots.Remove(key);
        }
        if (_boardData.Positions.TryGetValue(entity.Id, out var pos))
        {
            RenderCard(entity, pos);
            Dispatcher.UIThread.Post(() => { UpdatePortPositions(entity.Id); UpdateRelationsForEntity(entity.Id); }, DispatcherPriority.Loaded);
        }
    }

    // ── Selection & removal ──────────────────────────────────────────────────

    List<CodeEntity> AllBoardEntities() =>
        _boardData.Positions.Keys.Where(_entities.ContainsKey).Select(id => _entities[id]).ToList();

    List<CodeEntity> SelectedEntities() =>
        _selectedIds.Where(_entities.ContainsKey).Select(id => _entities[id]).ToList();

    // Selects the reading-order (top→bottom, left→right) range from the anchor to the given id.
    void SelectRangeTo(string id)
    {
        if (_selectionAnchor is null || !_cards.ContainsKey(_selectionAnchor))
        {
            _selectedIds.Clear(); _selectedIds.Add(id); _selectionAnchor = id; return;
        }
        var ordered = _cards.Keys.OrderBy(k => Canvas.GetTop(_cards[k])).ThenBy(k => Canvas.GetLeft(_cards[k])).ToList();
        int a = ordered.IndexOf(_selectionAnchor), b = ordered.IndexOf(id);
        if (a < 0 || b < 0) { _selectedIds.Add(id); return; }
        if (a > b) (a, b) = (b, a);
        _selectedIds.Clear();
        for (int i = a; i <= b; i++) _selectedIds.Add(ordered[i]);
    }

    void RemoveSelectedFromBoard() => RemoveFromBoard(_selectedIds.ToList());

    void RemoveFromBoard(IEnumerable<string> ids)
    {
        var any = false;
        foreach (var id in ids)
        {
            any = true;
            _boardData.Positions.Remove(id);

            if (_cards.TryGetValue(id, out var card)) { _canvas!.Children.Remove(card); _cards.Remove(id); }

            foreach (var key in _portDots.Keys.Where(k => k.StartsWith(id + ":")).ToList())
            {
                _canvas!.Children.Remove(_portDots[key]);
                _portDots.Remove(key);
            }

            foreach (var rel in _boardData.Relations.Where(r => r.FromId == id || r.ToId == id).ToList())
            {
                _boardData.Relations.Remove(rel);
                if (_relViews.TryGetValue(rel.Id, out var vs)) foreach (var v in vs) _canvas!.Children.Remove(v);
                _relViews.Remove(rel.Id);
            }
        }
        _selectedIds.Clear();
        if (any) FitCanvas();   // shrink the canvas back when edge cards were removed
        Save();
        RefreshSelectionVisuals();
        if (any) _ = InfoDialog.Show(this, "board_remove", Loc.S("Board_RemoveInfo"), Loc.S("Code_RemoveFromBoard"));
    }

    void RefreshSelectionVisuals()
    {
        bool any = _selectedIds.Count > 0;
        foreach (var (id, card) in _cards)
        {
            bool sel = _selectedIds.Contains(id);
            card.Opacity   = any && !sel ? 0.45 : 1.0;
            card.BoxShadow = sel ? SelGlow : default;
            card.BorderThickness = new(sel ? 2 : 1);
            if (sel) card.BorderBrush = new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3));
            else Ui.Theme(card, Border.BorderBrushProperty, "ControlBorderBrush");
        }
    }

    // ── Misc helpers ──────────────────────────────────────────────────────────

    double Snap(double v) =>
        !_boardData.SnapToGrid || _boardData.GridSize < 1 ? v : Math.Round(v / _boardData.GridSize) * _boardData.GridSize;

    static (Color color, string symbol) EntityTypeStyle(CodeEntityType t) => t switch
    {
        CodeEntityType.Class     => (Color.FromRgb(0x19, 0x76, 0xD2), "🧱"),
        CodeEntityType.Struct    => (Color.FromRgb(0x00, 0x89, 0x7B), "📦"),
        CodeEntityType.Interface => (Color.FromRgb(0x6A, 0x1B, 0x9A), "🔷"),
        CodeEntityType.Enum      => (Color.FromRgb(0xE6, 0x51, 0x00), "📋"),
        CodeEntityType.Function  => (Color.FromRgb(0x2E, 0x7D, 0x32), "⚡"),
        CodeEntityType.Namespace => (Color.FromRgb(0x37, 0x47, 0x4F), "📁"),
        _                        => (Color.FromRgb(0x55, 0x55, 0x55), "⚙"),
    };

    static Color ParseColor(string hex)
    {
        try { return Color.Parse(hex); } catch { return Colors.DodgerBlue; }
    }

    Button Btn(string label, string? tooltip = null)
    {
        var b = Ui.Btn(label, tooltip);
        b.Padding  = new(10, 5);
        b.FontSize = 12;
        return b;
    }
}
