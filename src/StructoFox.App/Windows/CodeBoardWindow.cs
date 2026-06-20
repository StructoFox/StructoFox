using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
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
    ScrollViewer? _scroll;

    readonly Dictionary<string, CodeEntity> _entities  = new();   // entity id → entity
    readonly Dictionary<string, Border>     _cards     = new();   // entity id → card
    readonly Dictionary<string, Ellipse>    _portDots  = new();   // "{entityId}:{portId}" → dot
    readonly Dictionary<string, List<Control>> _relViews = new(); // relation id → line/arrow/hit visuals

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

    double _zoom = 1.0;

    Rectangle? _gridRect;                              // tiled alignment grid behind the cards
    bool   _panning;  Point _panStart;  Vector _panOrigin;   // right-drag canvas pan
    bool   _rightMaybeMenu;                            // a right press that opens the add-menu if it wasn't a drag

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

        Title                 = board.Symbol + "  " + board.Name;
        Width                 = 1280;
        Height                = 800;
        MinWidth              = 640;
        MinHeight             = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        if (!string.IsNullOrWhiteSpace(themePath))
            try { Resources.MergedDictionaries.Add(OxsuitLoader.Load(themePath)); } catch { /* unthemed is fine */ }
        Ui.ThemeWindow(this);

        BuildContent();
    }

    void Save() => CodeBoardDataService.Save(_projFolder, _board.Id, _boardData);

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
        _scroll.Content = _canvas;

        KeyDown += (_, e) => HandleKey(e);
        Focusable = true;

        _canvas.PointerPressed  += Canvas_PointerPressed;
        _canvas.PointerMoved    += Canvas_PointerMoved;
        _canvas.PointerReleased += Canvas_PointerReleased;

        // Accept entities dragged in from the cockpit's entity lists.
        DragDrop.SetAllowDrop(_canvas, true);
        _canvas.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        _canvas.AddHandler(DragDrop.DropEvent, OnDrop);

        // Ctrl + wheel zooms; plain wheel scrolls.
        _scroll.PointerWheelChanged += (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            SetZoom(_zoom + (e.Delta.Y > 0 ? 0.1 : -0.1));
            e.Handled = true;
        };

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

        var zoom = Btn(Loc.S("Common_ResetZoomTip"));
        zoom.Click += (_, _) => SetZoom(1.0);
        panel.Children.Add(zoom);

        panel.Children.Add(new Separator());
        panel.Children.Add(new TextBlock { Text = Loc.S("Grid_Header"), FontWeight = FontWeight.Bold });

        var show = new CheckBox { Content = Loc.S("Grid_Show"), IsChecked = _boardData.GridVisible };
        show.IsCheckedChanged += (_, _) => { _boardData.GridVisible = show.IsChecked == true; RenderGrid(); Save(); };
        panel.Children.Add(show);

        var snap = new CheckBox { Content = Loc.S("Grid_Snap"), IsChecked = _boardData.SnapToGrid };
        snap.IsCheckedChanged += (_, _) => { _boardData.SnapToGrid = snap.IsChecked == true; Save(); };
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

    // Keyboard: Delete removes the selection; Ctrl+0 resets zoom, Ctrl +/- and Ctrl+Up/Down zoom.
    void HandleKey(KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _selectedIds.Count > 0) { RemoveSelectedFromBoard(); return; }
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        switch (e.Key)
        {
            case Key.D0 or Key.NumPad0:                    SetZoom(1.0); e.Handled = true; break;
            case Key.OemPlus or Key.Add or Key.Up:         SetZoom(_zoom + 0.1); e.Handled = true; break;
            case Key.OemMinus or Key.Subtract or Key.Down: SetZoom(_zoom - 0.1); e.Handled = true; break;
        }
    }

    // Applies a clamped zoom level to the canvas.
    void SetZoom(double z)
    {
        _zoom = Math.Clamp(z, 0.25, 3.0);
        if (_canvas is not null)
            _canvas.RenderTransform = Math.Abs(_zoom - 1.0) < 0.001 ? null : new ScaleTransform(_zoom, _zoom);
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

    // Snaps a card's top-left so its CENTRE lands on a grid line (centre-aligned → straight links).
    double SnapCentered(double topLeft, double size) =>
        !_boardData.SnapToGrid || _boardData.GridSize < 1 ? topLeft : Snap(topLeft + size / 2) - size / 2;

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

            _selectedIds.Clear();
            _selectedIds.Add(entity.Id);
            _selectionAnchor = entity.Id;
            RefreshSelectionVisuals();

            dragging = true;
            offset   = e.GetPosition(card);
            e.Pointer.Capture(card);
            e.Handled = true;
        };

        card.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var pt = e.GetPosition(_canvas);
            // Snap the card's CENTRE to the grid so centre-aligned cards give straight links.
            var nx = SnapCentered(Math.Max(0, pt.X - offset.X), card.Bounds.Width);
            var ny = SnapCentered(Math.Max(0, pt.Y - offset.Y), card.Bounds.Height);
            Canvas.SetLeft(card, nx);
            Canvas.SetTop(card,  ny);
            GrowCanvasFor(nx, ny, card.Bounds.Width, card.Bounds.Height);
            UpdatePortPositions(entity.Id);
            UpdateRelationsForEntity(entity.Id);
            e.Handled = true;
        };

        card.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
            if (_boardData.Positions.TryGetValue(entity.Id, out var p))
            {
                p.X = Canvas.GetLeft(card);
                p.Y = Canvas.GetTop(card);
            }
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
        int n = ports.Count;
        for (int i = 0; i < n; i++)
        {
            var dot = GetOrCreatePortDot(entity.Id, ports[i]);
            double cx, cy;
            if (orientation == PortOrientation.Horizontal)
            {
                cx = isInput ? cardX - PortRadius : cardX + cardW - PortRadius;
                cy = cardY + cardH * (i + 1.0) / (n + 1.0) - PortRadius;
            }
            else
            {
                cx = cardX + cardW * (i + 1.0) / (n + 1.0) - PortRadius;
                cy = isInput ? cardY - PortRadius : cardY + cardH - PortRadius;
            }
            Canvas.SetLeft(dot, cx);
            Canvas.SetTop(dot,  cy);
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
            if (_connectMode) { CancelConnect(); return; }
            // Right press may start a pan (on drag) or open the add-menu (on a plain click).
            _panning = true; _rightMaybeMenu = true;
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
        if (_connectMode && _connectFromEntityId is not null && _rubberBand is not null)
        {
            if (GetPortCenter(_connectFromEntityId, _connectFromPortId!) is { } c) _rubberBand.StartPoint = c;
            _rubberBand.EndPoint = e.GetPosition(_canvas);
            return;
        }

        if (_panning)
        {
            var d = e.GetPosition(_scroll) - _panStart;
            if (_rightMaybeMenu && (Math.Abs(d.X) > 4 || Math.Abs(d.Y) > 4)) { _rightMaybeMenu = false; if (_canvas is not null) _canvas.Cursor = new Cursor(StandardCursorType.SizeAll); }
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
            if (_rightMaybeMenu) { _rightMaybeMenu = false; ShowCanvasAddMenu(e.GetPosition(_canvas)); }   // plain right-click → add menu
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

        var pos = new CodeCardPosition { X = 80, Y = 80 };
        _boardData.Positions[picked.Id] = pos;
        Save();
        RenderCard(picked, pos);
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
