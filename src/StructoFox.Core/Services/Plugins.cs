namespace StructoFox.Core;

/// <summary>
/// A StructoFox plugin. Implement this in a class library that references StructoFox.Core, drop the built
/// .dll into the app's <c>Plugins/</c> folder, and the app loads it at startup and surfaces its commands.
/// A plugin is purely optional: leave the .dll out (e.g. a school omitting the AI code-generation plugin)
/// and the feature simply isn't there.
/// </summary>
public interface IStructoFoxPlugin
{
    /// <summary>Display name shown in the Extensions menu.</summary>
    string Name { get; }

    /// <summary>Plugin version string (free-form, e.g. "1.0").</summary>
    string Version { get; }

    /// <summary>The actions this plugin contributes; the host renders them as Extensions-menu items.</summary>
    IReadOnlyList<PluginCommand> Commands { get; }
}

/// <summary>One action a plugin contributes to the Extensions menu.</summary>
public sealed class PluginCommand
{
    /// <summary>Menu label.</summary>
    public required string Title { get; init; }

    /// <summary>Invoked when the user picks the command; gets the host context (project, UI helpers).</summary>
    public required Action<IPluginContext> Run { get; init; }
}

/// <summary>What the host gives a running command: the current project plus UI helpers, so a plugin can
/// show output or talk to the user WITHOUT StructoFox.Core having to reference any UI framework. The data
/// itself (functions, classes, diagrams) is read through the normal Core services using
/// <see cref="ProjectFolder"/>.</summary>
public interface IPluginContext
{
    /// <summary>The open project's folder, or null if none is open.</summary>
    string? ProjectFolder { get; }

    /// <summary>The host's current UI language code (e.g. "de", "en"), so a plugin can localize its own UI.</summary>
    string Language { get; }

    /// <summary>The host's main window as an opaque handle (an Avalonia <c>Window</c>), so a plugin that
    /// references a UI framework can parent and theme its own dialogs. Null if unavailable. Core stays UI-free
    /// by typing this as <see cref="object"/>; the plugin casts it.</summary>
    object? OwnerWindow { get; }

    /// <summary>Applies the host theme (merged resource dictionaries) to a plugin-created window, so plugin
    /// dialogs match the app. The argument is an Avalonia <c>Window</c>; no-op if the host can't theme it.</summary>
    void ApplyTheme(object window);

    /// <summary>Shows a read-only text panel to the user (e.g. generated code, a lookup result).</summary>
    void ShowText(string title, string content);

    /// <summary>Briefly notifies the user (status / confirmation), without blocking.</summary>
    void Notify(string message);
}
