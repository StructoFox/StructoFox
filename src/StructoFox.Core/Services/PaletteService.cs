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

    /// <summary>The built-in starter palette: neutral ink/paper tones plus a clear accent set.
    /// A sane default that's also a template for users building their own.</summary>
    public static ColorPalette BuiltIn() => new()
    {
        Name = "Standard",
        Colors =
        [
            new("Ink",       "#1C1C1C"),
            new("Paper",     "#FFFFFF"),
            new("Slate",     "#5A5A5A"),
            new("Mist",      "#E0E0E0"),
            new("Red",       "#D32F2F"),
            new("Orange",    "#F57F17"),
            new("Amber",     "#FFB300"),
            new("Green",     "#388E3C"),
            new("Teal",      "#00897B"),
            new("Blue",      "#1976D2"),
            new("Indigo",    "#3949AB"),
            new("Purple",    "#8E24AA"),
        ],
    };

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
