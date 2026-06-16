using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Media;

namespace StructoFox.App;

/// <summary>
/// Loads a native OXSUIT 1.0 theme file (.oxsuit) into an Avalonia <see cref="ResourceDictionary"/>.
/// Avalonia port of ClaudetRelay's WPF loader: same <c>&lt;oxsuit&gt;</c> XML, same key map,
/// same <b>#RRGGBBAA</b> convention (alpha byte last) — just Avalonia brushes instead of WPF ones.
/// </summary>
public static class OxsuitLoader
{
    // OXSUIT 1.0 short key -> resource key. Kept identical to ClaudetRelay so themes port verbatim.
    static readonly Dictionary<string, string> s_keyMap =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Content surface
        ["ContentBg"]       = "ContentBgBrush",
        ["ContentBorder"]   = "ContentBorderBrush",
        ["ContentText"]     = "ContentTextBrush",
        ["ContentHigh"]     = "ContentHighBrush",
        ["ContentDim"]      = "ContentDimBrush",

        // Sidebar surface
        ["SidebarBg"]       = "SidebarBgBrush",
        ["SidebarBorder"]   = "SidebarBorderBrush",
        ["SidebarText"]     = "SidebarTextBrush",
        ["SidebarHigh"]     = "SidebarHighBrush",
        ["SidebarDim"]      = "SidebarDimBrush",

        // Control surface
        ["ControlBg"]       = "ControlBgBrush",
        ["ControlBorder"]   = "ControlBorderBrush",
        ["ControlText"]     = "ControlTextBrush",
        ["ControlHigh"]     = "ControlHighBrush",
        ["ControlDim"]      = "ControlDimBrush",
        ["ControlHover"]    = "ControlHoverBrush",

        // Input surface
        ["InputBg"]         = "InputBgBrush",
        ["InputBorder"]     = "InputBorderBrush",
        ["InputText"]       = "InputTextBrush",
        ["InputHigh"]       = "InputHighBrush",
        ["InputDim"]        = "InputDimBrush",

        // Accent
        ["AccentBg"]        = "AccentBgBrush",
        ["AccentText"]      = "AccentTextBrush",
        ["AccentHighlight"] = "AccentHighlightBrush",
        ["PrimaryAccent"]   = "PrimaryAccentBrush",
        ["SecondaryAccent"] = "SecondaryAccentBrush",
        ["TertiaryAccent"]  = "TertiaryAccentBrush",

        // Primary bubble slot ("Bg" -> "Bubble" is a historical ClaudetRelay naming quirk)
        ["PrimaryBg"]       = "PrimaryBubbleBrush",
        ["PrimaryBorder"]   = "PrimaryBubbleBorderBrush",
        ["PrimaryText"]     = "PrimaryTextBrush",
        ["PrimaryHigh"]     = "PrimaryHighBrush",
        ["PrimaryDim"]      = "PrimaryDimBrush",

        // Secondary bubble slot
        ["SecondaryBg"]     = "SecondaryBubbleBrush",
        ["SecondaryBorder"] = "SecondaryBubbleBorderBrush",
        ["SecondaryText"]   = "SecondaryTextBrush",
        ["SecondaryHigh"]   = "SecondaryHighBrush",
        ["SecondaryDim"]    = "SecondaryDimBrush",

        // Tertiary bubble slot
        ["TertiaryBg"]      = "TertiaryBubbleBrush",
        ["TertiaryBorder"]  = "TertiaryBubbleBorderBrush",
        ["TertiaryText"]    = "TertiaryTextBrush",
        ["TertiaryHigh"]    = "TertiaryHighBrush",
        ["TertiaryDim"]     = "TertiaryDimBrush",
    };

    /// <summary>
    /// Loads the .oxsuit file at <paramref name="path"/> into a ResourceDictionary.
    /// Returns null if the file is missing, unparseable, or holds no usable theme entries.
    /// </summary>
    public static ResourceDictionary? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try { return Parse(File.ReadAllText(path)); }
        catch { return null; }
    }

    /// <summary>
    /// Returns a friendly display name for an .oxsuit file: the root <c>name</c> attribute
    /// if present, otherwise the bare filename. A theme should introduce itself politely.
    /// </summary>
    public static string GetDisplayName(string path)
    {
        try
        {
            var name = XDocument.Load(path).Root?.Attribute("name")?.Value;
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch { /* fall through to filename */ }
        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>
    /// Parses OXSUIT v1.0 XML text into a ResourceDictionary of SolidColorBrushes.
    /// Colours are #RRGGBB or #RRGGBBAA (alpha LAST). Returns null if nothing usable was found.
    /// </summary>
    public static ResourceDictionary? Parse(string xmlText)
    {
        var root = XDocument.Parse(xmlText).Root;
        if (root?.Name.LocalName != "oxsuit") return null;

        var result = new ResourceDictionary();
        foreach (var el in root.Element("colors")?.Elements("color") ?? Enumerable.Empty<XElement>())
        {
            var oxKey = el.Attribute("key")?.Value;
            var value = el.Attribute("value")?.Value;
            if (string.IsNullOrWhiteSpace(oxKey) || string.IsNullOrWhiteSpace(value)) continue;

            try { result[MapKey(oxKey)] = new SolidColorBrush(ParseHex(value)); }
            catch { /* skip a single unparseable colour, keep the rest */ }
        }

        return result.Count > 0 ? ApplyFallbacks(result) : null;
    }

    // Maps an OXSUIT short key to its resource key; unknown keys just get a "Brush" suffix.
    static string MapKey(string oxKey) =>
        s_keyMap.TryGetValue(oxKey, out var key) ? key : oxKey + "Brush";

    // Fills in a few semantically-related brushes when a theme omits them, so partial themes still render.
    static ResourceDictionary ApplyFallbacks(ResourceDictionary d)
    {
        void Fallback(string missing, string source)
        {
            if (!d.ContainsKey(missing) && d.TryGetResource(source, null, out var v)) d[missing] = v;
        }
        Fallback("ControlTextBrush", "ContentTextBrush");
        Fallback("SidebarTextBrush", "ContentTextBrush");
        Fallback("InputBgBrush",     "ControlBgBrush");
        Fallback("InputTextBrush",   "ContentTextBrush");
        return d;
    }

    /// <summary>
    /// Parses a hex colour in OXSUIT format: #RGB, #RRGGBB, or #RRGGBBAA (alpha byte last).
    /// Anything else comes back as magenta — loud on purpose, so bad values are easy to spot.
    /// </summary>
    static Color ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        return hex.Length switch
        {
            3 => Color.FromRgb(B(new string(hex[0], 2)), B(new string(hex[1], 2)), B(new string(hex[2], 2))),
            6 => Color.FromRgb(B(hex[..2]), B(hex[2..4]), B(hex[4..6])),
            8 => Color.FromArgb(B(hex[6..8]), B(hex[..2]), B(hex[2..4]), B(hex[4..6])), // alpha last in OXSUIT
            _ => Colors.Magenta,
        };

        static byte B(string s) => Convert.ToByte(s, 16);
    }
}
