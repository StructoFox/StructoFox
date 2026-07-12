using Avalonia;
using Avalonia.Controls;
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
    DiagramStyle _style;   // not readonly: undo/redo swaps _data (and thus its Style)
    IBrush _lineBrush = Brushes.Black;   // structural lines / borders
    IBrush _textBrush = Brushes.Black;   // block text
    IBrush _bgBrush   = Brushes.White;   // canvas background

    // Snapshot-based undo/redo of the structogram (JSON of _data), recorded at each Save() boundary.
    readonly List<string> _undo = new();
    readonly List<string> _redo = new();
    string _snapshot = "";
    static readonly System.Text.Json.JsonSerializerOptions _undoJson = new() { PropertyNameCaseInsensitive = true };

    ContextMenu? _menu;   // the one open context menu, so a new one closes the old (no stacking)

    // Opens a context menu over an anchor, first closing any menu still showing.
    void OpenMenu(ContextMenu cm, Control anchor) { _menu?.Close(); _menu = cm; cm.Open(anchor); }

    // Loads (or starts) the structogram for one function/method and builds the editor surface.
    public StructogramWindow(string projFolder, string key, string title, string? themePath)
    {
        _projFolder = projFolder;
        _key        = key;
        _themePath  = themePath;
        bool isNew  = !StructogramService.Exists(projFolder, key);
        _data       = StructogramService.Load(projFolder, key);
        if (string.IsNullOrEmpty(_data.Title)) _data.Title = title;
        // A brand-new structogram gets the user's chosen default header, if any.
        if (isNew) { HeaderTemplateService.ApplyDefault(isPap: false, _data.Style); StructogramService.Save(projFolder, key, _data); }
        _style      = _data.Style;   // persisted with the diagram
        _snapshot   = System.Text.Json.JsonSerializer.Serialize(_data);   // baseline for undo

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
        ThemeManager.FixFluentBrushes(this);   // theme popups (context menus) at window scope

        Build();
    }

    // Persists the current structogram to disk after every edit.
    void Save()
    {
        var cur = System.Text.Json.JsonSerializer.Serialize(_data);
        if (cur != _snapshot)
        {
            _undo.Add(_snapshot);
            if (_undo.Count > 100) _undo.RemoveAt(0);
            _redo.Clear();
            _snapshot = cur;
        }
        StructogramService.Save(_projFolder, _key, _data);
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

    // Restores a serialized state, persists it and rebuilds the diagram (incl. its surface brushes).
    void ApplySnapshot(string json)
    {
        StructogramData? d;
        try { d = System.Text.Json.JsonSerializer.Deserialize<StructogramData>(json, _undoJson); } catch { return; }
        if (d is null) return;
        _data = d;
        _style = _data.Style;
        _lineBrush = new SolidColorBrush(Color.Parse(_style.LineColor));
        _textBrush = new SolidColorBrush(Color.Parse(_style.TextColor));
        _bgBrush   = new SolidColorBrush(Color.Parse(_style.BackgroundColor));
        if (_hostBorder is not null) _hostBorder.Background = _bgBrush;
        StructogramService.Save(_projFolder, _key, _data);
        Rebuild();
        RefreshDecor();
        Title = string.Format(Loc.S("Struct_Title"), string.IsNullOrEmpty(_data.Title) ? Loc.S("Common_Untitled") : _data.Title);
    }

    // Assembles the toolbar + scrollable diagram host, then renders the tree once.
    void Build()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        Content = root;
        _root = root;

        Focusable = true;
        KeyDown += (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            switch (e.Key)
            {
                case Key.Z when !shift:                            Undo();  e.Handled = true; break;
                case Key.Y: case Key.Z:                            Redo();  e.Handled = true; break;
                case Key.OemPlus: case Key.Add:                    SetZoom(_zoom + 0.1); e.Handled = true; break;
                case Key.OemMinus: case Key.Subtract:              SetZoom(_zoom - 0.1); e.Handled = true; break;
                case Key.D0: case Key.NumPad0:                     SetZoom(1.0); e.Handled = true; break;
            }
        };

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

        // Code skeleton: send THIS structogram's function/method into the code exporter (copyable). Only meaningful
        // for structograms tied to a code entity (a project) — not standalone sketchbook sketches.
        var codeBtn = Ui.Btn(Loc.S("Struct_CodeExport"), Loc.S("Struct_CodeExportTip"));
        codeBtn.Click += (_, _) => OpenCodeExport();

        // Export the structogram as displayed (with decoration) to a single image file.
        var imgBtn = Ui.Btn(Loc.S("ImgExport_Menu"), Loc.S("ImgExport_Tip"));
        imgBtn.Click += async (_, _) =>
        {
            var body = BuildStructogramBody();
            if (body is null) { await MessageDialog.Show(this, Loc.S("ImgExport_Empty"), Loc.S("ImgExport_Title")); return; }
            await DiagramImageExporter.RunDialog(this, _data.Title, body, _data.Title, _style);
        };

        bar.Child = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children = { bgBtn, decorBtn, codeBtn, imgBtn, hint },
        };

        // Scrollable diagram host — the structogram can grow past the window. NOTE: no Padding here — a
        // ScrollViewer's padding sits INSIDE the viewport, so the far-side padding isn't scrollable (you
        // can't reach the right/bottom edge). The outer spacing is a Margin on the content instead.
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(_scroll, 1); root.Children.Add(_scroll);

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
            Padding             = new(22),   // breathing room so the structogram doesn't touch the canvas edge
        };
        // Wrap in a LayoutTransformControl so zoom scales the scrollable extent (not just the rendering).
        // The outer Margin is part of the scrollable content, so the right/bottom edge is always reachable.
        _zoomHost = new LayoutTransformControl
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Top,
            Margin              = new(20),
            Child               = _hostBorder,
        };
        _scroll.Content = _zoomHost;
        SetupZoomPan();

        Rebuild();
        RefreshDecor();
    }

    ScrollViewer? _scroll;
    LayoutTransformControl? _zoomHost;
    double _zoom = 1.0;
    bool _panning; Point _panStart; Vector _panOrigin;

    // Ctrl+wheel to zoom; left- or middle-drag to pan the canvas.
    void SetupZoomPan()
    {
        if (_scroll is null) return;

        _scroll.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;   // plain wheel = normal scroll
            e.Handled = true;
            ZoomAt(_zoom + (e.Delta.Y > 0 ? 0.1 : -0.1), e.GetPosition(_scroll));
        }, RoutingStrategies.Tunnel);

        _scroll.PointerPressed += (_, e) =>
        {
            var p = e.GetCurrentPoint(_scroll);
            if (!p.Properties.IsLeftButtonPressed && !p.Properties.IsMiddleButtonPressed) return;
            _panning = true;
            _panStart = p.Position;
            _panOrigin = _scroll!.Offset;
        };
        _scroll.PointerMoved += (_, e) =>
        {
            if (!_panning) return;
            var d = e.GetPosition(_scroll) - _panStart;
            if (_scroll!.Cursor is null && (Math.Abs(d.X) > 4 || Math.Abs(d.Y) > 4))
                _scroll.Cursor = new Cursor(StandardCursorType.SizeAll);
            _scroll.Offset = new Vector(_panOrigin.X - d.X, _panOrigin.Y - d.Y);
        };
        void EndPan() { _panning = false; if (_scroll is not null) _scroll.Cursor = null; }
        _scroll.PointerReleased    += (_, _) => EndPan();
        _scroll.PointerCaptureLost += (_, _) => EndPan();
    }

    // Applies the current zoom as a LayoutTransform (so the scroll viewer scrolls the whole scaled diagram).
    void ApplyZoom()
    {
        if (_zoomHost is not null)
            _zoomHost.LayoutTransform = Math.Abs(_zoom - 1.0) < 0.001 ? null : new ScaleTransform(_zoom, _zoom);
    }

    // Keyboard zoom: anchor on the viewport centre.
    void SetZoom(double z)
    {
        if (_scroll is null) { _zoom = Math.Clamp(z, 0.1, 3.0); ApplyZoom(); return; }
        ZoomAt(z, new Point(_scroll.Viewport.Width / 2, _scroll.Viewport.Height / 2));
    }

    // Zooms to a clamped level while keeping the content point under the given viewport position fixed.
    void ZoomAt(double z, Point viewportPos)
    {
        z = Math.Clamp(z, 0.1, 3.0);
        if (_scroll is null || Math.Abs(z - _zoom) < 0.0001) { _zoom = z; ApplyZoom(); return; }

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

    Grid? _root;
    // Rebuilds the decoration (title / info field / watermark / logo). It lives INSIDE the canvas content, so it
    // scrolls and zooms with the diagram and travels into print / PDF / image exports; edge decorations reserve
    // an empty band so they never overlap the structogram.
    void RefreshDecor()
    {
        Rebuild();
        // After a header change the content size may shrink; the zoom wrapper + scroll presenter cache their
        // size and won't shrink on their own — re-attach the content so the scroll extent is recomputed.
        if (_scroll is not null && _zoomHost is { } zh) { _scroll.Content = null; _scroll.Content = zh; }
    }

    // Opens the decoration dialog (title / watermark / logo) and re-applies on OK.
    async Task OpenDecor()
    {
        // Offer to pull the whole header from the matching flowchart (same function/method key).
        var newTitle = await DiagramDecorDialog.Show(this, _data.Title, _style,
            () => { var fc = FlowChartService.Load(_projFolder, _key); return (fc.Style, fc.Title); },
            ProjectService.DisplayName(_projFolder));
        if (newTitle is null) return;
        _data.Title = newTitle;
        Save();
        RefreshDecor();
        Title = string.Format(Loc.S("Struct_Title"), string.IsNullOrEmpty(newTitle) ? Loc.S("Common_Untitled") : newTitle);
    }

    // Opens the code exporter for JUST this structogram: resolves the function/method its key refers to and passes
    // that single entity (a method key trims the owning entity down to the one method). No AI — a plain skeleton with
    // the body coming from this structogram. Standalone sketchbook structograms have no code entity → info dialog.
    void OpenCodeExport() => CrashHandler.Safe(() =>
    {
        var byId = new Dictionary<string, CodeEntity>();
        foreach (var t in Enum.GetValues<CodeEntityType>())
            foreach (var ent in CodeEntityService.LoadAll(_projFolder, t.ToString()))
                byId[ent.Id] = ent;

        CodeEntity? target = null;
        int hash = _key.IndexOf('#');
        if (hash >= 0)
        {
            // "entityId#methodId": keep only that one method of the freshly-loaded owning entity.
            var ownerId = _key[..hash]; var methodId = _key[(hash + 1)..];
            if (byId.TryGetValue(ownerId, out var owner))
            {
                owner.Methods = owner.Methods.Where(m => m.Id == methodId).ToList();
                target = owner;
            }
        }
        else if (byId.TryGetValue(_key, out var fn))
            target = fn;

        // Standalone (e.g. sketchbook) structogram with no code entity: synthesise a free FUNCTION whose id IS this
        // structogram's key, so the exporter pulls THIS structogram as its body (functions fetch bodies by e.Id).
        // Gives a copy-paste-ready function skeleton even for a quick one-off diagram.
        target ??= new CodeEntity
        {
            Id = _key, EntityType = CodeEntityType.Function,
            Name = string.IsNullOrWhiteSpace(_data.Title) ? "Function" : _data.Title.Trim(),
        };
        new ExportWindow(_projFolder, new[] { target }, target.Name).Show();
    }, "StructCodeExport");

    // Re-renders the whole tree from the model — cheap enough to do on every change. The decoration overlay
    // (title/watermark/logo) is composited on top, INSIDE the canvas, so it travels with the diagram.
    void Rebuild()
    {
        if (_hostBorder is null) return;
        var diagram = RenderSequence(_data.Root, isRoot: true);
        _hostBorder.Child = DiagramDecor.Compose(diagram, _data.Title, _style, () => _ = OpenDecor());
    }

    /// <summary>The structogram body as a LIVE control (no decoration, no "＋" add row) — text/lines stay crisp at any
    /// scale, so the print composer places it as a scaled control instead of a bitmap. Null if empty. UI thread.</summary>
    public Control? BuildStructogramBody()
    {
        if (_data.Root.Count == 0) return null;
        return new Border
        {
            Background = Brushes.Transparent, Child = RenderSequence(_data.Root, isRoot: false),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
    }

    /// <summary>Renders JUST the structogram body (no decoration, no "＋" add row), transparent + tight to the
    /// content, at <paramref name="scale"/> (1.0 = 96 DPI). The bitmap is DPI-NEUTRAL (96) so embedding it in the
    /// DPI-scaled print export doesn't double-scale (same rule as the flowchart renderer). Null if empty. UI thread.</summary>
    public Avalonia.Media.Imaging.RenderTargetBitmap? RenderStructogramOnly(double scale = 1.0)
    {
        try
        {
            if (_data.Root.Count == 0) return null;
            var host = new Border
            {
                Background = Brushes.Transparent, Child = RenderSequence(_data.Root, isRoot: false),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            };
            host.Measure(Size.Infinity);
            var size = host.DesiredSize;
            if (size.Width < 1 || size.Height < 1) return null;
            host.Arrange(new Rect(size));

            int pw = Math.Max(1, (int)Math.Ceiling(size.Width  * scale));
            int ph = Math.Max(1, (int)Math.Ceiling(size.Height * scale));
            var full = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(pw, ph), new Vector(96 * scale, 96 * scale));
            full.Render(host);
            // Copy into a DPI-neutral (96) bitmap sized purely in pixels — the caller controls the footprint by pixels.
            var crop = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(pw, ph), new Vector(96, 96));
            using (var ctx = crop.CreateDrawingContext())
                ctx.DrawImage(full, new Rect(0, 0, pw, ph), new Rect(0, 0, pw, ph));
            full.Dispose();
            return crop;
        }
        catch { return null; }
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
            NsBlockKind.Jump      => JumpBox(b),
            _                     => StatementBox(b),
        };

        // A carried-over PAP "Bemerkung" rides as a small italic comment chip in the block's top-right corner.
        Control content = inner;
        if (!string.IsNullOrWhiteSpace(b.Note))
        {
            var note = new Border
            {
                Background          = new SolidColorBrush(Color.FromArgb(0x30, 0xF5, 0x7F, 0x17)),
                CornerRadius        = new(2),
                Padding             = new(3, 1, 3, 1),
                Margin              = new(0, 2, 2, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Top,
                IsHitTestVisible    = false,
                Child = new TextBlock { Text = "⌐ " + b.Note, FontStyle = FontStyle.Italic, FontSize = 10, Opacity = 0.85, Foreground = _textBrush },
            };
            ToolTip.SetTip(note, b.Note);
            content = new Grid { Children = { inner, note } };
        }

        var cell = new Border
        {
            BorderThickness = new(b.Flagged ? _style.LineThickness * 2 : _style.LineThickness),
            Child           = content,
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

    // The DIN 66261 early-exit symbol: a left-pointing arrow (drawn from lines) with the keyword inside
    // (return / break / continue / exit, or whatever text the user's End node carried).
    Control JumpBox(NsBlock b)
    {
        var word = string.IsNullOrWhiteSpace(b.Text) ? Loc.S("Struct_DefJump") : b.Text;
        var t = PrimaryLabel(word, b);
        t.Margin = new(4, 6, 8, 6);
        t.VerticalAlignment = VerticalAlignment.Center;
        t.FontWeight = FontWeight.SemiBold;

        var stroke = Solid(b.Style?.LineColor) ?? _lineBrush;
        var th     = _style.LineThickness;
        var arrow  = new Canvas { Width = 22, Height = 16, Margin = new(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        // shaft (right→left) + arrowhead pointing left, so the exit "leaves" the diagram to the left.
        arrow.Children.Add(new Polyline { Points = { new(20, 8), new(3, 8) }, Stroke = stroke, StrokeThickness = th });
        arrow.Children.Add(new Polyline { Points = { new(9, 3), new(3, 8), new(9, 13) }, Stroke = stroke, StrokeThickness = th });

        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(arrow, 0); g.Children.Add(arrow);
        Grid.SetColumn(t, 1);     g.Children.Add(t);
        g.DoubleTapped += (_, _) => EditText(b);
        return g;
    }

    // The subroutine (predefined-process) box: a centred name with the two vertical inner bars,
    // double-click opens its linked diagram ("show chart").
    Control SubroutineBox(NsBlock b)
    {
        var text = string.IsNullOrWhiteSpace(b.Text) ? Loc.S("Struct_PhSubroutine") : b.Text;
        text = text.Replace(".", "." + (char)0x200B);   // prefer wrapping qualified names at the dots
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
            var r = await SubroutineLinkDialog.Show(this, _projFolder, "", _key);
            if (r is null || string.IsNullOrEmpty(r.Id)) return;
            b.RefId = r.Id; b.Text = SubroutineLinkDialog.CallText(_projFolder, r); Save(); Rebuild();
        }
        _ = DiagramLauncher.ChooseAndOpen(this, _projFolder, b.RefId,
            SubroutineLinkDialog.RefName(_projFolder, b.RefId), _themePath);
    }

    // The classic if/else box (DIN 66261): the condition sits in a triangle formed by two diagonals running
    // from the top corners to the bottom centre; the then/else captions sit in the two lower corners. Then the
    // two side-by-side branches, split at the centre under the apex.
    Control IfBox(NsBlock b)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // triangle header
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star)); // branches

        var cond = PrimaryLabel(string.IsNullOrWhiteSpace(b.Text) ? Loc.S("Struct_PhCondition") : b.Text, b);
        cond.TextAlignment = TextAlignment.Center;
        cond.DoubleTapped += (_, _) => EditText(b);

        var trueCap  = string.IsNullOrWhiteSpace(b.TrueLabel)  ? Loc.S("Struct_True")  : b.TrueLabel;
        var falseCap = string.IsNullOrWhiteSpace(b.FalseLabel) ? Loc.S("Struct_False") : b.FalseLabel;
        var tl = LabelText(trueCap);  tl.FontSize = 10; tl.Opacity = 0.7;
        var fl = LabelText(falseCap); fl.FontSize = 10; fl.Opacity = 0.7;

        var layout = new IfHeaderPanel();
        layout.Children.Add(cond); layout.Children.Add(tl); layout.Children.Add(fl);
        var header = new Grid();
        header.Children.Add(layout);
        header.Children.Add(new HeaderDiagonals { Stroke = _lineBrush, StrokeThickness = _style.LineThickness, TwoLines = true });
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        // Two branch columns — equal width, each at least as wide as its content (no clipping).
        var cols = new BranchColumns();
        var thenCol = RenderSequence(b.Body);
        var elseCol = RenderSequence(b.Else);
        cols.Children.Add(thenCol);
        cols.Children.Add(LeftBorder(elseCol));
        var colsWrap = TopBorder(cols); Grid.SetRow(colsWrap, 1);
        grid.Children.Add(colsWrap);

        return grid;
    }

    // A loop box: condition above (pre-test/while) or below (post-test/do-while), body inset by a bracket.
    Control LoopBox(NsBlock b, bool preTest)
    {
        var outer = new StackPanel();
        var kw = preTest ? Loc.S("Struct_KwWhile") : Loc.S("Struct_KwDoWhile");
        var cond = PrimaryLabel(string.IsNullOrWhiteSpace(b.Text)
            ? (preTest ? Loc.S("Struct_PhWhile") : Loc.S("Struct_PhDoWhile"))
            : $"{kw} {b.Text}", b);
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

    // A multi-way case box (DIN 66261): the selector sits in a triangle above a single diagonal that slants
    // down to the case labels; below it, N equal columns each with its label and body.
    Control CaseBox(NsBlock b)
    {
        var outer = new StackPanel();
        if (b.Arms.Count == 0) b.Arms.Add(new NsArm());

        // Diagonal header: selector in the upper-left triangle, the case labels along the bottom of each column.
        var head = PrimaryLabel(string.IsNullOrWhiteSpace(b.Text) ? Loc.S("Struct_PhSelector") : b.Text, b);
        head.DoubleTapped += (_, _) => EditText(b);
        var layout = new CaseHeaderPanel { Arms = b.Arms.Count };
        layout.Children.Add(head);
        for (int i = 0; i < b.Arms.Count; i++)
        {
            var arm = b.Arms[i];
            var lbl = LabelText(string.IsNullOrWhiteSpace(arm.Label) ? Loc.S("Struct_Case") : arm.Label);
            lbl.FontSize = 10; lbl.Opacity = 0.8; lbl.TextAlignment = TextAlignment.Center;
            var capArm = arm;
            lbl.DoubleTapped += (_, _) => EditArmLabel(capArm);
            layout.Children.Add(lbl);
        }
        var header = new Grid();
        header.Children.Add(layout);
        header.Children.Add(new HeaderDiagonals { Stroke = _lineBrush, StrokeThickness = _style.LineThickness, TwoLines = false });
        outer.Children.Add(header);

        // Body columns under the labels — equal width, each at least as wide as its content (no clipping).
        var cols = new BranchColumns();
        for (int i = 0; i < b.Arms.Count; i++)
        {
            var body = RenderSequence(b.Arms[i].Body);
            cols.Children.Add(i == 0 ? body : LeftBorder(body));
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
        Add(Loc.S("Struct_KJump"), NsBlockKind.Jump);
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
            Add(Loc.S("Struct_KJump"), NsBlockKind.Jump);
            OpenMenu(cm, b);
        };
        return b;
    }

    // Prompts for a block's text/condition and saves the edit.
    async void EditText(NsBlock b)
    {
        var t = await PromptDialog.Show(this,
            b.Kind is NsBlockKind.Statement or NsBlockKind.Jump ? Loc.S("Struct_PromptStatement") : Loc.S("Struct_PromptCondition"),
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
        NsBlockKind.Jump    => Loc.S("Struct_DefJump"),
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

/// <summary>DIN 66261 IF header layout: the condition centred at the top (in the downward triangle), with the
/// then/else captions in the two lower corners. Children: [0]=condition, [1]=true caption, [2]=false caption.
/// The diagonals themselves are drawn by an overlaid <see cref="HeaderDiagonals"/>.</summary>
/// <summary>Lays DIN if/case branches side by side. Measured width = SUM of the branches' content widths
/// (bounded — no explosion), and on arrange each column is sized in PROPORTION to its content, so every column
/// is at least as wide as its content (never clipped) while together filling the block width. Each column
/// fills the full height (branches are equal height, DIN-style).</summary>
internal class BranchColumns : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        double w = 0, h = 0;
        foreach (var c in Children)
        {
            c.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            w += c.DesiredSize.Width;
            h  = Math.Max(h, c.DesiredSize.Height);
        }
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double sum = 0;
        foreach (var c in Children) sum += c.DesiredSize.Width;
        if (sum <= 0) sum = 1;

        double x = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            // Last column takes the remainder, so rounding never leaves a gap or overshoot.
            double cw = i == Children.Count - 1
                ? Math.Max(0, finalSize.Width - x)
                : finalSize.Width * (Children[i].DesiredSize.Width / sum);
            Children[i].Arrange(new Rect(x, 0, cw, finalSize.Height));
            x += cw;
        }
        return finalSize;
    }
}

internal class IfHeaderPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var c in Children) c.Measure(availableSize);
        var cond = Children[0].DesiredSize; var tl = Children[1].DesiredSize; var fl = Children[2].DesiredSize;
        double w = Math.Max(cond.Width + 24, tl.Width + fl.Width + 28);
        double h = cond.Height + 8 + Math.Max(tl.Height, fl.Height) + 6;
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double W = finalSize.Width, H = finalSize.Height;
        var cond = Children[0]; var tl = Children[1]; var fl = Children[2];
        var cs = cond.DesiredSize; cond.Arrange(new Rect((W - cs.Width) / 2, 3, cs.Width, cs.Height));
        var ts = tl.DesiredSize; tl.Arrange(new Rect(Math.Max(4, W * 0.25 - ts.Width / 2), H - ts.Height - 3, ts.Width, ts.Height));
        var fs = fl.DesiredSize; fl.Arrange(new Rect(Math.Min(W - fs.Width - 4, W * 0.75 - fs.Width / 2), H - fs.Height - 3, fs.Width, fs.Height));
        return finalSize;
    }
}

/// <summary>DIN 66261 CASE header layout: the selector in the upper-left triangle, the case labels along the
/// bottom of each column. Children: [0]=selector, [1..N]=labels. The slant is drawn by <see cref="HeaderDiagonals"/>.</summary>
internal class CaseHeaderPanel : Panel
{
    public int Arms { get; set; } = 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var c in Children) c.Measure(availableSize);
        var sel = Children[0].DesiredSize;
        double labelW = 0, labelH = 0;
        for (int i = 1; i < Children.Count; i++) { labelW += Children[i].DesiredSize.Width + 10; labelH = Math.Max(labelH, Children[i].DesiredSize.Height); }
        double w = Math.Max(sel.Width + 30, labelW);
        double h = sel.Height + 10 + labelH + 6;
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double W = finalSize.Width, H = finalSize.Height;
        var sel = Children[0]; var ss = sel.DesiredSize;
        sel.Arrange(new Rect(6, 3, ss.Width, ss.Height));   // selector top-left, in the upper triangle
        int n = Math.Max(1, Arms);
        double cw = W / n;
        for (int i = 1; i < Children.Count; i++)
        {
            var ls = Children[i].DesiredSize;
            double x = (i - 1) * cw + (cw - ls.Width) / 2;
            Children[i].Arrange(new Rect(x, H - ls.Height - 3, ls.Width, ls.Height));
        }
        return finalSize;
    }
}

/// <summary>Transparent overlay that draws the DIN branch-header diagonals over a header panel: two lines from
/// the top corners to the bottom centre (IF), or one slant from the top-right to the bottom-left (CASE).</summary>
internal class HeaderDiagonals : Control
{
    public HeaderDiagonals() => IsHitTestVisible = false;   // clicks pass through to the text below (edit on dbl-tap)

    public IBrush Stroke { get; set; } = Brushes.Black;
    public double StrokeThickness { get; set; } = 1;
    public bool   TwoLines { get; set; }   // true = IF triangle, false = CASE single slant

    public override void Render(DrawingContext ctx)
    {
        var pen = new Pen(Stroke, StrokeThickness);
        double W = Bounds.Width, H = Bounds.Height;
        if (TwoLines)
        {
            ctx.DrawLine(pen, new Point(0, 0), new Point(W / 2, H));
            ctx.DrawLine(pen, new Point(W, 0), new Point(W / 2, H));
        }
        else
        {
            ctx.DrawLine(pen, new Point(W, 0), new Point(0, H));
        }
    }
}
