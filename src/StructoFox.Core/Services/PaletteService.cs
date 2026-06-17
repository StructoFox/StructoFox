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
            new("Black", "#000000"), new("Dim Gray", "#696969"), new("Gray", "#808080"), new("Dark Gray", "#A9A9A9"),
            new("Silver", "#C0C0C0"), new("Light Gray", "#D3D3D3"), new("Gainsboro", "#DCDCDC"),
            new("Slate Gray", "#708090"), new("Light Slate Gray", "#778899"), new("White", "#FFFFFF"),
            // Reds
            new("Dark Red", "#8B0000"), new("Maroon", "#800000"), new("Firebrick", "#B22222"), new("Crimson", "#DC143C"),
            new("Red", "#FF0000"), new("Indian Red", "#CD5C5C"), new("Light Coral", "#F08080"), new("Tomato", "#FF6347"),
            // Pinks
            new("Salmon", "#FA8072"), new("Deep Pink", "#FF1493"), new("Hot Pink", "#FF69B4"),
            new("Pale Violet Red", "#DB7093"), new("Pink", "#FFC0CB"), new("Light Pink", "#FFB6C1"), new("Misty Rose", "#FFE4E1"),
            // Oranges & golds
            new("Orange Red", "#FF4500"), new("Coral", "#FF7F50"), new("Dark Orange", "#FF8C00"),
            new("Orange", "#FFA500"), new("Gold", "#FFD700"), new("Goldenrod", "#DAA520"),
            // Browns
            new("Saddle Brown", "#8B4513"), new("Sienna", "#A0522D"), new("Brown", "#A52A2A"), new("Chocolate", "#D2691E"),
            new("Peru", "#CD853F"), new("Rosy Brown", "#BC8F8F"), new("Tan", "#D2B48C"), new("Wheat", "#F5DEB3"),
            // Yellows
            new("Dark Khaki", "#BDB76B"), new("Khaki", "#F0E68C"), new("Pale Goldenrod", "#EEE8AA"),
            new("Yellow", "#FFFF00"), new("Light Yellow", "#FFFFE0"),
            // Greens
            new("Dark Olive Green", "#556B2F"), new("Olive", "#808000"), new("Olive Drab", "#6B8E23"),
            new("Yellow Green", "#9ACD32"), new("Chartreuse", "#7FFF00"), new("Lime Green", "#32CD32"),
            new("Green", "#008000"), new("Forest Green", "#228B22"), new("Sea Green", "#2E8B57"),
            new("Medium Sea Green", "#3CB371"), new("Spring Green", "#00FF7F"),
            // Cyans / teals
            new("Teal", "#008080"), new("Dark Cyan", "#008B8B"), new("Light Sea Green", "#20B2AA"),
            new("Turquoise", "#40E0D0"), new("Aquamarine", "#7FFFD4"), new("Aqua", "#00FFFF"),
            new("Cadet Blue", "#5F9EA0"), new("Pale Turquoise", "#AFEEEE"),
            // Blues
            new("Steel Blue", "#4682B4"), new("Sky Blue", "#87CEEB"), new("Light Blue", "#ADD8E6"),
            new("Deep Sky Blue", "#00BFFF"), new("Dodger Blue", "#1E90FF"), new("Cornflower Blue", "#6495ED"),
            new("Royal Blue", "#4169E1"), new("Blue", "#0000FF"), new("Medium Blue", "#0000CD"),
            new("Navy", "#000080"), new("Midnight Blue", "#191970"),
            // Purples
            new("Slate Blue", "#6A5ACD"), new("Medium Slate Blue", "#7B68EE"), new("Dark Slate Blue", "#483D8B"),
            new("Indigo", "#4B0082"), new("Blue Violet", "#8A2BE2"), new("Medium Purple", "#9370DB"),
            new("Dark Violet", "#9400D3"), new("Dark Orchid", "#9932CC"), new("Purple", "#800080"),
            new("Medium Orchid", "#BA55D3"), new("Orchid", "#DA70D6"), new("Violet", "#EE82EE"),
            new("Plum", "#DDA0DD"), new("Thistle", "#D8BFD8"), new("Magenta", "#FF00FF"), new("Medium Violet Red", "#C71585"),
            // Soft neutrals / off-whites
            new("Lavender", "#E6E6FA"), new("Beige", "#F5F5DC"), new("Ivory", "#FFFFF0"), new("Snow", "#FFFAFA"),
            new("Linen", "#FAF0E6"), new("Azure", "#F0FFFF"), new("Honeydew", "#F0FFF0"), new("Mint Cream", "#F5FFFA"),
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
