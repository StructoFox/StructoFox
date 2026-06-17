namespace StructoFox.Core.Models;

/// <summary>A single named colour — a human label plus a web-hex value (#RRGGBB[AA]).
/// Names matter for Corporate-Identity palettes, where "Brand Blue" reads better than "#0A4DA3".</summary>
public class NamedColor
{
    public string Name  { get; set; } = "Colour";
    public string Value { get; set; } = "#000000";

    public NamedColor() { }

    // Convenience ctor so building the built-in palette stays a one-liner per entry.
    public NamedColor(string name, string value) { Name = name; Value = value; }
}

/// <summary>A named set of colours the user can pick from, edit, extend, save and share across
/// devices as a palette file. Diagrams reference colours by value; palettes are the curated source.</summary>
public class ColorPalette
{
    public string           Name   { get; set; } = "Palette";
    public List<NamedColor> Colors { get; set; } = [];
}
