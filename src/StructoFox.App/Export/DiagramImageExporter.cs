using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using StructoFox.Core;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// Exports a single diagram (PAP or structogram) AS DISPLAYED — the body plus its decoration/title block — to one
/// image file (PNG / JPG / BMP / TIFF) at a chosen DPI. Reused by both editor windows. The diagram body is supplied
/// by the caller (a rendered PAP bitmap, or the live structogram control); decoration is composed via DiagramDecor.
/// </summary>
public static class DiagramImageExporter
{
    /// <summary>Shows a format + DPI dialog, then a save picker, then writes the file. <paramref name="body"/> is the
    /// diagram at native size; it is composed with its decoration and scaled to the chosen DPI.</summary>
    public static async Task RunDialog(Window owner, string suggestedName, Control body, string title, DiagramStyle style)
    {
        var dlg = new Window { Title = Loc.S("ImgExport_Title"), CanResize = false,
            SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Ui.ThemeWindow(dlg);

        var fmt = Ui.Combo(160);
        foreach (var f in new[] { "PNG", "JPG", "BMP", "TIFF" }) fmt.Items.Add(f);
        fmt.SelectedIndex = 0;

        var dpiBox = new TextBox { Width = 70, Text = "300" };
        Ui.Theme(dpiBox, TextBox.BackgroundProperty, "InputBgBrush");
        Ui.Theme(dpiBox, TextBox.ForegroundProperty, "SidebarTextBrush");
        Ui.Theme(dpiBox, TextBox.BorderBrushProperty, "ControlBorderBrush");

        bool ok = false;
        var okBtn = Ui.Btn(Loc.S("Common_Ok")); okBtn.Click += (_, _) => { ok = true; dlg.Close(); };
        var cancel = Ui.Btn(Loc.S("Common_Cancel")); cancel.Click += (_, _) => dlg.Close();
        TextBlock Lbl(string t) { var b = new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center }; Ui.Theme(b, TextBlock.ForegroundProperty, "SidebarTextBrush"); return b; }
        dlg.Content = new StackPanel { Margin = new(16), Spacing = 12, Children = {
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Lbl(Loc.S("Pc_ExportFormat")), fmt } },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Lbl(Loc.S("Pc_ExportDpi")), dpiBox } },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, okBtn } },
        } };
        await dlg.ShowDialog(owner);
        if (!ok) return;

        int dpi = int.TryParse(dpiBox.Text, out var d) ? System.Math.Clamp(d, 36, 1200) : 300;
        var (ext, typeName) = fmt.SelectedIndex switch { 1 => ("jpg", "JPEG"), 2 => ("bmp", "BMP"), 3 => ("tif", "TIFF"), _ => ("png", "PNG") };

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.S("ImgExport_Title"),
            SuggestedFileName = PrintDocumentService.Sanitize(suggestedName) + "." + ext,
            DefaultExtension = ext,
            FileTypeChoices = new[] { new FilePickerFileType(typeName) { Patterns = new[] { "*." + ext } } },
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        try { Save(path, body, title, style, dpi); }
        catch (System.Exception ex) { await MessageDialog.Show(owner, $"Export failed:\n{ex.Message}", Loc.S("ImgExport_Title")); }
    }

    // Composes body + decoration, renders to a bitmap at the target DPI, and encodes to the file's format.
    static void Save(string path, Control body, string title, DiagramStyle style, int dpi)
    {
        var composed = DiagramDecor.Compose(body, title, style, null);
        var bg = TryBrush(style.BackgroundColor);
        var host = new Border { Child = composed, Background = bg, Padding = new(12),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };

        host.Measure(Avalonia.Size.Infinity);
        var sz = host.DesiredSize;
        if (sz.Width < 1 || sz.Height < 1) throw new System.InvalidOperationException("Nothing to export.");
        host.Arrange(new Rect(sz));

        double scale = dpi / 96.0;
        var px = new PixelSize(System.Math.Max(1, (int)System.Math.Ceiling(sz.Width * scale)),
                               System.Math.Max(1, (int)System.Math.Ceiling(sz.Height * scale)));
        using var rtb = new RenderTargetBitmap(px, new Vector(96 * scale, 96 * scale));   // scales the whole visual
        rtb.Render(host);

        using var ms = new System.IO.MemoryStream();
        rtb.Save(ms);                       // PNG bytes at the scaled pixel size
        Encode(path, ms.ToArray(), dpi);
    }

    static void Encode(string path, byte[] pngBytes, int dpi)
    {
        using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(pngBytes);
        img.Metadata.ResolutionUnits    = PixelResolutionUnit.PixelsPerInch;
        img.Metadata.HorizontalResolution = dpi;
        img.Metadata.VerticalResolution   = dpi;

        switch (System.IO.Path.GetExtension(path).ToLowerInvariant())
        {
            case ".jpg": case ".jpeg":
                img.Mutate(x => x.BackgroundColor(SixLabors.ImageSharp.Color.White));   // JPEG has no alpha
                img.SaveAsJpeg(path, new JpegEncoder { Quality = 92 });
                break;
            case ".bmp":
                img.Mutate(x => x.BackgroundColor(SixLabors.ImageSharp.Color.White));
                img.SaveAsBmp(path);
                break;
            case ".tif": case ".tiff":
                img.SaveAsTiff(path, new TiffEncoder { Compression = SixLabors.ImageSharp.Formats.Tiff.Constants.TiffCompression.Deflate });
                break;
            default:
                img.SaveAsPng(path);
                break;
        }
    }

    static IBrush? TryBrush(string hex)
    {
        try { return string.IsNullOrWhiteSpace(hex) ? null : new SolidColorBrush(Avalonia.Media.Color.Parse(hex)); }
        catch { return null; }
    }
}
