using System.IO;
using BitMiracle.LibTiff.Classic;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace StructoFox.App;

/// <summary>
/// Encodes already-rendered page bitmaps (given as PNG bytes) into the composer's non-PNG export formats: single- and
/// multi-page TIFF (via LibTiff, so pages may differ in size) and PDF (via PDFsharp — a JPEG page image plus an
/// invisible selectable text layer). Cross-platform, pure managed. Author/copyright/date/program metadata are stamped
/// consistently across formats; the program string names the open-source encoder actually used.
/// </summary>
public static class DocumentExporter
{
    /// <summary>Author / copyright / title / timestamp written into every exported file's properties.</summary>
    public readonly record struct ExportMeta(string Author, string Copyright, string Title, System.DateTime Date);

    /// <summary>One selectable/searchable line of text at a position on a page (canvas pixels + line height).</summary>
    public readonly record struct PdfTextRun(string Text, double X, double Y, double Height);

    // ── TIFF (LibTiff for BOTH single- and multi-page: different page sizes work, metadata is uniform) ──

    public static void SaveTiff(string path, byte[] pagePng, int dpi, ExportMeta meta)
        => SaveTiffMultipage(path, new[] { pagePng }, dpi, meta);

    public static void SaveTiffMultipage(string path, IReadOnlyList<byte[]> pagesPng, int dpi, ExportMeta meta)
    {
        using var tif = Tiff.Open(path, "w") ?? throw new IOException("Could not open the TIFF file for writing.");
        int count = pagesPng.Count;
        for (int idx = 0; idx < count; idx++)
        {
            using var img = Image.Load<Rgba32>(pagesPng[idx]);
            int w = img.Width, h = img.Height, stride = w * 4;
            var buffer = new byte[stride * h];
            img.CopyPixelDataTo(buffer);   // packed RGBA, top-to-bottom

            SetTiffPage(tif, w, h, dpi, idx, count, meta);
            var row = new byte[stride];
            for (int y = 0; y < h; y++)
            {
                System.Array.Copy(buffer, y * stride, row, 0, stride);
                tif.WriteScanline(row, y);
            }
            tif.WriteDirectory();   // finalize this page and start the next
        }
    }

    static void SetTiffPage(Tiff tif, int w, int h, int dpi, int page, int pageCount, ExportMeta meta)
    {
        tif.SetField(TiffTag.IMAGEWIDTH, w);
        tif.SetField(TiffTag.IMAGELENGTH, h);
        tif.SetField(TiffTag.SAMPLESPERPIXEL, 4);
        tif.SetField(TiffTag.BITSPERSAMPLE, 8);
        tif.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
        tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tif.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
        tif.SetField(TiffTag.EXTRASAMPLES, 1, new short[] { (short)ExtraSample.UNASSALPHA });
        tif.SetField(TiffTag.COMPRESSION, Compression.DEFLATE);
        tif.SetField(TiffTag.ROWSPERSTRIP, h);
        tif.SetField(TiffTag.RESOLUTIONUNIT, ResUnit.INCH);
        tif.SetField(TiffTag.XRESOLUTION, (double)dpi);
        tif.SetField(TiffTag.YRESOLUTION, (double)dpi);
        tif.SetField(TiffTag.SUBFILETYPE, FileType.PAGE);
        tif.SetField(TiffTag.PAGENUMBER, page, pageCount);
        tif.SetField(TiffTag.SOFTWARE, "StructoFox / LibTiff.NET");
        tif.SetField(TiffTag.DATETIME, meta.Date.ToString("yyyy:MM:dd HH:mm:ss"));
        if (!string.IsNullOrWhiteSpace(meta.Author))    tif.SetField(TiffTag.ARTIST, meta.Author);
        if (!string.IsNullOrWhiteSpace(meta.Copyright)) tif.SetField(TiffTag.COPYRIGHT, meta.Copyright);
        if (!string.IsNullOrWhiteSpace(meta.Title))     tif.SetField(TiffTag.DOCUMENTNAME, meta.Title);
        // XMP packet — Windows Explorer reads this for "Date acquired" (Erfassungsdatum), author, copyright, program.
        var xmp = BuildXmp(meta, "StructoFox / LibTiff.NET");
        tif.SetField(TiffTag.XMLPACKET, xmp.Length, xmp);
    }

    /// <summary>The XMP metadata packet (author / copyright / program / Date-acquired) that Windows reads for an
    /// image's Details tab. Same schema as the PNG export's XMP.</summary>
    static byte[] BuildXmp(ExportMeta meta, string program)
    {
        static string Esc(string s) => System.Security.SecurityElement.Escape(s ?? "") ?? "";
        string date = meta.Date.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
        string xmp =
            "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
            "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:MicrosoftPhoto=\"http://ns.microsoft.com/photo/1.0/\">" +
            $"<xmp:CreatorTool>{Esc(program)}</xmp:CreatorTool>" +
            $"<MicrosoftPhoto:DateAcquired>{date}</MicrosoftPhoto:DateAcquired>" +
            (string.IsNullOrWhiteSpace(meta.Author) ? "" : $"<dc:creator><rdf:Seq><rdf:li>{Esc(meta.Author)}</rdf:li></rdf:Seq></dc:creator>") +
            (string.IsNullOrWhiteSpace(meta.Copyright) ? "" : $"<dc:rights><rdf:Alt><rdf:li xml:lang=\"x-default\">{Esc(meta.Copyright)}</rdf:li></rdf:Alt></dc:rights>") +
            "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        return System.Text.Encoding.UTF8.GetBytes(xmp);
    }

    // ── PDF (crisp JPEG page background + invisible selectable text layer, like an OCR'd scan) ──

    public static void SavePdf(string path, IReadOnlyList<(byte[] Png, IReadOnlyList<PdfTextRun> Texts)> pages,
                               int dpi, ExportMeta meta)
    {
        EnsureFontResolver();
        using var doc = new PdfDocument();
        doc.Info.Creator = "StructoFox / PDFsharp";
        doc.Info.CreationDate = meta.Date;   // PDF's "content created" date (Windows has no image-style "Date acquired" for PDFs)
        if (!string.IsNullOrWhiteSpace(meta.Title))     doc.Info.Title   = meta.Title;
        if (!string.IsNullOrWhiteSpace(meta.Author))    doc.Info.Author  = meta.Author;
        if (!string.IsNullOrWhiteSpace(meta.Copyright)) doc.Info.Subject = meta.Copyright;

        var streams = new List<MemoryStream>();   // XImage keeps its stream open until the document is saved
        var invisible = new XSolidBrush(XColor.FromArgb(0, 0, 0, 0));
        foreach (var (png, texts) in pages)
        {
            // publiclyVisible so PDFsharp's GetBuffer() works (a plain new MemoryStream(bytes) forbids it).
            var jpeg = ToJpeg(png);
            var ms = new MemoryStream(jpeg, 0, jpeg.Length, writable: false, publiclyVisible: true);
            streams.Add(ms);
            var xi = XImage.FromStream(ms);
            var page = doc.AddPage();
            page.Width  = XUnit.FromPoint(xi.PixelWidth  * 72.0 / dpi);
            page.Height = XUnit.FromPoint(xi.PixelHeight * 72.0 / dpi);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawImage(xi, 0, 0, page.Width.Point, page.Height.Point);

            if (SystemFontResolver.HasFont)
                foreach (var t in texts)
                {
                    double fh = t.Height * 72.0 / dpi;
                    if (fh < 1 || string.IsNullOrWhiteSpace(t.Text)) continue;
                    try
                    {
                        var font = new XFont("SF", fh * 0.8);
                        gfx.DrawString(t.Text, font, invisible, new XPoint(t.X * 72.0 / dpi, t.Y * 72.0 / dpi + fh * 0.8));
                    }
                    catch { /* skip a line the font can't render */ }
                }
        }
        doc.Save(path);
        foreach (var s in streams) s.Dispose();
    }

    static void EnsureFontResolver()
    {
        if (PdfSharp.Fonts.GlobalFontSettings.FontResolver is null)
            PdfSharp.Fonts.GlobalFontSettings.FontResolver = SystemFontResolver.Instance;
    }

    // PNG bytes → JPEG bytes, flattening any transparency onto white (JPEG has no alpha).
    static byte[] ToJpeg(byte[] png)
    {
        using var img = Image.Load<Rgba32>(png);
        img.Mutate(x => x.BackgroundColor(Color.White));
        using var ms = new MemoryStream();
        img.SaveAsJpeg(ms, new JpegEncoder { Quality = 92 });
        return ms.ToArray();
    }
}

/// <summary>Feeds one system font to PDFsharp for the invisible text layer (PDFsharp needs a resolver to draw any
/// text). Picks the first available common sans-serif; if none is found, the PDF is still written without the text
/// layer.</summary>
sealed class SystemFontResolver : PdfSharp.Fonts.IFontResolver
{
    public static readonly SystemFontResolver Instance = new();
    static readonly byte[]? FontBytes = Load();
    public static bool HasFont => FontBytes is not null;

    public byte[]? GetFont(string faceName) => FontBytes;

    public PdfSharp.Fonts.FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        => FontBytes is null ? null : new PdfSharp.Fonts.FontResolverInfo("SF");

    static byte[]? Load()
    {
        string[] paths =
        {
            @"C:\Windows\Fonts\arial.ttf", @"C:\Windows\Fonts\segoeui.ttf", @"C:\Windows\Fonts\calibri.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
            "/Library/Fonts/Arial.ttf", "/System/Library/Fonts/Supplemental/Arial.ttf",
        };
        foreach (var p in paths)
            try { if (File.Exists(p)) return File.ReadAllBytes(p); } catch { }
        return null;
    }
}
