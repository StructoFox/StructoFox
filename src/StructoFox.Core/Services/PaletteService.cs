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

    /// <summary>All built-in palettes offered everywhere: the basic <b>Standard</b> set of common
    /// named colours, and a systematic <b>Office</b> set (grey ramp + per-primary tint ramps).</summary>
    public static List<ColorPalette> BuiltIns() => [Standard(), Office()];

    /// <summary>The default built-in palette (Standard) — kept for callers that want a single fallback.</summary>
    public static ColorPalette BuiltIn() => Standard();

    // The "Standard" palette: a couple of rows of common, simple named colours.
    static ColorPalette Standard() => new()
    {
        Name = "Standard",
        Colors =
        [
            new("Black",   "#000000"), new("White",  "#FFFFFF"), new("Gray",    "#808080"), new("Silver", "#C0C0C0"),
            new("Red",     "#E53935"), new("Orange", "#FB8C00"), new("Banana",  "#FFE135"), new("Yellow", "#FDD835"),
            new("Lime",    "#C0CA33"), new("Green",  "#43A047"), new("Teal",    "#00897B"), new("Cyan",   "#00ACC1"),
            new("Blue",    "#1E88E5"), new("Navy",   "#283593"), new("Violet",  "#8E24AA"), new("Purple", "#6A1B9A"),
            new("Magenta", "#D81B60"), new("Pink",   "#FF8DA1"), new("Brown",   "#6D4C41"), new("Beige",  "#D7CCC8"),
        ],
    };

    // The "Office" palette: a black→white grey ramp, then a 100→20% tint ramp for each primary.
    static ColorPalette Office()
    {
        var p = new ColorPalette { Name = "Office" };

        p.Colors.Add(new("Black", "#000000"));
        foreach (var pct in new[] { 80, 60, 40, 20 })  // "X% grey" = X% black
            p.Colors.Add(new($"Gray {pct}%", GrayHex(pct)));
        p.Colors.Add(new("White", "#FFFFFF"));

        (string name, string hex)[] primaries =
        {
            ("Red", "#FF0000"), ("Yellow", "#FFD400"), ("Green", "#00A000"),
            ("Blue", "#0070FF"), ("Violet", "#8000FF"), ("Orange", "#FF8000"),
        };
        foreach (var (name, hex) in primaries)
            foreach (var pct in new[] { 100, 80, 60, 40, 20 })  // 100% = pure, 20% = pale (mixed with white)
                p.Colors.Add(new($"{name} {pct}%", MixWhite(hex, pct)));

        return p;
    }

    // "X% grey" → lightness (100-X)%, as #RRGGBB.
    static string GrayHex(int grayPct)
    {
        int v = (int)Math.Round(255 * (100 - grayPct) / 100.0);
        return $"#{v:X2}{v:X2}{v:X2}";
    }

    // Mixes a base colour with white: pct=100 → pure base, pct=20 → mostly white.
    static string MixWhite(string baseHex, int pct)
    {
        var (r, g, b) = Rgb(baseHex);
        double t = pct / 100.0;
        byte M(int ch) => (byte)Math.Round(ch * t + 255 * (1 - t));
        return $"#{M(r):X2}{M(g):X2}{M(b):X2}";
    }

    // Parses #RRGGBB into integer channels.
    static (int r, int g, int b) Rgb(string hex)
    {
        hex = hex.TrimStart('#');
        return (Convert.ToInt32(hex[..2], 16), Convert.ToInt32(hex[2..4], 16), Convert.ToInt32(hex[4..6], 16));
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
