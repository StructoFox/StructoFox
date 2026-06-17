using StructoFox.Core;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// Where the user's saved style presets live, and how the app gathers them: built-in standards
/// first, then any saved slots. Built-ins aren't written to disk — they're always offered.
/// </summary>
public static class PresetStore
{
    // Per-user, writable, easy to back up — preset slots are portable between machines.
    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StructoFox", "StylePresets");

    // The full list shown to the user: the black-on-white built-ins plus every saved slot.
    public static List<StylePreset> All()
    {
        var list = StylePresetService.BuiltIn();
        list.AddRange(StylePresetService.LoadAll(Dir));
        return list;
    }

    // Persists a preset slot to disk, returning the written path.
    public static string Save(StylePreset preset) => StylePresetService.Save(Dir, preset);
}
