using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using StructoFox.Core;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// The print / export composer: lays PAPs, structograms and boards (plus labels, free text and legends) out on
/// paper-sized pages, saved under the project's <c>print/</c> folder and exported to PDF / TIFF / PNG.
/// v1 in progress — this shell covers pages, paper format, label items, and persistence; diagram items, rich
/// text, legends and export land on top of the shared diagram renderer.
/// </summary>
public class PrintComposerWindow : Window
{
    readonly string        _projFolder;
    PrintDocument          _doc;
    string?                _savedName;    // the name the doc was last saved/loaded as (for rename cleanup)
    int                    _pageIndex;

    // Layout scale from device-independent page px (@96) to on-screen px (zoom-to-fit-ish).
    double _viewScale = 0.75;

    // Rendered diagram bitmaps, keyed by item id → (bitmap, the item.Scale it was rendered at). Re-rendered
    // (crisp) when the scale changes or on Update, never raster-stretched.
    readonly Dictionary<string, (RenderTargetBitmap Bmp, double Scale)> _diagramCache = new();

    // Current selection (item ids) and the on-page visual per item, so a group drag can move every selected
    // visual live without a full re-render.
    readonly HashSet<string>        _selected = new();
    readonly Dictionary<string, Control> _visuals = new();
    bool   _exporting;        // true while rendering a page off-screen for export (crisp bitmaps, no chrome/grid)
    double _exportDensity = 1;// device px per DIP during export (dpi / 96), so diagrams render crisp at that dpi

    Canvas?        _pageCanvas;     // holds the item visuals
    Border?        _pageBorder;     // the paper rectangle
    ScrollViewer?  _scroll;         // pan/scroll host around the page
    TextBlock?     _pageLabel;

    PrintPage Page => _doc.Pages[_pageIndex];

    public PrintComposerWindow(string projFolder)
    {
        _projFolder = projFolder;
        _doc = new PrintDocument();   // start fresh & predictable; use File → Load to open a saved document
        _savedName = null;

        Width = 1080; Height = 780; MinWidth = 720; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Ui.ThemeWindow(this);

        Content = Build();
        RenderPage();
        UpdateTitle();
    }

    // ── Shell ────────────────────────────────────────────────────────────────
    Control Build()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));

        var bar = BuildToolbar();
        Grid.SetRow(bar, 0); root.Children.Add(bar);

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
        };
        _scroll = scroll;
        // NOT clipped: items may extend beyond the paper on purpose — e.g. a big PAP tiled across several smaller
        // pages, each page showing a different section. The page rectangle marks the export area; the export clips.
        _pageCanvas = new Canvas { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        _pageBorder = new Border
        {
            Child = _pageCanvas,
            BorderThickness = new(1),
            BorderBrush = Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 12, OffsetX = 0, OffsetY = 4, Color = Color.FromArgb(60, 0, 0, 0) }),
        };
        // Margin (not ScrollViewer.Padding) so the gutter counts toward the scrollable extent — otherwise the
        // bottom edge (padding + box shadow) can't be scrolled into view when the window is smaller than the page.
        scroll.Content = new StackPanel { Margin = new(24), Children = { _pageBorder } };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);

        // Left-drag on empty paper (not an item) pans the canvas — and clears the selection. Wired on the
        // ScrollViewer via bubbling: an item press sets Handled (so this won't fire), an empty press bubbles up.
        WirePan(scroll);

        // Ctrl + mouse wheel zooms, like the diagram editors.
        scroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            Zoom(e.Delta.Y > 0 ? 1.1 : 1 / 1.1); e.Handled = true;
        }, RoutingStrategies.Tunnel);

        KeyDown += OnKeyDown;
        return root;
    }

    // Panning by dragging the empty page background; also clears any selection on press. Item presses set
    // Handled, so this bubbling handler only fires on the empty background.
    void WirePan(ScrollViewer scroll)
    {
        bool panning = false; Point start = default; Vector origin = default;
        scroll.PointerPressed += (_, e) =>
        {
            var p = e.GetCurrentPoint(scroll);
            if (!p.Properties.IsLeftButtonPressed) return;
            if (_selected.Count > 0) { _selected.Clear(); RenderPage(); }
            panning = true; start = p.Position; origin = scroll.Offset;
            e.Pointer.Capture(scroll); e.Handled = true;
        };
        scroll.PointerMoved += (_, e) =>
        {
            if (!panning) return;
            var pos = e.GetPosition(scroll);
            scroll.Offset = origin - new Vector(pos.X - start.X, pos.Y - start.Y);
        };
        scroll.PointerReleased += (_, e) => { if (panning) { panning = false; e.Pointer.Capture(null); } };
    }

    // Zoom the page view (view scale), clamped, re-rendering items crisp at the new size.
    void Zoom(double factor) => SetZoom(_viewScale * factor);
    void SetZoom(double scale)
    {
        _viewScale = Math.Clamp(scale, 0.15, 4.0);
        _diagramCache.Clear();   // bitmaps re-render at the new view scale
        RenderPage();
    }

    // Strg+A = select all on the page, Delete = remove the selection, Escape = deselect. Ignored while typing
    // in a text box (so Strg+A there still selects text).
    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) return;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (ctrl && e.Key is Key.OemPlus or Key.Add)       { Zoom(1.1);        e.Handled = true; return; }
        if (ctrl && e.Key is Key.OemMinus or Key.Subtract) { Zoom(1 / 1.1);    e.Handled = true; return; }
        if (ctrl && e.Key is Key.D0 or Key.NumPad0)        { SetZoom(1.0);     e.Handled = true; return; }   // 100 %
        if (ctrl && e.Key == Key.A)
        {
            _selected.Clear();
            foreach (var it in Page.Items) _selected.Add(it.Id);
            RenderPage(); e.Handled = true;
        }
        else if (e.Key == Key.Delete && _selected.Count > 0)
        {
            Page.Items.RemoveAll(it => _selected.Contains(it.Id));
            foreach (var id in _selected) _diagramCache.Remove(id);
            _selected.Clear(); RenderPage(); e.Handled = true;
        }
        else if (e.Key == Key.Escape && _selected.Count > 0)
        {
            _selected.Clear(); RenderPage(); e.Handled = true;
        }
    }

    Border BuildToolbar()
    {
        var bar = new Border { Padding = new(12, 8) };
        Ui.Theme(bar, Border.BackgroundProperty, "SidebarBgBrush");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        bar.Child = row;

        // File menu: New / Rename / Save / Load. (The document name — shown in the Load list — is edited here.)
        var file = TB(Loc.S("Pc_MenuFile"));
        var miNew    = new MenuItem { Header = Loc.S("Pc_NewDoc") }; miNew.Click += (_, _) => NewDoc();
        var miRename = new MenuItem { Header = Loc.S("Pc_Rename") }; miRename.Click += (_, _) => _ = RenameDoc();
        var miSave   = new MenuItem { Header = Loc.S("Pc_Save") }; miSave.Click += (_, _) => _ = Save();
        var miSaveAs = new MenuItem { Header = Loc.S("Pc_SaveAs") }; miSaveAs.Click += (_, _) => _ = SaveAs();
        var miLoad   = new MenuItem { Header = Loc.S("Pc_Load") }; miLoad.Click += (_, _) => _ = OpenLoadDialog();
        var miExport = new MenuItem { Header = Loc.S("Pc_ExportPng") }; miExport.Click += (_, _) => _ = ExportPng();
        file.Flyout = new MenuFlyout { ItemsSource = new[] { miNew, miRename, new Separator() as Control, miSave, miSaveAs, miLoad, new Separator() as Control, miExport } };
        row.Children.Add(file);

        // Options menu: paper format, grid, insert header.
        var opts = TB(Loc.S("Pc_MenuOptions"));
        var miPaper  = new MenuItem { Header = Loc.S("Pc_PaperFormat") };  miPaper.Click  += (_, _) => OpenPaperDialog();
        var miGrid   = new MenuItem { Header = Loc.S("Pc_GridSettings") }; miGrid.Click   += (_, _) => OpenGridDialog();
        var miHeader = new MenuItem { Header = Loc.S("Pc_InsertHeader") }; miHeader.Click += (_, _) => _ = AddHeader();
        opts.Flyout = new MenuFlyout { ItemsSource = new[] { miPaper, miGrid, miHeader } };
        row.Children.Add(opts);

        row.Children.Add(Sep());

        // Page navigation.
        var prev = TB("‹"); prev.Click += (_, _) => GoToPage(_pageIndex - 1);
        _pageLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0) };
        Ui.Theme(_pageLabel, TextBlock.ForegroundProperty, "SidebarTextBrush");
        var next = TB("›"); next.Click += (_, _) => GoToPage(_pageIndex + 1);
        var addPage = TB("＋ Page"); addPage.Click += (_, _) => { _doc.Pages.Insert(_pageIndex + 1, new PrintPage()); GoToPage(_pageIndex + 1); };
        var delPage = TB("🗑 Page"); delPage.Click += (_, _) => RemovePage();
        row.Children.Add(prev); row.Children.Add(_pageLabel); row.Children.Add(next);
        row.Children.Add(addPage); row.Children.Add(delPage);

        row.Children.Add(Sep());

        // Insert diagram / text.
        var addDiagram = TB("＋ Diagram"); addDiagram.Click += (_, _) => _ = AddDiagram();
        row.Children.Add(addDiagram);
        var addText = TB(Loc.S("Pc_AddText"));
        var labelMi = new MenuItem { Header = Loc.S("Pc_LabelSingle") }; labelMi.Click += (_, _) => AddLabel();
        var boxMi   = new MenuItem { Header = Loc.S("Pc_TextMulti") }; boxMi.Click += (_, _) => AddTextBox();
        addText.Flyout = new MenuFlyout { ItemsSource = new[] { labelMi, boxMi } };
        row.Children.Add(addText);

        row.Children.Add(Sep());

        // Zoom controls (also Ctrl +/- / 0 and Ctrl+wheel).
        var zoomOut = TB("🔍−"); zoomOut.Click += (_, _) => Zoom(1 / 1.1);
        var zoomIn  = TB("🔍+"); zoomIn.Click  += (_, _) => Zoom(1.1);
        var zoom100 = TB("100%"); zoom100.Click += (_, _) => SetZoom(1.0);
        row.Children.Add(zoomOut); row.Children.Add(zoomIn); row.Children.Add(zoom100);

        return bar;
    }

    // Shows the current document name in the window title, so it's always clear what's being edited/saved.
    void UpdateTitle() => Title = $"Print / Export — {_doc.Name}" + (_savedName is null ? " *" : "");

    // Save: to the current file if already saved; otherwise ask for a name (Save As).
    async Task Save()
    {
        if (string.IsNullOrWhiteSpace(_savedName)) { await SaveAs(); return; }
        PrintDocumentService.Save(_projFolder, _doc, _savedName);
        _savedName = _doc.Name; Notify(Loc.S("Pc_Saved")); UpdateTitle();
    }

    // Save as: ask for a name, confirm before overwriting a different existing document, then save a new file.
    async Task SaveAs()
    {
        var n = await PromptDialog.Show(this, Loc.S("Pc_Name"), _doc.Name, Loc.S("Pc_SaveAs"));
        if (string.IsNullOrWhiteSpace(n)) return;
        n = n.Trim();
        bool sameAsCurrent = string.Equals(n, _savedName, StringComparison.OrdinalIgnoreCase);
        if (!sameAsCurrent && PrintDocumentService.Load(_projFolder, n) is not null)
        {
            var r = await MessageDialog.Show(this, string.Format(Loc.S("Pc_OverwriteConfirm"), n), Loc.S("Pc_SaveAs"), DialogButtons.YesNo);
            if (r != DialogResult.Yes) return;
        }
        _doc.Name = n;
        PrintDocumentService.Save(_projFolder, _doc);   // a new file under the new name (Save As = copy, no move)
        _savedName = n; Notify(Loc.S("Pc_Saved")); UpdateTitle();
    }

    // ── Export ───────────────────────────────────────────────────────────────

    // Renders one page off-screen to a bitmap at the given DPI — no selection chrome, no grid, diagrams re-rendered
    // crisp at the target density.
    RenderTargetBitmap ExportPageBitmap(int page, double dpi, List<DocumentExporter.PdfTextRun>? textSink = null)
    {
        var savedIndex = _pageIndex; var savedScale = _viewScale; var savedExport = _exporting; var savedDensity = _exportDensity;
        // Build the page at 1:1 DIP; the bitmap's own DPI scales it to the target resolution (so the PNG carries
        // the correct physical size AND resolution — higher DPI = sharper, NOT bigger).
        double density = dpi / 96.0;
        // Build the page DIRECTLY at target pixel size (everything × density in DIP), then render 1:1 (RTB dpi 96).
        // No RTB-DPI scaling, so there is NO way for the diagram bitmap (rendered at density) to be scaled twice.
        // The real DPI goes into the PNG metadata separately (SetPngMetadata).
        _pageIndex = page; _viewScale = density; _exporting = true; _exportDensity = density;
        try
        {
            var pg = _doc.Pages[page];
            var (w, h) = pg.SizePx;                     // DIP
            double sw = w * density, sh = h * density;  // scaled DIP == output pixels (rendered 1:1)
            var canvas = new Canvas { Width = sw, Height = sh, Background = new SolidColorBrush(Color.Parse(pg.Background)) };
            // Only DIAGRAMS (PAPs/structograms) are clipped to the printable area — that clip is what lets a big PAP
            // be tiled manually across pages. Headers, text boxes, labels and decoration must NOT be clipped: they may
            // sit slightly inside the margin and should still print in full (their own frame is the boundary).
            var pr = pg.PrintablePx;
            var diagramLayer = new Canvas { Width = sw, Height = sh,
                Clip = new RectangleGeometry(new Rect(pr.X * density, pr.Y * density, pr.W * density, pr.H * density)) };
            var overlayLayer = new Canvas { Width = sw, Height = sh };   // headers / text / labels / decoration (unclipped)
            foreach (var item in pg.Items.OrderBy(i => i.ZOrder))
            {
                var raw = BuildItemVisual(item);
                if (raw is null) continue;
                Canvas.SetLeft(raw, item.X * density); Canvas.SetTop(raw, item.Y * density);
                (item is DiagramItem ? diagramLayer : overlayLayer).Children.Add(raw);
            }
            canvas.Children.Add(diagramLayer);
            canvas.Children.Add(overlayLayer);
            canvas.Measure(new Size(sw, sh));
            canvas.Arrange(new Rect(0, 0, sw, sh));
            // Collect a selectable text layer (for searchable PDF): every TextBlock's text + its on-canvas pixel
            // rect. Covers free text + header/structogram text; diagram bitmaps have no TextBlocks, so they stay raster.
            if (textSink is not null)
                foreach (var tb in canvas.GetVisualDescendants().OfType<TextBlock>())
                {
                    var text = tb.Text;
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    if (tb.TransformToVisual(canvas) is not { } m) continue;
                    var b = tb.Bounds;
                    var p0 = m.Transform(new Point(0, 0));
                    var p1 = m.Transform(new Point(b.Width, b.Height));
                    textSink.Add(new DocumentExporter.PdfTextRun(text,
                        Math.Min(p0.X, p1.X), Math.Min(p0.Y, p1.Y), Math.Abs(p1.Y - p0.Y)));
                }

            var px = new PixelSize(Math.Max(1, (int)Math.Ceiling(sw)), Math.Max(1, (int)Math.Ceiling(sh)));
            var rtb = new RenderTargetBitmap(px, new Vector(96, 96));   // 1:1; DPI metadata written by SetPngMetadata
            rtb.Render(canvas);
            return rtb;
        }
        finally { _pageIndex = savedIndex; _viewScale = savedScale; _exporting = savedExport; _exportDensity = savedDensity; }
    }

    // Export every page as its own PNG (one file per page) at a chosen DPI, into a chosen folder.
    async Task ExportPng()
    {
        int dpi = _doc.Export.Dpi <= 0 ? 200 : _doc.Export.Dpi;
        var dlg = new Window { Title = Loc.S("Pc_ExportTitle"), CanResize = false,
            SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Ui.ThemeWindow(dlg);
        var dpiField = new TextBox { Width = 70, Text = Num(dpi) }; ThemeInput(dpiField);
        // Format: PNG (per page) / TIFF (per page) / TIFF (multipage) / PDF.
        var fmtCombo = Ui.Combo(240);
        foreach (var f in new[] { Loc.S("Pc_FmtPngPages"), Loc.S("Pc_FmtTiffPages"), Loc.S("Pc_FmtTiffMulti"), Loc.S("Pc_FmtPdf") })
            fmtCombo.Items.Add(f);
        fmtCombo.SelectedIndex = Math.Clamp(_doc.Export.FormatIndex, 0, 3);
        bool ok = false;
        var okBtn = Ui.Btn(Loc.S("Common_Ok")); okBtn.Click += (_, _) => { ok = true; dlg.Close(); };
        var cancel = Ui.Btn(Loc.S("Common_Cancel")); cancel.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel { Margin = new(16), Spacing = 12, Children = {
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Lbl(Loc.S("Pc_ExportFormat")), fmtCombo } },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Lbl(Loc.S("Pc_ExportDpi")), Spinner(dpiField, 36, 1200, 50) } },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, okBtn } },
        } };
        await dlg.ShowDialog(this);
        if (!ok) return;
        dpi = (int)Math.Clamp(ParseNum(dpiField.Text, dpi), 36, 1200);
        int fmt = Math.Clamp(fmtCombo.SelectedIndex, 0, 3);
        _doc.Export.Dpi = dpi; _doc.Export.FormatIndex = fmt;

        var (ext, typeName) = fmt switch
        {
            1 or 2 => ("tif", "TIFF"),
            3      => ("pdf", "PDF"),
            _      => ("png", "PNG"),
        };
        // A Save dialog: the document name is only the SUGGESTED file name — the user types their own name/location.
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.S("Pc_ExportTitle"),
            SuggestedFileName = PrintDocumentService.Sanitize(_doc.Name) + "." + ext,
            DefaultExtension = ext,
            FileTypeChoices = new[] { new FilePickerFileType(typeName) { Patterns = new[] { "*." + ext } } },
        });
        var chosen = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(chosen)) return;

        var dir  = System.IO.Path.GetDirectoryName(chosen)!;
        var stem = System.IO.Path.GetFileNameWithoutExtension(chosen);
        int n = _doc.Pages.Count;
        string author = string.IsNullOrWhiteSpace(AppSettings.UserName) ? Environment.UserName : AppSettings.UserName;
        string copyright = string.IsNullOrWhiteSpace(author) ? "" : $"© {DateTime.Now:yyyy} {author}";
        var meta = new DocumentExporter.ExportMeta(author, copyright, _doc.Name, DateTime.Now);

        try
        {
            if (fmt == 3)   // PDF: raster background + selectable text layer, all pages in one file
            {
                var pages = new List<(byte[], IReadOnlyList<DocumentExporter.PdfTextRun>)>();
                for (int i = 0; i < n; i++)
                {
                    var texts = new List<DocumentExporter.PdfTextRun>();
                    var bmp = ExportPageBitmap(i, dpi, texts);
                    pages.Add((PngBytes(bmp), texts));
                    bmp.Dispose();
                }
                DocumentExporter.SavePdf(chosen, pages, dpi, meta);
            }
            else if (fmt == 2)   // multipage TIFF (all pages, one file)
            {
                var pages = new List<byte[]>();
                for (int i = 0; i < n; i++)
                {
                    var bmp = ExportPageBitmap(i, dpi);
                    pages.Add(PngBytes(bmp));
                    bmp.Dispose();
                }
                DocumentExporter.SaveTiffMultipage(chosen, pages, dpi, meta);
            }
            else                        // one file per page (PNG / TIFF)
            {
                for (int i = 0; i < n; i++)
                {
                    var bmp  = ExportPageBitmap(i, dpi);
                    var path = n == 1 ? chosen : System.IO.Path.Combine(dir, $"{stem}-{i + 1:00}.{ext}");
                    if (fmt == 1) DocumentExporter.SaveTiff(path, PngBytes(bmp), dpi, meta);
                    else { bmp.Save(path); SetPngMetadata(path, dpi); }
                    bmp.Dispose();
                }
            }
            Notify(string.Format(Loc.S("Pc_Exported"), n));
        }
        catch (Exception ex)
        {
            await MessageDialog.Show(this, $"Export failed:\n{ex.GetType().Name}: {ex.Message}", Loc.S("Pc_ExportTitle"));
        }
    }

    static byte[] PngBytes(RenderTargetBitmap bmp)
    {
        using var ms = new System.IO.MemoryStream();
        bmp.Save(ms);
        return ms.ToArray();
    }

    // Injects PNG metadata Avalonia's encoder omits: DPI (pHYs) plus tEXt fields — Author, Creation Time,
    // Software and Copyright — inserted after IHDR (before IDAT). Best-effort.
    static void SetPngMetadata(string path, int dpi)
    {
        try
        {
            var b = System.IO.File.ReadAllBytes(path);
            if (b.Length < 8 || b[0] != 0x89 || b[1] != 0x50) return;   // not a PNG

            string author = string.IsNullOrWhiteSpace(AppSettings.UserName) ? Environment.UserName : AppSettings.UserName;
            string copyright = string.IsNullOrWhiteSpace(author) ? "" : $"© {DateTime.Now:yyyy} {author}";
            var extra = new List<byte[]>();
            int ppm = (int)Math.Round(dpi / 0.0254);
            var phys = new byte[9]; WriteBE(phys, 0, ppm); WriteBE(phys, 4, ppm); phys[8] = 1;
            extra.Add(MakeChunk("pHYs", phys));
            // tEXt: read by some image tools.
            extra.Add(TextChunk("Software", "StructoFox / SkiaSharp"));
            extra.Add(TextChunk("Creation Time", DateTime.Now.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
            if (!string.IsNullOrWhiteSpace(author)) { extra.Add(TextChunk("Author", author)); extra.Add(TextChunk("Copyright", copyright)); }
            // XMP (iTXt): this is what Windows Explorer's Details tab reads for Author / Copyright / date / program.
            extra.Add(XmpChunk(author, copyright));

            var outp = new List<byte>(b.Length + 256);
            outp.AddRange(b[..8]);
            int pos = 8; bool inserted = false;
            while (pos + 8 <= b.Length)
            {
                int len = ReadBE(b, pos);
                string type = System.Text.Encoding.ASCII.GetString(b, pos + 4, 4);
                int total = 12 + len;
                if (pos + total > b.Length) break;
                if (type is "pHYs" or "tEXt" or "iTXt") { pos += total; continue; }   // drop existing ones we set
                outp.AddRange(b[pos..(pos + total)]);
                if (type == "IHDR" && !inserted) { foreach (var c in extra) outp.AddRange(c); inserted = true; }
                pos += total;
            }
            System.IO.File.WriteAllBytes(path, outp.ToArray());
        }
        catch { /* metadata is best-effort */ }
    }

    // An XMP packet in a PNG iTXt chunk (keyword "XML:com.adobe.xmp") — Windows Explorer reads this for the
    // Author / Copyright / creation date / creator tool shown under file Properties → Details.
    static byte[] XmpChunk(string author, string copyright)
    {
        static string Esc(string s) => System.Security.SecurityElement.Escape(s ?? "") ?? "";
        string date = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
        // MicrosoftPhoto:DateAcquired fills Windows' "Date acquired" (Erfassungsdatum) — the right field for "when
        // saved", NOT xmp:CreateDate (which is the photo "Date taken"). The file's own "Date created" is set by the OS.
        string xmp =
            "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
            "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:MicrosoftPhoto=\"http://ns.microsoft.com/photo/1.0/\">" +
            "<xmp:CreatorTool>StructoFox / SkiaSharp</xmp:CreatorTool>" +
            $"<MicrosoftPhoto:DateAcquired>{date}</MicrosoftPhoto:DateAcquired>" +
            (string.IsNullOrWhiteSpace(author) ? "" : $"<dc:creator><rdf:Seq><rdf:li>{Esc(author)}</rdf:li></rdf:Seq></dc:creator>") +
            (string.IsNullOrWhiteSpace(copyright) ? "" : $"<dc:rights><rdf:Alt><rdf:li xml:lang=\"x-default\">{Esc(copyright)}</rdf:li></rdf:Alt></dc:rights>") +
            "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

        var lat1 = System.Text.Encoding.Latin1;
        var kw = lat1.GetBytes("XML:com.adobe.xmp");
        var body = System.Text.Encoding.UTF8.GetBytes(xmp);
        // keyword \0 compressionFlag(0) compressionMethod(0) langTag \0 translatedKeyword \0 text
        var data = new byte[kw.Length + 1 + 1 + 1 + 0 + 1 + 0 + 1 + body.Length];
        int i = 0;
        kw.CopyTo(data, i); i += kw.Length; data[i++] = 0; data[i++] = 0; data[i++] = 0; data[i++] = 0; data[i++] = 0;
        body.CopyTo(data, i);
        return MakeChunk("iTXt", data);
    }

    // A PNG tEXt chunk: Latin-1 keyword + NUL + Latin-1 text.
    static byte[] TextChunk(string keyword, string text)
    {
        var lat1 = System.Text.Encoding.Latin1;
        var kw = lat1.GetBytes(keyword); var tx = lat1.GetBytes(text);
        var data = new byte[kw.Length + 1 + tx.Length];
        kw.CopyTo(data, 0); data[kw.Length] = 0; tx.CopyTo(data, kw.Length + 1);
        return MakeChunk("tEXt", data);
    }

    static void WriteBE(byte[] a, int o, int v) { a[o] = (byte)(v >> 24); a[o + 1] = (byte)(v >> 16); a[o + 2] = (byte)(v >> 8); a[o + 3] = (byte)v; }
    static int ReadBE(byte[] a, int o) => (a[o] << 24) | (a[o + 1] << 16) | (a[o + 2] << 8) | a[o + 3];
    static byte[] MakeChunk(string type, byte[] data)
    {
        var buf = new byte[12 + data.Length];
        WriteBE(buf, 0, data.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(buf, 4);
        data.CopyTo(buf, 8);
        WriteBE(buf, 8 + data.Length, (int)Crc32(buf, 4, 4 + data.Length));
        return buf;
    }
    static uint Crc32(byte[] a, int off, int len)
    {
        uint c = 0xFFFFFFFF;
        for (int i = off; i < off + len; i++)
        {
            c ^= a[i];
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFF;
    }

    // Start a fresh, empty print document.
    void NewDoc()
    {
        _doc = new PrintDocument(); _savedName = null;
        _pageIndex = 0; _selected.Clear(); _diagramCache.Clear();
        Content = Build(); RenderPage(); UpdateTitle();
    }

    // Rename the document (the name shown in the Load list).
    async Task RenameDoc()
    {
        var n = await PromptDialog.Show(this, Loc.S("Pc_Name"), _doc.Name, Loc.S("Pc_Rename"));
        if (!string.IsNullOrWhiteSpace(n)) { _doc.Name = n.Trim(); UpdateTitle(); }
    }

    // Paper size + orientation for the current page (applied live).
    void OpenPaperDialog()
    {
        var dlg = new Window { Title = Loc.S("Pc_PaperFormat"), CanResize = false,
            SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Ui.ThemeWindow(dlg);

        var paper = new ComboBox { MinWidth = 150 }; ThemeInput(paper);
        foreach (var p in Enum.GetValues<PaperSize>()) paper.Items.Add(p.ToString());
        paper.SelectedIndex = (int)Page.Paper;
        paper.SelectionChanged += (_, _) => { if (paper.SelectedIndex >= 0) { Page.Paper = (PaperSize)paper.SelectedIndex; RenderPage(); } };

        var orient = new ComboBox { MinWidth = 150 }; ThemeInput(orient);
        orient.Items.Add(Loc.S("Pc_Portrait")); orient.Items.Add(Loc.S("Pc_Landscape"));
        orient.SelectedIndex = (int)Page.Orientation;
        orient.SelectionChanged += (_, _) => { if (orient.SelectedIndex >= 0) { Page.Orientation = (PrintOrientation)orient.SelectedIndex; RenderPage(); } };

        // Margins in cm (stored in mm). Live-applied.
        TextBox MarginField(Func<double> get, Action<double> set)
        {
            var f = new TextBox { Width = 56, Text = Num(get() / 10.0) }; ThemeInput(f);
            void Commit() { set(Math.Clamp(ParseNum(f.Text, get() / 10.0) * 10.0, 0, 100)); f.Text = Num(get() / 10.0); RenderPage(); }
            f.LostFocus += (_, _) => Commit();
            f.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
            f.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty && !f.IsFocused) Commit(); };
            return f;
        }
        var mTop = MarginField(() => Page.MarginTop, v => Page.MarginTop = v);
        var mBot = MarginField(() => Page.MarginBottom, v => Page.MarginBottom = v);
        var mLeft = MarginField(() => Page.MarginLeft, v => Page.MarginLeft = v);
        var mRight = MarginField(() => Page.MarginRight, v => Page.MarginRight = v);

        var ok = Ui.Btn(Loc.S("Common_Ok")); ok.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel { Margin = new(16), Spacing = 10, Children = {
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Lbl(Loc.S("Pc_Paper")), paper } },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Lbl(Loc.S("Pc_Orientation")), orient } },
            Lbl(Loc.S("Pc_Margins")),
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = {
                Lbl(Loc.S("Pc_MTop")), mTop, Lbl(Loc.S("Pc_MBottom")), mBot } },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = {
                Lbl(Loc.S("Pc_MLeft")), mLeft, Lbl(Loc.S("Pc_MRight")), mRight } },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok } },
        } };
        dlg.ShowDialog(this);
    }

    // Grid settings as a dialog (opened from the Options menu).
    void OpenGridDialog()
    {
        var dlg = new Window { Title = Loc.S("Pc_GridSettings"), CanResize = false,
            SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Ui.ThemeWindow(dlg);
        var ok = Ui.Btn(Loc.S("Common_Ok")); ok.Click += (_, _) => dlg.Close();
        var panel = BuildGridPanel();
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok } });
        dlg.Content = panel;
        dlg.ShowDialog(this);
    }

    // Pick one of the project's saved print documents — load or delete it.
    async Task OpenLoadDialog()
    {
        var docs = PrintDocumentService.List(_projFolder);
        var dlg = new Window { Title = Loc.S("Pc_LoadTitle"), Width = 380, Height = 440, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Ui.ThemeWindow(dlg);

        var listBox = new ListBox(); ThemeInput(listBox);
        var empty = new TextBlock { Text = Loc.S("Pc_NoSaved"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Ui.Theme(empty, TextBlock.ForegroundProperty, "SidebarTextBrush");
        void Refresh() { listBox.ItemsSource = docs.Select(d => d.Name).ToList(); listBox.IsVisible = docs.Count > 0; empty.IsVisible = docs.Count == 0; }
        Refresh();

        void Load() { if (listBox.SelectedIndex >= 0) { _doc = docs[listBox.SelectedIndex]; _savedName = _doc.Name; _pageIndex = 0; _selected.Clear(); _diagramCache.Clear(); Content = Build(); RenderPage(); UpdateTitle(); dlg.Close(); } }
        listBox.DoubleTapped += (_, _) => Load();

        // Compact icon buttons with localized tooltips (the dialog is narrow).
        static Button IconBtn(string icon, string tip) { var b = Ui.Btn(icon); b.MinWidth = 0; b.Padding = new(10, 6); ToolTip.SetTip(b, tip); return b; }

        var load = IconBtn("✔", Loc.S("Pc_Load")); load.Click += (_, _) => Load();
        var del  = IconBtn("🗑", Loc.S("Pc_Delete")); del.Click += async (_, _) =>
        {
            int i = listBox.SelectedIndex; if (i < 0) return;
            var name = docs[i].Name;
            if (await MessageDialog.Show(dlg, string.Format(Loc.S("Pc_DeleteConfirm"), name), Loc.S("Pc_Delete"), DialogButtons.YesNo) != DialogResult.Yes) return;
            PrintDocumentService.Delete(_projFolder, name);
            docs.RemoveAt(i); Refresh();
        };
        var cancel = IconBtn("✕", Loc.S("Common_Cancel")); cancel.Click += (_, _) => dlg.Close();
        var openDir = IconBtn("📁", Loc.S("Pc_OpenFolder")); openDir.Click += (_, _) =>
        {
            var folder = PrintDocumentService.Folder(_projFolder);
            try { System.IO.Directory.CreateDirectory(folder); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = folder, UseShellExecute = true }); } catch { }
        };

        var grid = new Grid { Margin = new(14), RowDefinitions = new("*,Auto") };
        var listArea = new Panel { Children = { listBox, empty } };
        Grid.SetRow(listArea, 0); grid.Children.Add(listArea);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new(0, 10, 0, 0), Children = { openDir, del, cancel, load } };
        Grid.SetRow(btns, 1); grid.Children.Add(btns);
        dlg.Content = grid;
        await dlg.ShowDialog(this);
    }

    // A theme-coloured label (so it never renders white on the flyout/dialog).
    static TextBlock Lbl(string text)
    {
        var tb = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        Ui.Theme(tb, TextBlock.ForegroundProperty, "SidebarTextBrush");
        return tb;
    }

    void RedrawGridIfShown() { if (_doc.GridVisible) RenderPage(); }

    // Grid settings panel: show / snap, spacing (mm or inch), colour, opacity, line width and line style.
    StackPanel BuildGridPanel()
    {
        var show = new CheckBox { Content = Loc.S("Pc_GridShow"), IsChecked = _doc.GridVisible };
        show.IsCheckedChanged += (_, _) => { _doc.GridVisible = show.IsChecked == true; RenderPage(); };
        var snap = new CheckBox { Content = Loc.S("Pc_GridSnap"), IsChecked = _doc.GridSnap };
        snap.IsCheckedChanged += (_, _) => { _doc.GridSnap = snap.IsChecked == true; };
        foreach (var cb in new[] { show, snap }) Ui.Theme(cb, CheckBox.ForegroundProperty, "SidebarTextBrush");

        // Spacing + unit.
        var unit = new ComboBox { MinWidth = 74 }; ThemeInput(unit);
        unit.Items.Add("mm"); unit.Items.Add("inch");
        unit.SelectedIndex = _doc.GridUnit == "in" ? 1 : 0;
        var sizeField = new TextBox { Width = 66 }; ThemeInput(sizeField);
        void RefreshSize() => sizeField.Text = Num(unit.SelectedIndex == 1 ? _doc.GridMm / 25.4 : _doc.GridMm);
        RefreshSize();
        void CommitSize()
        {
            double val = ParseNum(sizeField.Text, unit.SelectedIndex == 1 ? _doc.GridMm / 25.4 : _doc.GridMm);
            _doc.GridMm = Math.Clamp(unit.SelectedIndex == 1 ? val * 25.4 : val, 0.5, 100);
            _doc.GridUnit = unit.SelectedIndex == 1 ? "in" : "mm"; RefreshSize(); RedrawGridIfShown();
        }
        sizeField.LostFocus += (_, _) => CommitSize();
        sizeField.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitSize(); };
        sizeField.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty && !sizeField.IsFocused) CommitSize(); };
        unit.SelectionChanged += (_, _) => { _doc.GridUnit = unit.SelectedIndex == 1 ? "in" : "mm"; RefreshSize(); };

        // Colour (with alpha via the picker's A channel — no "inherit", the grid has no parent colour to inherit).
        Color startC;
        try { startC = Color.Parse(_doc.GridColor); } catch { startC = Colors.Black; }
        startC = Color.FromArgb((byte)Math.Clamp(_doc.GridOpacity * 255, 0, 255), startC.R, startC.G, startC.B);
        var colour = new HexColorPicker(showPalette: true) { Color = startC };
        colour.ColorChanged += (_, _) =>
        {
            var c = colour.Color;
            _doc.GridColor = HexColorPicker.HexOf(c);      // RGB
            _doc.GridOpacity = c.A / 255.0;                // alpha from the A channel
            RedrawGridIfShown();
        };

        // Line width.
        var width = new TextBox { Width = 66, Text = Num(_doc.GridThickness) }; ThemeInput(width);
        void CommitWidth() { _doc.GridThickness = Math.Clamp(ParseNum(width.Text, 1), 0.25, 10); width.Text = Num(_doc.GridThickness); RedrawGridIfShown(); }
        width.LostFocus += (_, _) => CommitWidth();
        width.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitWidth(); };
        width.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty && !width.IsFocused) CommitWidth(); };

        // Line style.
        var styleCombo = new ComboBox { MinWidth = 150 }; ThemeInput(styleCombo);
        foreach (var s in new[] { "Pc_StyleSolid", "Pc_StyleDotted", "Pc_StyleDashed", "Pc_StyleCrosses", "Pc_StyleCrossesDots", "Pc_StyleCrossesDashes" })
            styleCombo.Items.Add(Loc.S(s));
        styleCombo.SelectedIndex = (int)_doc.GridStyle;
        styleCombo.SelectionChanged += (_, _) => { if (styleCombo.SelectedIndex >= 0) { _doc.GridStyle = (PrintGridStyle)styleCombo.SelectedIndex; RedrawGridIfShown(); } };

        var panel = new StackPanel { Margin = new(12), Spacing = 10, Children = {
            show, snap,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Lbl(Loc.S("Pc_GridSpacing")), Spinner(sizeField, 0.5, 100, 1), unit } },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Lbl(Loc.S("Pc_GridWidth")), Spinner(width, 0.25, 10, 0.25) } },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Lbl(Loc.S("Pc_GridStyle")), styleCombo } },
            Lbl(Loc.S("Pc_GridColor")), colour,
        } };
        return panel;
    }

    // Draws the printable-area guide (dashed rectangle at the page margins) — the export clips content to this.
    void DrawMarginGuide()
    {
        if (_pageCanvas is null) return;
        var pr = Page.PrintablePx;
        var rect = new Avalonia.Controls.Shapes.Rectangle
        {
            Width = pr.W * _viewScale, Height = pr.H * _viewScale,
            Stroke = new SolidColorBrush(Color.FromArgb(90, 0x2F, 0x80, 0xED)), StrokeThickness = 1,
            StrokeDashArray = new(4, 4), Fill = null, IsHitTestVisible = false,
        };
        Canvas.SetLeft(rect, pr.X * _viewScale); Canvas.SetTop(rect, pr.Y * _viewScale);
        _pageCanvas.Children.Add(rect);
    }

    // Grid spacing in device-independent page px (@96); mm → px96.
    double GridPx96 => _doc.GridMm * (96.0 / 25.4);
    // Snaps a page coordinate to the nearest grid line when snapping is on.
    double Snap(double v) => _doc.GridSnap && GridPx96 > 0.5 ? Math.Round(v / GridPx96) * GridPx96 : v;

    // Draws the layout grid behind the items using the document's colour / opacity / thickness / style. Built as a
    // couple of Path geometries (one control each) so even a dense grid stays cheap. Skipped when it would be
    // denser than 4 px on screen.
    void DrawGrid(double pw, double ph)
    {
        if (_pageCanvas is null) return;
        double sp = GridPx96 * _viewScale;
        if (sp < 4) return;

        var baseC = ParseColorOr(_doc.GridColor, Colors.Black);
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(_doc.GridOpacity * 255, 0, 255), baseC.R, baseC.G, baseC.B));
        double th = Math.Max(0.25, _doc.GridThickness);

        if (_doc.GridStyle is PrintGridStyle.Solid or PrintGridStyle.Dotted or PrintGridStyle.Dashed)
        {
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                for (double x = 0; x <= pw + 0.5; x += sp) { c.BeginFigure(new(x, 0), false); c.LineTo(new(x, ph)); c.EndFigure(false); }
                for (double y = 0; y <= ph + 0.5; y += sp) { c.BeginFigure(new(0, y), false); c.LineTo(new(pw, y)); c.EndFigure(false); }
            }
            var path = new Avalonia.Controls.Shapes.Path { Data = geo, Stroke = brush, StrokeThickness = th, IsHitTestVisible = false };
            if (_doc.GridStyle == PrintGridStyle.Dotted) { path.StrokeDashArray = new(0.1, 2.5); path.StrokeLineCap = PenLineCap.Round; }
            else if (_doc.GridStyle == PrintGridStyle.Dashed) path.StrokeDashArray = new(4, 3);
            _pageCanvas.Children.Add(path);
            return;
        }

        // Crosses (and optional dots): short cross segments at each intersection.
        double arm = Math.Min(sp * 0.16, 4);
        var cross = new StreamGeometry();
        using (var c = cross.Open())
            for (double x = 0; x <= pw + 0.5; x += sp)
                for (double y = 0; y <= ph + 0.5; y += sp)
                {
                    c.BeginFigure(new(x - arm, y), false); c.LineTo(new(x + arm, y)); c.EndFigure(false);
                    c.BeginFigure(new(x, y - arm), false); c.LineTo(new(x, y + arm)); c.EndFigure(false);
                }
        _pageCanvas.Children.Add(new Avalonia.Controls.Shapes.Path { Data = cross, Stroke = brush, StrokeThickness = th, IsHitTestVisible = false });

        if (_doc.GridStyle is PrintGridStyle.CrossesDots or PrintGridStyle.CrossesDashes)
        {
            // Marks spaced ALONG each grid line between the crosses (never at a cross itself). Dashes use a coarser
            // spacing and leave a gap next to each cross, so the pattern reads as "cross … dashes … cross" rather
            // than one continuous dashed line.
            bool dots = _doc.GridStyle == PrintGridStyle.CrossesDots;
            int subN = dots ? Math.Max(2, (int)Math.Round(sp / 8)) : Math.Max(3, (int)Math.Round(sp / 12));
            double sub = sp / subN;
            double hl = sub * 0.28;                              // half-length of a dash
            // Skip the sub-position right next to a cross (a clear gap around each cross), when there's room.
            bool nextToCross(int k) => !dots && subN >= 4 && (k % subN == 1 || k % subN == subN - 1);

            var marks = new StreamGeometry();
            using (var c = marks.Open())
            {
                // Along horizontal lines (y on the grid, x stepping in sub-divisions, skipping the crosses).
                for (double y = 0; y <= ph + 0.5; y += sp)
                    for (int k = 1; k * sub <= pw + 0.5; k++)
                    {
                        if (k % subN == 0 || nextToCross(k)) continue;
                        double x = k * sub;
                        if (dots) { c.BeginFigure(new(x, y), false); c.LineTo(new(x, y)); }
                        else      { c.BeginFigure(new(x - hl, y), false); c.LineTo(new(x + hl, y)); }
                        c.EndFigure(false);
                    }
                // Along vertical lines.
                for (double x = 0; x <= pw + 0.5; x += sp)
                    for (int k = 1; k * sub <= ph + 0.5; k++)
                    {
                        if (k % subN == 0 || nextToCross(k)) continue;
                        double y = k * sub;
                        if (dots) { c.BeginFigure(new(x, y), false); c.LineTo(new(x, y)); }
                        else      { c.BeginFigure(new(x, y - hl), false); c.LineTo(new(x, y + hl)); }
                        c.EndFigure(false);
                    }
            }
            var markPath = new Avalonia.Controls.Shapes.Path { Data = marks, Stroke = brush, IsHitTestVisible = false };
            if (dots) { markPath.StrokeThickness = Math.Max(1.5, th * 1.6); markPath.StrokeLineCap = PenLineCap.Round; }
            else markPath.StrokeThickness = th;
            _pageCanvas.Children.Add(markPath);
        }
    }

    static Color ParseColorOr(string? hex, Color fallback)
    { try { return string.IsNullOrWhiteSpace(hex) ? fallback : Color.Parse(hex); } catch { return fallback; } }

    // ── Page rendering ─────────────────────────────────────────────────────
    void RenderPage()
    {
        if (_pageCanvas is null || _pageBorder is null) return;
        var (w, h) = Page.SizePx;
        _pageBorder.Width = w * _viewScale;
        _pageBorder.Height = h * _viewScale;
        _pageCanvas.Width = w * _viewScale;    // explicit size so ClipToBounds clips to the page (a Canvas is 0×0 otherwise)
        _pageCanvas.Height = h * _viewScale;
        _pageCanvas.Background = new SolidColorBrush(Color.Parse(Page.Background));

        _pageCanvas.Children.Clear();
        _visuals.Clear();
        if (_doc.GridVisible) DrawGrid(w * _viewScale, h * _viewScale);
        DrawMarginGuide();
        foreach (var item in Page.Items.OrderBy(i => i.ZOrder))
        {
            var raw = BuildItemVisual(item);
            if (raw is null) continue;
            var chrome = WrapChrome(item, raw);
            Canvas.SetLeft(chrome, item.X * _viewScale);
            Canvas.SetTop(chrome, item.Y * _viewScale);
            _pageCanvas.Children.Add(chrome);
            _visuals[item.Id] = chrome;
        }

        if (_pageLabel is not null)
        {
            var o = Page.Orientation == PrintOrientation.Landscape ? Loc.S("Pc_Landscape") : Loc.S("Pc_Portrait");
            _pageLabel.Text = $"Page {_pageIndex + 1} / {_doc.Pages.Count}  ·  {Page.Paper} {o}";
        }
    }

    // Builds the RAW on-screen visual for an item (no chrome). WrapChrome adds selection outline, scale handles,
    // drag and the context menu around it.
    Control? BuildItemVisual(PrintItem item) => item switch
    {
        LabelItem lbl       => BuildLabelVisual(lbl),
        TextItem  txt       => BuildTextVisual(txt),
        DiagramItem di      => BuildImageVisual(di),
        DiagramDecorItem dc => BuildDecorVisual(dc),
        HeaderItem hd       => BuildHeaderVisual(hd),
        _ => null,
    };

    // One slot of a print header (the decoration at hd.Pos), built from a DiagramStyle exactly like a diagram
    // header — same templates. A header is inserted as one item per slot, so it can be decomposed. Live control
    // (crisp text), scaled via a layout transform.
    Control? BuildHeaderVisual(HeaderItem hd)
    {
        var style = hd.Style;
        if (hd.Style.ShowPageNumber)   // inject the current page's number into a clone (never persisted)
        {
            style = CloneStyle(hd.Style);
            style.InfoPage = $"{_pageIndex + 1} / {_doc.Pages.Count}";
        }
        var piece = DiagramDecor.EnumeratePieces(hd.Title, style).FirstOrDefault(p => p.Pos == hd.Pos).Ctrl;
        Control inner = piece ?? new Border { Padding = new(8), Child = new TextBlock { Text = "(empty — double-click to edit)" } };
        // Never let the header run past the printable right edge from where it sits — long info fields wrap instead
        // of being clipped. hd.Scale enlarges the rendered block, so the inner max width shrinks by that factor.
        var pr = Page.PrintablePx;
        double hardMax = Math.Max(60, (pr.X + pr.W - hd.X) / Math.Max(0.1, hd.Scale));
        // A set width FILLS that width (the Star info columns stretch to fill the page); unset = natural, capped at
        // the printable edge. Either way it never runs past the right margin.
        if (hd.MaxWidth > 1) inner.Width = Math.Min(hd.MaxWidth, hardMax);
        else                 inner.MaxWidth = hardMax;
        double s = hd.Scale * _viewScale;
        return new LayoutTransformControl { LayoutTransform = new ScaleTransform(s, s), Child = inner, ClipToBounds = false };
    }

    static DiagramStyle CloneStyle(DiagramStyle s)
        => System.Text.Json.JsonSerializer.Deserialize<DiagramStyle>(System.Text.Json.JsonSerializer.Serialize(s)) ?? new();

    // A single-line caption inside an optional filled/bordered box.
    Control BuildLabelVisual(LabelItem lbl)
    {
        var tb = new TextBlock
        {
            Text = string.IsNullOrEmpty(lbl.Text) ? " " : lbl.Text,
            FontFamily = string.IsNullOrEmpty(lbl.FontFamily) ? FontFamily.Default : new FontFamily(lbl.FontFamily),
            FontSize = lbl.FontSize * _viewScale,
            FontWeight = lbl.Bold ? FontWeight.Bold : FontWeight.Normal,
            FontStyle = lbl.Italic ? FontStyle.Italic : FontStyle.Normal,
            Foreground = ParseBrush(lbl.Color, Brushes.Black),
            TextDecorations = Decorations(lbl.Underline, lbl.Strike),
        };
        return BoxAround(tb, lbl.Background, lbl.BorderColor, lbl.BorderThickness, lbl.Padding);
    }

    // A multi-line free-text box (wrap width) inside an optional filled/bordered box. With a list style, each line
    // becomes a marker | text row so wrapped text keeps a hanging indent under the text column; lines the user
    // indents (leading space/tab) are continuations — no marker, aligned under the text.
    Control BuildTextVisual(TextItem txt)
    {
        EnsureRunsMigrated(txt);
        double scale = _viewScale;
        double boxW  = txt.Width * scale;
        var align = txt.Align == "Center" ? TextAlignment.Center : txt.Align == "Right" ? TextAlignment.Right : TextAlignment.Left;
        var fam   = string.IsNullOrEmpty(txt.FontFamily) ? FontFamily.Default : new FontFamily(txt.FontFamily);
        var defFg = ParseBrush(txt.Color, Brushes.Black);
        double baseFs = txt.FontSize;

        // A line's height follows its LARGEST font (so an enlarged word pushes the next line down), scaled by the
        // spacing factor and floored at the glyph height so lines can tighten but never overlap.
        double LineHOf(double fs) => Math.Max(fs * scale * 1.3 * txt.LineSpacing, fs * scale * 1.18);
        double MaxFsIn(int a, int b)
        {
            double mx = baseFs; int pos = 0;
            foreach (var r in txt.Runs) { int s = Math.Max(a, pos), e = Math.Min(b, pos + r.Text.Length); if (e > s && r.Size is { } sz && sz > mx) mx = sz; pos += r.Text.Length; }
            return mx;
        }

        // A plain TextBlock (list markers aren't per-run formatted).
        TextBlock Styled(string s, double lineH) { var tb = new TextBlock { Text = s, FontFamily = fam, FontSize = baseFs * scale, Foreground = defFg, LineHeight = lineH }; return tb; }
        // A TextBlock built from the run-slice covering one line body's character range [a,b).
        TextBlock Body(int a, int b, double width, double lineH)
        {
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = fam, FontSize = baseFs * scale,
                                     Foreground = defFg, Width = width, TextAlignment = align, LineHeight = lineH };
            foreach (var inl in RichText.ToInlines(RichText.Slice(txt.Runs, a, b), baseFs, scale, defFg)) tb.Inlines!.Add(inl);
            return tb;
        }

        double para   = baseFs * scale * 0.55;   // blank line = paragraph gap
        double gap     = baseFs * scale * 0.45;   // marker → text gap
        double perCol  = baseFs * scale * 0.9;    // indent per leading space
        var plain = RichText.Plain(txt.Runs).Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = plain.Split('\n');

        var panel = new StackPanel { Width = boxW };
        int lineStart = 0;
        foreach (var l in lines)
        {
            if (string.IsNullOrWhiteSpace(l)) { panel.Children.Add(new Border { Height = para }); lineStart += l.Length + 1; continue; }
            int cols = 0, i = 0;
            for (; i < l.Length; i++) { if (l[i] == ' ') cols++; else if (l[i] == '\t') cols += 4; else break; }
            var mark = DetectMarker(l[i..]);
            string bodyStr = mark?.rest ?? l[i..];     // a suffix of l (indent/marker trimmed from the front)
            int bodyStart = lineStart + (l.Length - bodyStr.Length);
            int bodyEnd   = lineStart + l.Length;
            double indent = cols * perCol;
            double avail  = Math.Max(20, boxW - indent);
            double lineH  = LineHOf(MaxFsIn(bodyStart, bodyEnd));   // height driven by this line's biggest font
            Control unit;
            if (mark is null) unit = Body(bodyStart, bodyEnd, avail, lineH);
            else
            {
                // A wrapping TextBlock in a Grid '*' column measures against infinity and won't wrap (it gets clipped
                // instead), so measure the marker and give the body an EXPLICIT remaining width so it wraps properly.
                var mk = Styled(mark.Value.marker, lineH); mk.VerticalAlignment = VerticalAlignment.Top; mk.Margin = new(0, 0, gap, 0);
                mk.Measure(Avalonia.Size.Infinity);
                double bodyW = Math.Max(20, avail - mk.DesiredSize.Width - gap);
                var bd = Body(bodyStart, bodyEnd, bodyW, lineH);
                unit = new StackPanel { Orientation = Orientation.Horizontal, Width = avail, Children = { mk, bd } };
            }
            unit.HorizontalAlignment = HorizontalAlignment.Left;
            unit.Margin = new(indent, 0, 0, 0);
            panel.Children.Add(unit);
            lineStart += l.Length + 1;
        }
        Control content = string.IsNullOrEmpty(plain) ? new TextBlock { Text = " ", Width = boxW } : panel;
        return BoxAround(content, txt.Background, txt.BorderColor, txt.BorderThickness, txt.Padding);
    }

    // Backward-compat: old text boxes carried box-level Bold/Italic/Underline/Strike with a single plain run. Fold
    // those into the runs (once) so the run model is authoritative, then clear the box-level flags.
    static void EnsureRunsMigrated(TextItem t)
    {
        if (t.Runs.Count == 0) t.Runs.Add(new TextRun { Text = "" });
        bool runFmt = t.Runs.Any(r => r.Bold || r.Italic || r.Underline || r.Strike || r.Fg != null || r.Marker != null || r.Size != null || r.Super || r.Sub);
        if (!runFmt && (t.Bold || t.Italic || t.Underline || t.Strike))
        {
            foreach (var r in t.Runs) { r.Bold = t.Bold; r.Italic = t.Italic; r.Underline = t.Underline; r.Strike = t.Strike; }
            t.Bold = t.Italic = t.Underline = t.Strike = false;
        }
    }

    // Wraps text content in a Border carrying the optional fill / border / padding (all scaled to the view).
    Border BoxAround(Control content, string? bg, string? border, double thickness, double padding)
    {
        var b = new Border { Child = content, Padding = new(padding * _viewScale) };
        if (!string.IsNullOrWhiteSpace(bg)) b.Background = ParseBrush(bg, Brushes.Transparent);
        if (!string.IsNullOrWhiteSpace(border) && thickness > 0)
        { b.BorderBrush = ParseBrush(border, Brushes.Black); b.BorderThickness = new(thickness * _viewScale); }
        return b;
    }

    // Recognises a list marker the user typed at the START of a line: a bullet (- – • *) or an enumerator
    // (digits or a single letter followed by '.' or ')'), each followed by whitespace. Returns (marker, rest) or
    // null if the line isn't a list item. Leading whitespace = not a marker (it's a plain/continuation line).
    static (string marker, string rest)? DetectMarker(string line)
    {
        if (string.IsNullOrEmpty(line) || char.IsWhiteSpace(line[0])) return null;

        // Bullet: one of - – • * then a space. '*' shows as • (the typeable bullet), '-' shows as –.
        if (line.Length >= 2 && line[0] is '-' or '–' or '•' or '*' && char.IsWhiteSpace(line[1]))
        {
            string glyph = line[0] switch { '*' => "•", '-' => "–", _ => line[0].ToString() };
            return (glyph, line[2..].TrimStart());
        }

        // Enumerator: digits, or a single letter, then '.' or ')', then a space.
        int j = 0;
        while (j < line.Length && char.IsDigit(line[j])) j++;
        if (j == 0 && line.Length > 0 && char.IsLetter(line[0])) j = 1;   // single-letter enumerator (a. / b))
        if (j > 0 && j < line.Length && line[j] is '.' or ')' && j + 1 < line.Length && char.IsWhiteSpace(line[j + 1]))
            return (line[..(j + 1)], line[(j + 2)..].TrimStart());

        return null;
    }

    static IBrush ParseBrush(string? hex, IBrush fallback)
    { try { return string.IsNullOrWhiteSpace(hex) ? fallback : new SolidColorBrush(Color.Parse(hex)); } catch { return fallback; } }

    // The PAP bitmap. On screen: rendered NATIVE (scale 1 → tight, unambiguous crop), cached, displayed at
    // PixelSize × di.Scale × zoom. On export: rendered at the target device density (di.Scale × exportScale) so
    // it's pixel-crisp, displayed 1:1 with its pixels. Both use PixelSize (bmp.Size reports pixels, not DIP, at
    // scale ≠ 1 — using it double-applied the scale and blew diagrams up).
    Control? BuildImageVisual(DiagramItem di)
    {
        // Structograms are a pure control tree → place as a LIVE control scaled by a layout transform, so text/lines
        // stay crisp at any zoom AND in export (no bitmap upscaling). Scale = di.Scale × zoom (zoom = density on export).
        if (di.Kind == DiagramKind.Structogram && DiagramRenderer.BuildControl(_projFolder, di.Kind, di.Key) is { } body)
        {
            var s = di.Scale * _viewScale;
            return new LayoutTransformControl { LayoutTransform = new ScaleTransform(s, s), Child = body, ClipToBounds = false };
        }
        if (_exporting)
        {
            // EXPORT: render the diagram at the FINAL pixel size (di.Scale × density) so it's genuinely CRISP, then
            // show it 1:1 (Width = PixelSize). The crop is DPI-neutral (96 DPI = the export render's DPI), so there
            // is no DPI multiplication — same footprint fraction as the screen, just real pixels instead of stretch.
            var ex = DiagramRenderer.Render(_projFolder, di.Kind, di.Key, di.Scale * _viewScale);
            if (ex is null) return RenderPlaceholder();
            return new Image { Source = ex, Width = ex.PixelSize.Width, Height = ex.PixelSize.Height, Stretch = Stretch.Fill };
        }
        // SCREEN (unchanged, confirmed good): native render (scale 1), display at PixelSize × di.Scale × zoom.
        var bmp = GetCachedBitmap(di, () => DiagramRenderer.Render(_projFolder, di.Kind, di.Key, 1.0));
        if (bmp is null) return RenderPlaceholder();
        double disp = di.Scale * _viewScale;
        return new Image { Source = bmp, Width = bmp.PixelSize.Width * disp, Height = bmp.PixelSize.Height * disp, Stretch = Stretch.Fill };
    }

    static Control RenderPlaceholder() => new Border
    {
        Width = 140, Height = 60, BorderBrush = Brushes.OrangeRed, BorderThickness = new(1),
        Child = new TextBlock { Text = Loc.S("Pc_NothingToRender"), Margin = new(8), Foreground = Brushes.OrangeRed },
    };

    // A decoration block as a LIVE control (text stays crisp at any scale), scaled steplessly via a layout transform.
    Control? BuildDecorVisual(DiagramDecorItem dc)
    {
        var inner = DiagramRenderer.DecorControl(_projFolder, dc.Kind, dc.Key, dc.Pos);
        if (inner is null) return null;
        var s = dc.Scale * _viewScale;
        return new LayoutTransformControl { LayoutTransform = new ScaleTransform(s, s), Child = inner, ClipToBounds = false };
    }

    // The context menu for an item: resize, (diagrams) update/edit source, delete.
    List<MenuItem> ItemMenu(PrintItem item)
    {
        var list = new List<MenuItem>();
        if (item is LabelItem or TextItem)
        {
            var editTxt = new MenuItem { Header = Loc.S("Pc_Edit") }; editTxt.Click += async (_, _) => await EditItem(item);
            list.Add(editTxt);
        }
        else
        {
            var bigger  = new MenuItem { Header = Loc.S("Pc_Bigger") };  bigger.Click  += (_, _) => Rescale(item, 1.25);
            var smaller = new MenuItem { Header = Loc.S("Pc_Smaller") }; smaller.Click += (_, _) => Rescale(item, 0.8);
            var scaleTo = new MenuItem { Header = Loc.S("Pc_ScaleDots") };  scaleTo.Click += async (_, _) => await ScaleDialog(item);
            var reset   = new MenuItem { Header = Loc.S("Pc_ResetSize") }; reset.Click += (_, _) => SetScale(item, 1.0);
            list.Add(bigger); list.Add(smaller); list.Add(scaleTo); list.Add(reset);
        }
        if (item is DiagramItem di)
        {
            var update = new MenuItem { Header = Loc.S("Pc_Update") }; update.Click += (_, _) => { _diagramCache.Remove(item.Id); RenderPage(); };
            var edit   = new MenuItem { Header = Loc.S("Pc_EditSource") }; edit.Click += (_, _) => OpenSource(di);
            list.Add(update); list.Add(edit);
        }
        if (item is HeaderItem hdr)
        {
            var edit = new MenuItem { Header = Loc.S("Pc_Edit") }; edit.Click += async (_, _) => await EditItem(item);
            var width = new MenuItem { Header = Loc.S("Pc_HeaderWidth") }; width.Click += async (_, _) => await HeaderWidthDialog(hdr);
            list.Add(edit); list.Add(width);   // page number is toggled in the header editor (checkbox)
        }
        var del = new MenuItem { Header = Loc.S("Pc_Delete") };
        del.Click += (_, _) => { Page.Items.Remove(item); _diagramCache.Remove(item.Id); _selected.Remove(item.Id); RenderPage(); };
        list.Add(del);
        return list;
    }

    void Rescale(PrintItem item, double factor) => SetScale(item, item.Scale * factor);

    void SetScale(PrintItem item, double scale)
    {
        item.Scale = Math.Max(0.1, scale);
        _diagramCache.Remove(item.Id);
        RenderPage();
    }

    // "Scale…" — a percent field pre-filled with the current scale; 100 % is the item's original size.
    async Task ScaleDialog(PrintItem item)
    {
        var cur = ((int)Math.Round(item.Scale * 100)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var s = await PromptDialog.Show(this, Loc.S("Pc_ScalePrompt"), cur, Loc.S("Pc_ScaleTitle"));
        if (string.IsNullOrWhiteSpace(s)) return;
        s = s.Trim().TrimEnd('%').Trim().Replace(',', '.');
        if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct) && pct > 0)
            SetScale(item, pct / 100.0);
    }

    // Renders (and caches) a bitmap item at its current scale. Keyed by scale so a resize re-renders crisp rather
    // than raster-stretching. Runs on the UI thread.
    RenderTargetBitmap? GetCachedBitmap(PrintItem item, Func<RenderTargetBitmap?> render)
    {
        if (_diagramCache.TryGetValue(item.Id, out var c) && Math.Abs(c.Scale - item.Scale) < 0.001) return c.Bmp;
        var bmp = render();
        if (bmp is not null) _diagramCache[item.Id] = (bmp, item.Scale);
        return bmp;
    }

    void OpenSource(DiagramItem di)
    {
        if (di.Kind == DiagramKind.Flowchart)
            DiagramWindows.OpenOrActivate(DiagramWindows.FlowId(_projFolder, di.Key),
                () => new FlowChartWindow(_projFolder, di.Key, di.Caption, null));
    }

    async Task AddDiagram()
    {
        var entries = DiagramRenderer.ListAvailable(_projFolder);
        if (entries.Count == 0) { Notify(Loc.S("Pc_NoDiagrams")); return; }
        var pick = await PickDiagram(entries);
        if (pick is not { } e) return;

        // The diagram itself (transparent, tight) — placed first; its size drives where the decoration blocks go.
        const double x0 = 40, y0 = 90;
        // Render once at natural density just to measure the diagram's DIP size (drives decor placement); the
        // display bitmap is rendered on demand by BuildImageVisual at the right device density.
        var diag = DiagramRenderer.Render(_projFolder, e.Kind, e.Key, 1.0);
        double dw = diag?.Size.Width ?? 200, dh = diag?.Size.Height ?? 120;
        var di = new DiagramItem { Kind = e.Kind, Key = e.Key, Caption = e.Name, X = x0, Y = y0, ZOrder = NextZ() };
        Page.Items.Add(di);

        // Each decoration block as its own movable/deletable item, at the position it holds on the PAP.
        foreach (var pos in DiagramRenderer.DecorPositions(_projFolder, e.Kind, e.Key))
        {
            // Measure the decoration control (unscaled DIPs) to place it at the position it holds on the PAP.
            double pw = 90, ph = 30;
            if (DiagramRenderer.DecorControl(_projFolder, e.Kind, e.Key, pos) is { } ctrl)
            { ctrl.Measure(Size.Infinity); pw = ctrl.DesiredSize.Width; ph = ctrl.DesiredSize.Height; }
            bool left  = pos is DecorPos.TopLeft  or DecorPos.BottomLeft;
            bool right = pos is DecorPos.TopRight or DecorPos.BottomRight;
            double px = left ? x0 : right ? x0 + dw - pw : x0 + (dw - pw) / 2;
            double py = DiagramDecor.IsTopBand(pos) ? y0 - ph - 4 : y0 + dh + 4;
            var dc = new DiagramDecorItem { Kind = e.Kind, Key = e.Key, Pos = pos, X = Math.Max(0, px), Y = Math.Max(0, py), ZOrder = NextZ() };
            Page.Items.Add(dc);
        }
        RenderPage();
    }

    async Task<DiagramRenderer.Entry?> PickDiagram(List<DiagramRenderer.Entry> entries)
    {
        var dlg = new Window { Title = Loc.S("Pc_AddDiagramTitle"), Width = 380, Height = 460, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Ui.ThemeWindow(dlg);
        var listBox = new ListBox();
        ThemeInput(listBox);

        // Two type filters: 🔁 flowcharts, ▦ structograms (same glyphs as the sketchbook). Toggleable; PAP on by
        // default. The list shows only entries whose kind is enabled; the selection maps back through `filtered`.
        var filtered = new List<DiagramRenderer.Entry>();
        void Refresh(bool showPap, bool showNs)
        {
            filtered = entries.Where(x => x.Kind switch
            {
                DiagramKind.Structogram => showNs,
                DiagramKind.Board       => false,
                _                       => showPap,
            }).ToList();
            listBox.ItemsSource = filtered.Select(x => x.Name).ToList();
        }
        var papBtn = new ToggleButton { Content = "🔁 PAP", IsChecked = true,  Padding = new(12, 6) };
        var nsBtn  = new ToggleButton { Content = "▦ NS",  IsChecked = false, Padding = new(12, 6) };
        ThemeInput(papBtn); ThemeInput(nsBtn);   // themed background/foreground so the inactive state isn't white
        ToolTip.SetTip(papBtn, "Flowcharts (PAP)"); ToolTip.SetTip(nsBtn, "Structograms (NS)");
        void OnFilter(object? _, RoutedEventArgs __) => Refresh(papBtn.IsChecked == true, nsBtn.IsChecked == true);
        papBtn.IsCheckedChanged += OnFilter; nsBtn.IsCheckedChanged += OnFilter;
        Refresh(true, false);

        DiagramRenderer.Entry? result = null;
        void Accept() { if (listBox.SelectedIndex >= 0 && listBox.SelectedIndex < filtered.Count) { result = filtered[listBox.SelectedIndex]; dlg.Close(); } }
        listBox.DoubleTapped += (_, _) => Accept();
        var ok = Ui.Btn(Loc.S("Common_Add")); ok.Click += (_, _) => Accept();
        var cancel = Ui.Btn(Loc.S("Common_Cancel")); cancel.Click += (_, _) => dlg.Close();
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(0, 0, 0, 10), Children = { papBtn, nsBtn } };
        var grid = new Grid { Margin = new(14), RowDefinitions = new("Auto,*,Auto") };
        Grid.SetRow(filters, 0); grid.Children.Add(filters);
        Grid.SetRow(listBox, 1); grid.Children.Add(listBox);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new(0, 10, 0, 0), Children = { cancel, ok } };
        Grid.SetRow(btns, 2); grid.Children.Add(btns);
        dlg.Content = grid;
        await dlg.ShowDialog(this);
        return result;
    }

    const double HandleSize = 10;
    static readonly IBrush AccentBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x80, 0xED));

    // Wraps a raw visual in a container that SIZES ITSELF to the content (no pre-measure that can drift from the
    // real layout). The selection outline is stretched to the container, so it hugs the content exactly; the scale
    // handles sit on the corners via alignment + negative margins.
    Control WrapChrome(PrintItem item, Control raw)
    {
        var container = new Grid { Background = Brushes.Transparent };
        container.Children.Add(raw);

        if (_selected.Contains(item.Id))
        {
            container.Children.Add(new Border { BorderBrush = AccentBrush, BorderThickness = new(1.5), IsHitTestVisible = false });
            // Diagrams/decor scale via corner handles; a text box AND a header set their wrap width via a
            // right-edge handle (the header reflows — height follows content); a single-line label has no drag sizing.
            if (_selected.Count == 1)
            {
                if (item is DiagramItem or DiagramDecorItem) AddScaleHandles(container, item);
                else if (item is TextItem txt) AddWidthHandle(container, raw, txt);
                else if (item is HeaderItem hd) AddHeaderWidthHandle(container, raw, hd);
            }
        }

        WireItemPointer(container, item);
        container.DoubleTapped += async (_, _) => await EditItem(item);
        container.ContextMenu = new ContextMenu { ItemsSource = ItemMenu(item) };
        return container;
    }

    // Left-click selects (Strg+click toggles); dragging moves every selected item together. Selection chrome is
    // (re)drawn on release, so a drag stays smooth (no rebuild mid-gesture).
    void WireItemPointer(Control container, PrintItem item)
    {
        bool dragging = false; Point start = default;
        Dictionary<string, (double X, double Y)> orig = new();

        container.PointerPressed += (_, e) =>
        {
            var p = e.GetCurrentPoint(_pageCanvas);
            if (!p.Properties.IsLeftButtonPressed) return;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (!_selected.Remove(item.Id)) _selected.Add(item.Id);
                RenderPage(); e.Handled = true; return;
            }
            if (!_selected.Contains(item.Id)) { _selected.Clear(); _selected.Add(item.Id); }
            dragging = true; start = p.Position;
            orig = _selected.ToDictionary(id => id, id => { var it = Page.Items.First(x => x.Id == id); return (it.X, it.Y); });
            e.Pointer.Capture(container); e.Handled = true;
        };
        container.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var pos = e.GetPosition(_pageCanvas);
            double dx = (pos.X - start.X) / _viewScale, dy = (pos.Y - start.Y) / _viewScale;
            foreach (var kv in orig)
            {
                var it = Page.Items.First(x => x.Id == kv.Key);
                it.X = Snap(kv.Value.X + dx); it.Y = Snap(kv.Value.Y + dy);
                if (_visuals.TryGetValue(kv.Key, out var v)) { Canvas.SetLeft(v, it.X * _viewScale); Canvas.SetTop(v, it.Y * _viewScale); }
            }
        };
        container.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false; e.Pointer.Capture(null);
            RenderPage();   // redraw selection outline / handles at the new spot
        };
    }

    // Four corner handles that scale the item STEPLESSLY, aspect-locked, with a live preview (a render transform
    // anchored at the opposite corner) and a crisp re-render on release. The opposite corner stays put. All the
    // geometry comes from container.Bounds — the real, laid-out size — so the handles and the commit agree with
    // what's on screen. (RenderTransform doesn't alter Bounds, so it stays the pre-drag size for the whole drag.)
    void AddScaleHandles(Grid container, PrintItem item)
    {
        (HorizontalAlignment H, VerticalAlignment V, bool MovesLeft, bool MovesTop)[] corners =
        {
            (HorizontalAlignment.Left,  VerticalAlignment.Top,    true,  true),
            (HorizontalAlignment.Right, VerticalAlignment.Top,    false, true),
            (HorizontalAlignment.Left,  VerticalAlignment.Bottom, true,  false),
            (HorizontalAlignment.Right, VerticalAlignment.Bottom, false, false),
        };
        const double o = HandleSize / 2;
        foreach (var (h, v, movesLeft, movesTop) in corners)
        {
            var handle = new Border
            {
                Width = HandleSize, Height = HandleSize, Background = AccentBrush,
                BorderBrush = Brushes.White, BorderThickness = new(1.5),
                HorizontalAlignment = h, VerticalAlignment = v,
                Margin = new(h == HorizontalAlignment.Left  ? -o : 0, v == VerticalAlignment.Top    ? -o : 0,
                             h == HorizontalAlignment.Right ? -o : 0, v == VerticalAlignment.Bottom ? -o : 0),
                Cursor = new Cursor(StandardCursorType.SizeAll),
            };

            // The fixed anchor is the OPPOSITE corner, in page-canvas coordinates.
            Point Anchor()
            {
                var sz = container.Bounds.Size;
                return new Point(item.X * _viewScale + (movesLeft ? sz.Width : 0),
                                 item.Y * _viewScale + (movesTop  ? sz.Height : 0));
            }

            bool scaling = false; double grabDist = 1, factor = 1;
            handle.PointerPressed += (_, e) =>
            {
                var a = Anchor();
                grabDist = Math.Max(1, Dist(e.GetPosition(_pageCanvas), a.X, a.Y)); factor = 1;
                container.RenderTransformOrigin = new RelativePoint(movesLeft ? 1 : 0, movesTop ? 1 : 0, RelativeUnit.Relative);
                scaling = true; e.Pointer.Capture(handle); e.Handled = true;
            };
            handle.PointerMoved += (_, e) =>
            {
                if (!scaling) return;
                var a = Anchor();
                factor = Math.Max(0.1, Dist(e.GetPosition(_pageCanvas), a.X, a.Y) / grabDist);
                container.RenderTransform = new ScaleTransform(factor, factor);   // live preview
                e.Handled = true;
            };
            handle.PointerReleased += (_, e) =>
            {
                if (!scaling) return;
                scaling = false; e.Pointer.Capture(null);
                var sz = container.Bounds.Size;
                double wItem = sz.Width / _viewScale, hItem = sz.Height / _viewScale;
                double f = factor;
                // Snap-to-grid on mouse resize: snap the moving edge to the nearest grid line, keep aspect ratio.
                if (_doc.GridSnap && GridPx96 > 0.5)
                {
                    double movingX = movesLeft ? (item.X + wItem) - wItem * f : item.X + wItem * f;
                    double snapped = Snap(movingX);
                    double newW = movesLeft ? (item.X + wItem) - snapped : snapped - item.X;
                    if (newW > 4) f = newW / wItem;
                }
                if (movesLeft) item.X = (item.X + wItem) - wItem * f;   // keep the opposite (right) edge fixed
                if (movesTop)  item.Y = (item.Y + hItem) - hItem * f;   // keep the opposite (bottom) edge fixed
                item.Scale = Math.Max(0.1, item.Scale * f);
                container.RenderTransform = null;
                _diagramCache.Remove(item.Id);
                RenderPage();   // re-render crisp at the new scale
            };
            container.Children.Add(handle);
        }
    }

    static double Dist(Point p, double x, double y) { double dx = p.X - x, dy = p.Y - y; return Math.Sqrt(dx * dx + dy * dy); }

    // A right-edge handle that sets a text box's WRAP WIDTH by dragging. Live: the inner TextBlock's width grows
    // with the drag (the outline and handle follow); committed to the item on release.
    void AddWidthHandle(Grid container, Control raw, TextItem txt)
    {
        // The content is a Border wrapping either a single TextBlock or the list StackPanel — either way a Control
        // with a Width we can grow live during the drag.
        var tb = (raw as Border)?.Child as Control;
        if (tb is null) return;
        var handle = new Border
        {
            Width = HandleSize, Height = 26, CornerRadius = new(3), Background = AccentBrush, BorderBrush = Brushes.White, BorderThickness = new(1.5),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new(0, 0, -HandleSize / 2, 0), Cursor = new Cursor(StandardCursorType.SizeWestEast),
        };
        bool sizing = false; double startX = 0, origW = 0;
        handle.PointerPressed += (_, e) =>
        {
            startX = e.GetPosition(_pageCanvas).X; origW = txt.Width;
            sizing = true; e.Pointer.Capture(handle); e.Handled = true;
        };
        handle.PointerMoved += (_, e) =>
        {
            if (!sizing) return;
            double w = Math.Max(40, origW + (e.GetPosition(_pageCanvas).X - startX) / _viewScale);
            txt.Width = w; tb.Width = w * _viewScale;   // live: grows the box, outline + handle follow
            e.Handled = true;
        };
        handle.PointerReleased += (_, e) => { if (sizing) { sizing = false; e.Pointer.Capture(null); RenderPage(); } };
        container.Children.Add(handle);
    }

    // A right-edge handle that sets a HEADER's wrap width (MaxWidth) by dragging — the info table REFLOWS (text stays
    // unwarped, height follows the content). raw is the LayoutTransformControl from BuildHeaderVisual; its Child is the
    // header block whose MaxWidth we grow live. Drag is in page px → divide by the block's on-page scale (zoom×hd.Scale).
    void AddHeaderWidthHandle(Grid container, Control raw, HeaderItem hd)
    {
        if (raw is not LayoutTransformControl { Child: Control inner }) return;
        double sc = Math.Max(0.1, hd.Scale);
        double s = Math.Max(0.01, sc * _viewScale);
        var pr = Page.PrintablePx;
        // Max inner width so the block's right edge lands exactly on the printable right margin (same cap as
        // BuildHeaderVisual) — clamping live avoids the jump-back that happened when the drag ran past the margin.
        double hardMax = Math.Max(60, (pr.X + pr.W - hd.X) / sc);
        var handle = new Border
        {
            Width = HandleSize, Height = 26, CornerRadius = new(3), Background = AccentBrush, BorderBrush = Brushes.White, BorderThickness = new(1.5),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new(0, 0, -HandleSize / 2, 0), Cursor = new Cursor(StandardCursorType.SizeWestEast),
        };
        bool sizing = false; double startX = 0, origW = 0;
        handle.PointerPressed += (_, e) =>
        {
            startX = e.GetPosition(_pageCanvas).X;
            origW = hd.MaxWidth > 1 ? hd.MaxWidth : container.Bounds.Width / s;   // current effective width if unset
            sizing = true; e.Pointer.Capture(handle); e.Handled = true;
        };
        handle.PointerMoved += (_, e) =>
        {
            if (!sizing) return;
            double w = Math.Clamp(origW + (e.GetPosition(_pageCanvas).X - startX) / s, 60, hardMax);
            inner.Width = w;   // live: the block fills to the new width within the margin, outline + handle follow
            e.Handled = true;
        };
        handle.PointerReleased += (_, e) =>
        {
            if (!sizing) return;
            sizing = false; e.Pointer.Capture(null);
            double w = Math.Clamp(origW + (e.GetPosition(_pageCanvas).X - startX) / s, 60, hardMax);
            // Snap the RIGHT edge to the grid (in page coords), but never past the printable right margin.
            double rightPage = Math.Min(Snap(hd.X + w * sc), pr.X + pr.W);
            hd.MaxWidth = Math.Clamp((rightPage - hd.X) / sc, 60, hardMax);
            RenderPage();
        };
        container.Children.Add(handle);
    }

    async Task EditItem(PrintItem item)
    {
        if (item is LabelItem or TextItem)
        {
            if (await EditText(item)) RenderPage();
        }
        else if (item is HeaderItem hd)
        {
            // Reuse the diagram header editor — same fields, same saved templates. It mutates hd.Style in place.
            var newTitle = await DiagramDecorDialog.Show(this, hd.Title, hd.Style, null, ProjectService.DisplayName(_projFolder));
            if (newTitle is not null) { hd.Title = newTitle; RenderPage(); }
        }
    }

    // Insert a header: open the header editor first (same as a PAP), then place its decoration slots as SEPARATE
    // items at the positions they hold (TopLeft/TopCenter/…), so the header can be decomposed. Cancel adds nothing.
    async Task AddHeader()
    {
        var style = new DiagramStyle { ShowTitle = true, ShowInfo = true };
        try { HeaderTemplateService.ApplyDefault(isPap: true, style); } catch { /* default is optional */ }
        var title = await DiagramDecorDialog.Show(this, "Header", style, null, ProjectService.DisplayName(_projFolder));
        if (title is null) return;   // cancelled

        var pr = Page.PrintablePx;            // place & size within the printable area (inside the margins)
        double maxW = pr.W;                   // default header max width = full printable width → long fields wrap
        var pieces = DiagramDecor.EnumeratePieces(title, style);
        if (pieces.Count == 0) { Notify(Loc.S("Pc_HeaderEmpty")); return; }

        _selected.Clear();
        foreach (var (pos, ctrl) in pieces)
        {
            ctrl.MaxWidth = maxW;                 // wrap long fields; measure at the constrained width for placement
            ctrl.Measure(new Size(maxW, double.PositiveInfinity));
            var sz = ctrl.DesiredSize;
            bool left  = pos is DecorPos.TopLeft or DecorPos.BottomLeft;
            bool right = pos is DecorPos.TopRight or DecorPos.BottomRight;
            double x = left ? pr.X : right ? pr.X + pr.W - sz.Width : pr.X + (pr.W - sz.Width) / 2;
            double y = DiagramDecor.IsTopBand(pos) ? pr.Y : pr.Y + pr.H - sz.Height;
            var hd = new HeaderItem { Title = title, Style = CloneStyle(style), Pos = pos, MaxWidth = maxW, X = Math.Max(0, x), Y = Math.Max(0, y), ZOrder = NextZ() };
            Page.Items.Add(hd); _selected.Add(hd.Id);
        }
        RenderPage();
    }

    // The header's maximum render width (in cm) — long info fields wrap instead of overflowing the page. "To
    // default" sets it to (page width − 3 cm). Applies to every header piece that shares this header's title.
    async Task HeaderWidthDialog(HeaderItem hdr)
    {
        const double cmToDip = 96.0 / 2.54;
        double pageDefault = Math.Max(120, Page.SizePx.W - 3 * cmToDip);

        var dlg = new Window { Title = Loc.S("Pc_HeaderWidthTitle"), CanResize = false,
            SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Ui.ThemeWindow(dlg);
        var field = new TextBox { Width = 80, Text = Num((hdr.MaxWidth > 1 ? hdr.MaxWidth : pageDefault) / cmToDip) }; ThemeInput(field);
        var toDef = Ui.Btn(Loc.S("Pc_ToDefault")); toDef.Click += (_, _) => field.Text = Num(pageDefault / cmToDip);
        bool ok = false;
        var okBtn = Ui.Btn(Loc.S("Common_Ok")); okBtn.Click += (_, _) => { ok = true; dlg.Close(); };
        var cancel = Ui.Btn(Loc.S("Common_Cancel")); cancel.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel { Margin = new(16), Spacing = 12, Children = {
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Lbl(Loc.S("Pc_WidthCm")), field, toDef } },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, okBtn } },
        } };
        await dlg.ShowDialog(this);
        if (!ok) return;
        double dip = Math.Max(40, ParseNum(field.Text, hdr.MaxWidth / cmToDip) * cmToDip);
        // Apply to all header pieces of the same header (same title) so the block stays consistent.
        foreach (var it in Page.Items.OfType<HeaderItem>().Where(h => h.Title == hdr.Title)) it.MaxWidth = dip;
        RenderPage();
    }

    void AddLabel()
    {
        var lbl = new LabelItem { Text = "Label", X = 40, Y = 40, ZOrder = NextZ() };
        Page.Items.Add(lbl); _selected.Clear(); _selected.Add(lbl.Id); RenderPage();
    }

    void AddTextBox()
    {
        var txt = new TextItem { X = 40, Y = 40, ZOrder = NextZ() }; txt.PlainText = "Text";
        Page.Items.Add(txt); _selected.Clear(); _selected.Add(txt.Id); RenderPage();
    }

    // Shared editor for a Label (single line) and a Text box (multi-line): text, font family + size, B/I/U/S,
    // text/background/border colours, border thickness — and, for a Text box, wrap width + alignment.
    async Task<bool> EditText(PrintItem item)
    {
        if (item is TextItem rich) return await EditRichText(rich);   // text boxes use the selection-based rich editor
        bool isText = item is TextItem;
        string curText   = isText ? ((TextItem)item).PlainText : ((LabelItem)item).Text;
        string curFamily = isText ? ((TextItem)item).FontFamily : ((LabelItem)item).FontFamily;
        double curSize   = isText ? ((TextItem)item).FontSize : ((LabelItem)item).FontSize;

        var dlg = new Window { Title = isText ? Loc.S("Pc_TextBox") : Loc.S("Pc_Label"), Width = 500, MinWidth = 440, CanResize = true,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Ui.ThemeWindow(dlg);

        var textBox = new TextBox { Text = curText, AcceptsReturn = isText, MinHeight = isText ? 90 : 0,
            TextWrapping = isText ? TextWrapping.Wrap : TextWrapping.NoWrap };
        ThemeInput(textBox);

        var family = new ComboBox { MinWidth = 180 }; ThemeInput(family);
        family.Items.Add(Loc.S("Pc_Default"));
        foreach (var f in FontManager.Current.SystemFonts.Select(f => f.Name).Distinct().OrderBy(n => n)) family.Items.Add(f);
        family.SelectedIndex = string.IsNullOrEmpty(curFamily) ? 0 : Math.Max(0, family.Items.IndexOf(curFamily));

        var size = new TextBox { Text = Num(curSize), Width = 56 }; ThemeInput(size);

        var bold = new ToggleButton { Content = "B", FontWeight = FontWeight.Bold, MinWidth = 34 };
        var ital = new ToggleButton { Content = "I", FontStyle = FontStyle.Italic, MinWidth = 34 };
        var undl = new ToggleButton { Content = "U", MinWidth = 34 };
        var strk = new ToggleButton { Content = "S", MinWidth = 34 };
        foreach (var t in new[] { bold, ital, undl, strk }) ThemeInput(t);   // readable glyphs (not white-on-white)
        bold.IsChecked = GetBool(item, "Bold"); ital.IsChecked = GetBool(item, "Italic");
        undl.IsChecked = GetBool(item, "Underline"); strk.IsChecked = GetBool(item, "Strike");

        var textCol   = new ColorField(Loc.S("Pc_ColorText"));  SetColorField(textCol, GetStr(item, "Color"), Colors.Black, allowNone: false);
        var backCol   = new ColorField(Loc.S("Pc_Background")); SetColorField(backCol, GetStr(item, "Background"), Colors.White, allowNone: true);
        var borderCol = new ColorField(Loc.S("Pc_Border"));     SetColorField(borderCol, GetStr(item, "BorderColor"), Colors.Black, allowNone: true);
        var borderW   = new TextBox { Text = Num(GetNum(item, "BorderThickness")), Width = 56 }; ThemeInput(borderW);

        var styleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { bold, ital, undl, strk } };
        var fontRow  = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { family, Lbl(Loc.S("Pc_Size")), Spinner(size, 4, 400, 1) } };
        var borderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { borderCol, Lbl(Loc.S("Pc_BorderWidth")), Spinner(borderW, 0, 40, 0.5) } };

        var panel = new StackPanel { Margin = new(16), Spacing = 10, Children = { textBox, fontRow, styleRow, textCol, backCol, borderRow } };

        ComboBox? alignBox = null;
        if (isText)
        {
            var t = (TextItem)item;
            alignBox = new ComboBox { MinWidth = 110 }; ThemeInput(alignBox);
            foreach (var a in new[] { "Pc_AlignLeft", "Pc_AlignCenter", "Pc_AlignRight" }) alignBox.Items.Add(Loc.S(a));
            alignBox.SelectedIndex = t.Align == "Center" ? 1 : t.Align == "Right" ? 2 : 0;

            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = {
                Lbl(Loc.S("Pc_Align")), alignBox } });
            // Lists are automatic — hint on its own wrapping line so it never overflows the window.
            var hint = new TextBlock { Text = Loc.S("Pc_ListHint"), TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
            Ui.Theme(hint, TextBlock.ForegroundProperty, "SidebarTextBrush");
            panel.Children.Add(hint);
        }

        bool ok = false;
        var okBtn = Ui.Btn(Loc.S("Common_Ok")); okBtn.Click += (_, _) => { ok = true; dlg.Close(); };
        var cancel = Ui.Btn(Loc.S("Common_Cancel")); cancel.Click += (_, _) => dlg.Close();
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, okBtn } });
        dlg.Content = panel;
        await dlg.ShowDialog(this);
        if (!ok) return false;

        string fam = family.SelectedIndex <= 0 ? "" : (family.SelectedItem as string ?? "");
        double sz  = Math.Clamp(ParseNum(size.Text, curSize), 4, 400);
        string? textHex = HexColorPicker.HexOf(textCol.Color);
        string? bgHex   = backCol.Inherit ? null : HexColorPicker.HexOf(backCol.Color);
        string? bdHex   = borderCol.Inherit ? null : HexColorPicker.HexOf(borderCol.Color);
        double bw       = Math.Clamp(ParseNum(borderW.Text, 0), 0, 40);

        if (item is LabelItem l)
        {
            l.Text = textBox.Text ?? ""; l.FontFamily = fam; l.FontSize = sz;
            l.Bold = bold.IsChecked == true; l.Italic = ital.IsChecked == true; l.Underline = undl.IsChecked == true; l.Strike = strk.IsChecked == true;
            l.Color = textHex ?? "#000000"; l.Background = bgHex; l.BorderColor = bdHex; l.BorderThickness = bw;
        }
        else if (item is TextItem tx)
        {
            tx.PlainText = textBox.Text ?? ""; tx.FontFamily = fam; tx.FontSize = sz;
            tx.Bold = bold.IsChecked == true; tx.Italic = ital.IsChecked == true; tx.Underline = undl.IsChecked == true; tx.Strike = strk.IsChecked == true;
            tx.Color = textHex ?? "#000000"; tx.Background = bgHex; tx.BorderColor = bdHex; tx.BorderThickness = bw;
            if (alignBox is not null) tx.Align = alignBox.SelectedIndex == 1 ? "Center" : alignBox.SelectedIndex == 2 ? "Right" : "Left";
        }
        return true;
    }

    // Rich text-box editor: plain typing + a selection-based formatting toolbar (Avalonia has no rich-text control),
    // a live formatted PREVIEW, and box-level font/line-spacing/colour/border. Formatting is stored per TextRun and
    // kept aligned to the characters as the text is edited. Font FAMILY stays per box.
    async Task<bool> EditRichText(TextItem t)
    {
        // The rich text-box editor lives in the shared RichTextEditorDialog (also used by the board). Build a neutral
        // model from this item, edit, and copy the result back. Font FAMILY stays per box.
        EnsureRunsMigrated(t);
        var m = new RichTextModel
        {
            Runs = t.Runs.Select(r => r.Clone()).ToList(),
            FontFamily = t.FontFamily, FontSize = t.FontSize, Align = t.Align, LineSpacing = t.LineSpacing,
            Color = t.Color, Background = t.Background, BorderColor = t.BorderColor, BorderThickness = t.BorderThickness,
        };
        if (!await RichTextEditorDialog.Edit(this, m, Loc.S("Pc_TextBox"), Page.Background)) return false;
        t.Runs = m.Runs; t.FontFamily = m.FontFamily; t.FontSize = m.FontSize; t.Align = m.Align;
        t.LineSpacing = m.LineSpacing; t.Color = m.Color; t.Background = m.Background;
        t.BorderColor = m.BorderColor; t.BorderThickness = m.BorderThickness;
        return true;
    }

    // A numeric text field plus tiny ▲/▼ step buttons (own buttons, so no unthemed spinner glyphs). The whole
    // group is vertically centred so the field, arrows and label sit on one line.
    Control Spinner(TextBox field, double min, double max, double step)
    {
        void Bump(double d) => field.Text = Num(Math.Clamp(ParseNum(field.Text, min) + d, min, max));
        var up = new Button { Content = "▲", FontSize = 8, Padding = new(5, 0), MinWidth = 0 }; up.Click += (_, _) => Bump(step);
        var dn = new Button { Content = "▼", FontSize = 8, Padding = new(5, 0), MinWidth = 0 }; dn.Click += (_, _) => Bump(-step);
        ThemeInput(up); ThemeInput(dn);
        field.VerticalAlignment = VerticalAlignment.Center;
        var stack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { up, dn } };
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { field, stack } };
    }

    static string Num(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    static double ParseNum(string? s, double fallback)
        => double.TryParse((s ?? "").Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    static void SetColorField(ColorField f, string? hex, Color fallback, bool allowNone)
    {
        if (string.IsNullOrWhiteSpace(hex)) { if (allowNone) f.Inherit = true; else { f.Inherit = false; f.Color = fallback; } }
        else { f.Inherit = false; try { f.Color = Color.Parse(hex); } catch { f.Color = fallback; } }
    }

    // Small reflection-free accessors so the shared editor can read either item type's common properties.
    static bool   GetBool(PrintItem it, string p) => it switch {
        LabelItem l => p switch { "Bold" => l.Bold, "Italic" => l.Italic, "Underline" => l.Underline, "Strike" => l.Strike, _ => false },
        TextItem t  => p switch { "Bold" => t.Bold, "Italic" => t.Italic, "Underline" => t.Underline, "Strike" => t.Strike, _ => false },
        _ => false };
    static string? GetStr(PrintItem it, string p) => it switch {
        LabelItem l => p switch { "Color" => l.Color, "Background" => l.Background, "BorderColor" => l.BorderColor, _ => null },
        TextItem t  => p switch { "Color" => t.Color, "Background" => t.Background, "BorderColor" => t.BorderColor, _ => null },
        _ => null };
    static double GetNum(PrintItem it, string p) => it switch {
        LabelItem l => l.BorderThickness, TextItem t => t.BorderThickness, _ => 0 };

    // ── Helpers ─────────────────────────────────────────────────────────────
    void GoToPage(int i)
    {
        if (i < 0 || i >= _doc.Pages.Count) return;
        _pageIndex = i; RenderPage();
    }

    void RemovePage()
    {
        if (_doc.Pages.Count <= 1) return;
        _doc.Pages.RemoveAt(_pageIndex);
        _pageIndex = Math.Min(_pageIndex, _doc.Pages.Count - 1);
        RenderPage();
    }

    int NextZ() => Page.Items.Count == 0 ? 0 : Page.Items.Max(i => i.ZOrder) + 1;

    static TextDecorationCollection? Decorations(bool underline, bool strike)
    {
        var dec = new TextDecorationCollection();
        if (underline) dec.Add(new TextDecoration { Location = TextDecorationLocation.Underline });
        if (strike)    dec.Add(new TextDecoration { Location = TextDecorationLocation.Strikethrough });
        return dec.Count > 0 ? dec : null;
    }

    void Notify(string msg) => Title = $"Print / Export composer — {msg}";

    static Control Sep() => new Border { Width = 1, Margin = new(6, 2), Background = Brushes.Gray, Opacity = 0.4 };

    Button TB(string label)
    {
        var b = Ui.Btn(label);
        b.VerticalAlignment = VerticalAlignment.Center;
        return b;
    }

    static void ThemeInput(Control c)
    {
        Ui.Theme(c, TemplatedControl.BackgroundProperty,  "InputBgBrush");
        Ui.Theme(c, TemplatedControl.ForegroundProperty,  "SidebarTextBrush");
        Ui.Theme(c, TemplatedControl.BorderBrushProperty, "ControlBorderBrush");
    }
}
