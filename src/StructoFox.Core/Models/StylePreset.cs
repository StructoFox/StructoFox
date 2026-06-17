namespace StructoFox.Core.Models;

/// <summary>A named, reusable element-appearance "slot" — line/fill/text colour + thickness bundled
/// under a label. Saveable and shareable like palettes, so a look can be applied with one click.</summary>
public class StylePreset
{
    public string       Name  { get; set; } = "Preset";
    public ElementStyle Style { get; set; } = new();

    public StylePreset() { }

    // Convenience ctor so the built-in presets read as one tidy line each.
    public StylePreset(string name, ElementStyle style) { Name = name; Style = style; }
}
