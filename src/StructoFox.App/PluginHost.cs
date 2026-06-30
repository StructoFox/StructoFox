using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using StructoFox.Core;

namespace StructoFox.App;

/// <summary>
/// Discovers and loads optional plugins from the app's <c>Plugins/</c> folder (next to the executable),
/// mirroring how <see cref="ThemeManager"/> loads <c>Themes/</c>. Each plugin .dll references StructoFox.Core
/// and implements <see cref="IStructoFoxPlugin"/>. A folder that's missing or empty just means no plugins —
/// e.g. a school deploying without the AI code-generation plugin. A broken plugin is skipped, never fatal.
/// </summary>
public static class PluginHost
{
    public static string PluginsDir { get; } = Path.Combine(AppContext.BaseDirectory, "Plugins");

    static readonly List<IStructoFoxPlugin> _plugins = new();
    public static IReadOnlyList<IStructoFoxPlugin> Plugins => _plugins;

    /// <summary>Loads every plugin .dll found in <see cref="PluginsDir"/>. Call once at startup.</summary>
    public static void Load()
    {
        _plugins.Clear();
        if (!Directory.Exists(PluginsDir)) return;

        foreach (var dll in Directory.EnumerateFiles(PluginsDir, "*.dll"))
        {
            try
            {
                var asm = new PluginLoadContext(dll).LoadFromAssemblyPath(dll);
                foreach (var t in asm.GetTypes())
                    if (!t.IsAbstract && typeof(IStructoFoxPlugin).IsAssignableFrom(t)
                        && t.GetConstructor(Type.EmptyTypes) is not null)
                        _plugins.Add((IStructoFoxPlugin)Activator.CreateInstance(t)!);
            }
            catch
            {
                // A bad / incompatible plugin must never take the app down — just skip it.
            }
        }
    }
}

/// <summary>Loads a plugin and its private dependencies, but resolves SHARED assemblies (StructoFox.Core and
/// anything already loaded by the app) from the host — so the plugin's <see cref="IStructoFoxPlugin"/> is the
/// very same type the host knows.</summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath) : base(isCollectible: false)
        => _resolver = new AssemblyDependencyResolver(pluginPath);

    protected override Assembly? Load(AssemblyName name)
    {
        // Already loaded by the host (Core, Avalonia, BCL…) → share it, so types match across the boundary.
        if (Default.Assemblies.Any(a => a.GetName().Name == name.Name)) return null;
        var path = _resolver.ResolveAssemblyToPath(name);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }
}
