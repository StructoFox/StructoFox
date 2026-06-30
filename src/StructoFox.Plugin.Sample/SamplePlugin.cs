using StructoFox.Core;

namespace StructoFox.Plugin.Sample;

/// <summary>A minimal example plugin: proves the host loads external .dlls and that a command can read the
/// project (via the normal Core services) and show a result. Replace with the real AI / codegen plugins.</summary>
public sealed class SamplePlugin : IStructoFoxPlugin
{
    public string Name => "Sample";
    public string Version => "1.0";

    public IReadOnlyList<PluginCommand> Commands { get; } = new[]
    {
        new PluginCommand
        {
            Title = "List functions (sample plugin)",
            Run = ctx =>
            {
                if (ctx.ProjectFolder is null) { ctx.Notify("No project open."); return; }
                var fns = CodeEntityService.LoadAll(ctx.ProjectFolder, "Function").ToList();
                ctx.ShowText("Functions",
                    fns.Count == 0 ? "(none)" : string.Join("\n", fns.Select(f => "• " + f.Name)));
            },
        },
    };
}
