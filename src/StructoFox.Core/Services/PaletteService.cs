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

    // The "Standard" palette: a curated set of well-known, named standard colours (CSS/X11),
    // spanning every hue area — the kind of names everyone recognises (Crimson, Navy, Teal…).
    static ColorPalette Standard() => new()
    {
        Name = "Standard",
        Colors =
        [
            // Neutrals
            new("Black", "#000000"), new("Gray", "#808080"), new("Silver", "#C0C0C0"), new("White", "#FFFFFF"),
            // Reds
            new("Crimson", "#DC143C"), new("Firebrick", "#B22222"), new("Maroon", "#800000"), new("Tomato", "#FF6347"),
            // Pinks
            new("Salmon", "#FA8072"), new("Pink", "#FFC0CB"), new("Hot Pink", "#FF69B4"),
            // Oranges & golds
            new("Coral", "#FF7F50"), new("Orange", "#FFA500"), new("Gold", "#FFD700"),
            // Yellows
            new("Yellow", "#FFFF00"), new("Khaki", "#F0E68C"),
            // Browns
            new("Tan", "#D2B48C"), new("Sienna", "#A0522D"), new("Chocolate", "#D2691E"), new("Brown", "#A52A2A"),
            // Greens
            new("Olive", "#808000"), new("Olive Drab", "#6B8E23"), new("Lime Green", "#32CD32"),
            new("Green", "#008000"), new("Forest Green", "#228B22"), new("Sea Green", "#2E8B57"),
            // Cyans / teals
            new("Teal", "#008080"), new("Turquoise", "#40E0D0"), new("Aqua", "#00FFFF"),
            // Blues
            new("Sky Blue", "#87CEEB"), new("Steel Blue", "#4682B4"), new("Royal Blue", "#4169E1"),
            new("Blue", "#0000FF"), new("Navy", "#000080"), new("Midnight Blue", "#191970"),
            // Purples
            new("Slate Blue", "#6A5ACD"), new("Indigo", "#4B0082"), new("Violet", "#EE82EE"),
            new("Purple", "#800080"), new("Magenta", "#FF00FF"), new("Orchid", "#DA70D6"),
            // Soft neutrals
            new("Lavender", "#E6E6FA"), new("Beige", "#F5F5DC"),
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
            ("Red",    "#FF0000"), ("Orange",  "#FF8000"), ("Amber",  "#FFB300"), ("Yellow", "#FFD400"),
            ("Lime",   "#99CC00"), ("Green",   "#00A000"), ("Teal",   "#009688"), ("Cyan",   "#00B8D4"),
            ("Blue",   "#0070FF"), ("Indigo",  "#3F51B5"), ("Violet", "#8000FF"), ("Magenta", "#D500A0"),
            ("Pink",   "#FF4081"), ("Brown",   "#8B4513"),
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
