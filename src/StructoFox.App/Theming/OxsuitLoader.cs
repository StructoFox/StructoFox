// OXSUIT 1.0 — Avalonia Loader
// Loads an .oxsuit file into an Avalonia ResourceDictionary.
//
// Color conversion:
//   OXSUIT uses web-standard #RRGGBB / #RRGGBBAA (alpha last).
//   Avalonia's Color.FromArgb expects alpha FIRST — this loader reorders transparently.
//   (Avalonia, unlike WPF, uses the same channel order internally, but the parse step is identical.)
//
// Resource key mapping:
//   OXSUIT key                 → Avalonia resource key
//   "ContentBg"                → "ContentBgBrush"                  (SolidColorBrush)
//   "CornerRadius"             → "OxsuitCornerRadius"               (CornerRadius)
//   "ShadowDepth"              → "OxsuitShadowDepth"                (double)
//   "ContentBorderWidth"       → "OxsuitContentBorderWidth"         (double)
//                              → "OxsuitContentBorderThickness"     (Thickness)
//   …same for Sidebar/Control/Input/Primary/Secondary/Tertiary border widths.

using System;
using System.Linq;
using System.Xml.Linq;
using Avalonia;            // CornerRadius, Thickness
using Avalonia.Controls;   // ResourceDictionary
using Avalonia.Media;      // Color, Colors, SolidColorBrush

namespace OXSUIT.Loaders.Avalonia;

public static class OxsuitLoader
{
    /// <summary>
    /// Loads an .oxsuit file and returns an Avalonia ResourceDictionary,
    /// ready to be merged into Application.Current.Resources or a control's resources.
    /// </summary>
    /// <param name="path">Full path to the .oxsuit file.</param>
    /// <param name="appExtension">
    ///   Optional app name to also load a matching &lt;extensions app="…"&gt; block.
    /// </param>
    public static ResourceDictionary Load(string path, string? appExtension = null) =>
        Build(XDocument.Load(path), appExtension);

    /// <summary>
    /// Same as <see cref="Load"/> but reads the theme from an in-memory XML string
    /// instead of a file — handy for embedded themes and tests.
    /// </summary>
    public static ResourceDictionary LoadXml(string xml, string? appExtension = null) =>
        Build(XDocument.Parse(xml), appExtension);

    // Walks the parsed document and fills a ResourceDictionary with colours, tokens and extensions.
    // The single place all the actual loading happens — Load/LoadXml are just the two front doors.
    static ResourceDictionary Build(XDocument xml, string? appExtension)
    {
        var root = xml.Root ?? throw new InvalidOperationException("Invalid OXSUIT file — missing root element.");
        var rd   = new ResourceDictionary();

        // ── Core colours ──────────────────────────────────────────────────────────
        foreach (var el in root.Element("colors")?.Elements("color") ?? Enumerable.Empty<XElement>())
            AddColor(rd, el);

        // ── Geometry tokens ─────────────────────────────────────────────────────────
        foreach (var el in root.Element("tokens")?.Elements("token") ?? Enumerable.Empty<XElement>())
            AddToken(rd, el);

        // ── App-specific extensions (only the requested app's block) ─────────────────
        if (appExtension != null)
        {
            var ext = root.Elements("extensions").FirstOrDefault(e =>
                string.Equals(e.Attribute("app")?.Value, appExtension, StringComparison.OrdinalIgnoreCase));
            foreach (var el in ext?.Elements("color") ?? Enumerable.Empty<XElement>())
                AddColor(rd, el);
        }

        return rd;
    }

    // Adds one <color key value> as a SolidColorBrush under "<key>Brush".
    static void AddColor(ResourceDictionary rd, XElement el)
    {
        var key   = el.Attribute("key")?.Value;
        var value = el.Attribute("value")?.Value;
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value)) return;

        rd[key + "Brush"] = new SolidColorBrush(ParseWebColor(value));
    }

    // Adds one <token key value> as the matching geometry resource(s); ignores unknown keys.
    static void AddToken(ResourceDictionary rd, XElement el)
    {
        var key   = el.Attribute("key")?.Value;
        var value = el.Attribute("value")?.Value;
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value)) return;

        if (!double.TryParse(value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d)) return;

        switch (key)
        {
            case "CornerRadius":
                rd["OxsuitCornerRadius"] = new CornerRadius(d);
                break;

            case "ShadowDepth":
                rd["OxsuitShadowDepth"] = d;
                break;

            // Per-surface border widths: emit both a double and a Thickness so bindings
            // can grab whichever type their target property expects.
            case "ContentBorderWidth":
            case "SidebarBorderWidth":
            case "ControlBorderWidth":
            case "InputBorderWidth":
            case "PrimaryBorderWidth":
            case "SecondaryBorderWidth":
            case "TertiaryBorderWidth":
                rd[$"Oxsuit{key}"]                               = d;
                rd[$"Oxsuit{key.Replace("Width", "Thickness")}"] = new Thickness(d);
                break;
        }
    }

    /// <summary>
    /// Parses a web-standard hex colour (#RGB, #RRGGBB, or #RRGGBBAA with alpha LAST)
    /// into an Avalonia Color. A bad value comes back as magenta — loud on purpose.
    /// </summary>
    static Color ParseWebColor(string hex)
    {
        hex = hex.TrimStart('#');
        return hex.Length switch
        {
            3 => Color.FromRgb(
                    Convert.ToByte(new string(hex[0], 2), 16),
                    Convert.ToByte(new string(hex[1], 2), 16),
                    Convert.ToByte(new string(hex[2], 2), 16)),

            6 => Color.FromRgb(
                    Convert.ToByte(hex[0..2], 16),
                    Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16)),

            8 => Color.FromArgb(                 // OXSUIT: RRGGBBAA → Avalonia wants alpha first
                    Convert.ToByte(hex[6..8], 16),   // alpha is LAST in web format
                    Convert.ToByte(hex[0..2], 16),
                    Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16)),

            _ => Colors.Magenta,                 // fallback — signals a bad value
        };
    }
}
