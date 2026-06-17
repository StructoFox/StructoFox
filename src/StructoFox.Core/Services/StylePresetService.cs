using System.IO;
using System.Text.Json;
using StructoFox.Core.Models;

namespace StructoFox.Core;

/// <summary>
/// Loads, saves and lists reusable style presets as portable JSON files, plus a set of built-in
/// black-on-white standards. Presets let users apply a whole look to an element in one click.
/// </summary>
public static class StylePresetService
{
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public const string Extension = ".stylepreset.json";

    /// <summary>The built-in standards: black on white at three border weights (thin / medium / thick).</summary>
    public static List<StylePreset> BuiltIn() =>
    [
        new("B/W Thin (1px)",   BlackOnWhite(1)),
        new("B/W Medium (3px)", BlackOnWhite(3)),
        new("B/W Thick (5px)",  BlackOnWhite(5)),
    ];

    // A black-on-white element style at the given border thickness — the classic norm-compliant look.
    static ElementStyle BlackOnWhite(double thickness) => new()
    {
        LineColor     = "#000000",
        FillColor     = "#FFFFFF",
        TextColor     = "#000000",
        LineThickness = thickness,
    };

    /// <summary>Reads every saved preset file in <paramref name="dir"/>; empty if the folder is missing.</summary>
    public static List<StylePreset> LoadAll(string dir)
    {
        var result = new List<StylePreset>();
        if (!Directory.Exists(dir)) return result;
        foreach (var path in Directory.EnumerateFiles(dir, "*" + Extension))
            if (Load(path) is { } p) result.Add(p);
        return result;
    }

    /// <summary>Loads one preset file, or null if it's missing or malformed.</summary>
    public static StylePreset? Load(string path)
    {
        try { return JsonSerializer.Deserialize<StylePreset>(File.ReadAllText(path), Opts); }
        catch { return null; }
    }

    /// <summary>Writes a preset to <paramref name="dir"/> as <c>&lt;Name&gt;.stylepreset.json</c>; returns the path.</summary>
    public static string Save(string dir, StylePreset preset)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, SafeName(preset.Name) + Extension);
        File.WriteAllText(path, JsonSerializer.Serialize(preset, Opts));
        return path;
    }

    // Strips characters that can't appear in a filename so any preset name is saveable.
    static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "Preset" : name.Trim();
    }
}
