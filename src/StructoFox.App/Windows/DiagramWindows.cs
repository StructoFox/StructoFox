using Avalonia.Controls;

namespace StructoFox.App;

/// <summary>
/// Tracks the one open editor window per diagram, so a second open of the same diagram (e.g. a double-click)
/// just brings the existing window to the front instead of spawning a duplicate. One window per key.
/// </summary>
public static class DiagramWindows
{
    static readonly Dictionary<string, Window> _open = new();

    /// <summary>Opens the diagram identified by <paramref name="id"/>, or activates its window if it's
    /// already open. <paramref name="create"/> builds the window only when one isn't open yet.</summary>
    public static void OpenOrActivate(string id, Func<Window> create)
    {
        if (_open.TryGetValue(id, out var existing))
        {
            existing.Activate();   // bring the already-open window to the front
            return;
        }
        var win = create();
        _open[id] = win;
        win.Closed += (_, _) => { if (_open.TryGetValue(id, out var w) && ReferenceEquals(w, win)) _open.Remove(id); };
        win.Show();
    }

    // Stable id for each diagram kind (folder + key/board id), so the same diagram maps to one window.
    public static string FlowId(string projFolder, string key)   => $"flow:{projFolder}:{key}";
    public static string StructId(string projFolder, string key) => $"struct:{projFolder}:{key}";
    public static string BoardId(string projFolder, string boardId) => $"board:{projFolder}:{boardId}";
}
