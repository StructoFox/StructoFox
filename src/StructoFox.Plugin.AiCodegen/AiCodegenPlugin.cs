using StructoFox.Core;
using StructoFox.Core.Models;

namespace StructoFox.Plugin.AiCodegen;

/// <summary>
/// The KI-Codegen plugin: lets a configured AI model turn a StructoFox project (diagrams → code structure) into
/// a ready-to-build project, filling in the method bodies. Bundles StructoFox.AI, so a deployment WITHOUT this
/// plugin contains no AI code at all.
///
/// Contributes three commands to the host's Extensions menu:
///  • KI-Konfiguration   — manage model cards (provider, model, local server, self-description).
///  • API-Keys verwalten — store provider keys in the OS key store.
///  • Code generieren     — generate a buildable project from the open diagram with AI-filled bodies.
/// </summary>
public sealed class AiCodegenPlugin : IStructoFoxPlugin
{
    public string Name    => "KI-Codegen";
    public string Version => "1.0";

    // API-key management lives inside the configuration (a 🔑 button there), not as its own menu entry.
    // Built fresh each read so the titles reflect the current UI language.
    public IReadOnlyList<PluginCommand> Commands =>
    [
        new PluginCommand { Title = PluginLoc.T("cmd_generate"), Run = CodegenRunner.Run },
        new PluginCommand { Title = PluginLoc.T("cmd_config"),   Run = AiConfigWindow.Show },
    ];

    /// <summary>Loads every code entity of the project, keyed by id (mirrors the app's exporter gathering).</summary>
    internal static List<CodeEntity> GatherEntities(string projFolder)
    {
        var all = new Dictionary<string, CodeEntity>();
        foreach (var t in Enum.GetValues<CodeEntityType>())
            foreach (var ent in CodeEntityService.LoadAll(projFolder, t.ToString()))
                all[ent.Id] = ent;
        return all.Values.ToList();
    }
}
