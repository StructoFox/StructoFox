using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using System;

namespace StructoFox.App;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        CrashHandler.Install();
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception ex) { CrashHandler.Report(ex, "Startup"); }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            // Pin a deterministic colour-emoji fallback chain so icon glyphs render the same way on
            // every layout pass — otherwise Avalonia's fallback resolution can flip emoji between the
            // colour glyph and a monochrome one (inheriting the themed text colour) after a re-render.
            .With(new FontManagerOptions
            {
                FontFallbacks = new[]
                {
                    new FontFallback { FontFamily = new FontFamily("Segoe UI Emoji") },
                    new FontFallback { FontFamily = new FontFamily("Apple Color Emoji") },
                    new FontFallback { FontFamily = new FontFamily("Noto Color Emoji") },
                },
            })
            .LogToTrace();
}
