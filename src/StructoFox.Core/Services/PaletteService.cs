using System.IO;
using System.Text.Json;
using StructoFox.Core.Models;

namespace StructoFox.Core;

/// <summary>
/// Loads, saves and lists colour palettes as portable JSON files, so users can curate their own
/// (incl. Corporate-Identity colours) and carry them between devices. Also supplies a built-in default.
/// </summary>
public static class PaletteService
{
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    // Palette files are plain JSON with this extension; one palette per file.
    public const string Extension = ".palette.json";

    /// <summary>The built-in starter palette: a greyscale ramp (incl. pure black &amp; white) plus a
    /// lightness ramp for each main hue. A broad, useful default — and a good stress test of the grid.</summary>
    public static ColorPalette BuiltIn()
    {
        var p = new ColorPalette { Name = "Standard" };

        // Greyscale ramp: pure black, 20/40/60/80% lightness, pure white.
        p.Colors.Add(new("Black", "#000000"));
        foreach (var l in new[] { 0.20, 0.40, 0.60, 0.80 })
            p.Colors.Add(new($"Gray {Pct(l)}%", HslHex(0, 0, l)));
        p.Colors.Add(new("White", "#FFFFFF"));

        // Per-hue lightness ramps (dark → light) at full-ish saturation.
        (string name, double h)[] hues =
        {
            ("Red", 0), ("Orange", 30), ("Yellow", 52), ("Green", 130), ("Blue", 215), ("Violet", 280),
        };
        foreach (var (name, h) in hues)
            foreach (var l in new[] { 0.30, 0.45, 0.60, 0.75, 0.88 })
                p.Colors.Add(new($"{name} {Pct(l)}%", HslHex(h, 0.85, l)));

        return p;
    }

    // Whole-percent label for a 0..1 lightness.
    static int Pct(double v) => (int)Math.Round(v * 100);

    // HSL→#RRGGBB (h in degrees, s/l in 0..1) — kept in Core (no UI types) so palettes stay portable.
    static string HslHex(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = l - c / 2;
        double r = 0, g = 0, b = 0;
        switch (((int)(h / 60)) % 6)
        {
            case 0: r = c; g = x; break;
            case 1: r = x; g = c; break;
            case 2: g = c; b = x; break;
            case 3: g = x; b = c; break;
            case 4: r = x; b = c; break;
            default: r = c; b = x; break;
        }
        byte B(double v) => (byte)Math.Round((v + m) * 255);
        return $"#{B(r):X2}{B(g):X2}{B(b):X2}";
    }

    /// <summary>Reads every <c>*.palette.json</c> in <paramref name="dir"/>; returns empty if the
    /// folder is missing. Unreadable files are skipped rather than aborting the whole load.</summary>
    public static List<ColorPalette> LoadAll(string dir)
    {
        var result = new List<ColorPalette>();
        if (!Directory.Exists(dir)) return result;
        foreach (var path in Directory.EnumerateFiles(dir, "*" + Extension))
        {
            var p = Load(path);
            if (p is not null) result.Add(p);
        }
        return result;
    }

    /// <summary>Loads one palette file, or null if it's missing or malformed.</summary>
    public static ColorPalette? Load(string path)
    {
        try { return JsonSerializer.Deserialize<ColorPalette>(File.ReadAllText(path), Opts); }
        catch { return null; }
    }

    /// <summary>Writes a palette to <paramref name="dir"/> as <c>&lt;Name&gt;.palette.json</c>,
    /// creating the folder if needed. Returns the path written.</summary>
    public static string Save(string dir, ColorPalette palette)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, SafeName(palette.Name) + Extension);
        File.WriteAllText(path, JsonSerializer.Serialize(palette, Opts));
        return path;
    }

    // Strips characters that can't live in a filename so a palette name is always saveable.
    static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "Palette" : name.Trim();
    }
}
