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

        // Dress the app in a default OXSUIT theme before any window appears, so every
        // DynamicResource brush resolves and windows render with real colours.
        ThemeManager.ApplyDefault(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}