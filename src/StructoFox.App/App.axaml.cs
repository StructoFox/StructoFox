using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace StructoFox.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Load translations (and export the English/German starter files) before any UI is built.
        Loc.Init();

        // Discover optional plugins from the Plugins/ folder (none = fine, e.g. a school build without them).
        PluginHost.Load();

        // Dress the app in a default OXSUIT theme before any window appears, so every
        // DynamicResource brush resolves and windows render with real colours.
        ThemeManager.ApplyDefault(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // A project-folder argument (positional path, or `--project <path>`) launches straight into that
            // project's cockpit in embedded mode (e.g. when started from ClaudetRelay).
            desktop.MainWindow = new MainWindow(ProjectPathArg(desktop.Args));
        }

        base.OnFrameworkInitializationCompleted();
    }

    // The project folder to open on launch: the value after `--project`, else the first non-switch argument.
    // Null when no path was given (→ normal home browser).
    static string? ProjectPathArg(string[]? args)
    {
        if (args is null) return null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--project" && i + 1 < args.Length) return args[i + 1];
            if (!args[i].StartsWith("--")) return args[i];
        }
        return null;
    }
}