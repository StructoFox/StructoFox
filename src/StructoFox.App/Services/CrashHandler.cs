using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace StructoFox.App;

/// <summary>
/// The app's safety net: logs unhandled exceptions to a crash file and shows a readable error dialog
/// instead of dying silently. <see cref="Safe"/>/<see cref="SafeAsync"/> wrap risky UI actions so a
/// throw surfaces an error and keeps the app alive.
/// </summary>
public static class CrashHandler
{
    static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StructoFox");
    static string LogPath => Path.Combine(Dir, "crash.log");

    /// <summary>Hooks the process-wide unhandled-exception sources. Call once at startup.</summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception, "AppDomain (fatal)");
        TaskScheduler.UnobservedTaskException     += (_, e) => { Report(e.Exception, "Task"); e.SetObserved(); };
    }

    /// <summary>Runs an action, reporting any exception instead of letting it crash the app.</summary>
    public static void Safe(Action action, string where)
    {
        try { action(); } catch (Exception ex) { Report(ex, where); }
    }

    /// <summary>Awaits an async action, reporting any exception instead of crashing.</summary>
    public static async Task SafeAsync(Func<Task> action, string where)
    {
        try { await action(); } catch (Exception ex) { Report(ex, where); }
    }

    /// <summary>Appends an exception to the crash log and shows an error dialog (on the UI thread).</summary>
    public static void Report(Exception? ex, string where)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(LogPath, $"{new string('-', 60)}\n[{DateTime.Now:u}] {where}\n{ex}\n");
        }
        catch { /* logging is best-effort */ }

        try
        {
            if (Dispatcher.UIThread.CheckAccess()) ShowError(ex, where);
            else Dispatcher.UIThread.Post(() => ShowError(ex, where));
        }
        catch { /* can't show UI (e.g. terminating) — the log still has it */ }
    }

    // Pops up a themed, non-modal error window with the message + where to find full details.
    static void ShowError(Exception ex, string where)
    {
        var win = new Window
        {
            Title = "StructoFox — error",
            Width = 560, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        Ui.ThemeWindow(win);

        var msg  = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.Bold };
        var kind = new TextBlock { Text = $"{ex.GetType().Name}  ·  {where}", FontSize = 11, Opacity = 0.7 };
        var hint = new TextBlock { Text = "Full details were written to:\n" + LogPath, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        var ok   = Ui.Btn("OK"); ok.HorizontalAlignment = HorizontalAlignment.Right; ok.Click += (_, _) => win.Close();

        win.Content = new StackPanel { Margin = new(18), Spacing = 10, Children = { msg, kind, hint, ok } };
        win.Show();
    }
}
