using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Reactive;
using Avalonia.Threading;

namespace StructoFox.App;

/// <summary>A combo entry that shows <see cref="Name"/> but quietly carries an opaque <see cref="Id"/>.
/// The label the user reads and the value the code keeps, travelling together.</summary>
public sealed record ComboItem(string Name, string Id)
{
    // The drop-down shows this; Avalonia renders plain items via their ToString().
    public override string ToString() => Name;
}

/// <summary>
/// Small factory of consistently-styled controls. The shared toolbox every window dips into,
/// so buttons and friends look the same whether they live on a board, a flowchart, or a dialog.
/// </summary>
public static class Ui
{
    /// <summary>
    /// Runs <paramref name="onChanged"/> whenever a control's layout bounds change — but ALWAYS DEFERRED (posted at
    /// Render priority), so it never executes inside Avalonia's layout/arrange pass.
    ///
    /// ── WHY THIS EXISTS / THE RULE ──────────────────────────────────────────────────────────────────────────────
    /// NEVER add or remove visual-tree children (Panel/Canvas <c>Children.Add/Remove</c>, re-rendering visuals, etc.)
    /// directly inside a Bounds / LayoutUpdated / PropertyChanged(BoundsProperty) callback. While arranging, Avalonia
    /// is ENUMERATING those children — mutating the collection then throws
    /// "Collection was modified; enumeration operation may not execute" (and repeatedly re-invalidating layout from
    /// such a callback can spin into a hang). Symptoms show up as a crash/hang during <c>Window.Show</c>.
    ///
    /// Safe INLINE in a bounds callback: only REPOSITION existing elements (<c>Canvas.SetLeft/Top</c>, setting a
    /// Width/Height) — that does not change the Children collection. Anything that ADDS/REMOVES children, or rebuilds
    /// a set of visuals, must be DEFERRED — which is exactly what this helper does. Prefer it over subscribing to
    /// <c>BoundsProperty</c> by hand. (Pure redraw requests like <c>InvalidateVisual()</c> are also fine inline.)
    /// </summary>
    public static IDisposable OnBoundsChanged(Visual target, Action onChanged) =>
        target.GetObservable(Visual.BoundsProperty).Subscribe(new AnonymousObserver<Rect>(_ =>
            Dispatcher.UIThread.Post(() => { try { onChanged(); } catch { /* never let a layout callback crash the UI */ } },
                                     DispatcherPriority.Render)));

    /// <summary>
    /// Builds a standard push button with uniform padding and an optional tooltip.
    /// One button to rule them all — or at least to look the same everywhere.
    /// </summary>
    public static Button Btn(string text, string? tooltip = null)
    {
        var b = new Button
        {
            Content = text,
            Padding = new(12, 6),
            CornerRadius = new(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        if (!string.IsNullOrEmpty(tooltip)) ToolTip.SetTip(b, tooltip);
        // Tie buttons to the OXSUIT theme so their label stays readable on any themed surface
        // (Fluent's own button foreground doesn't follow our inherited window text colour).
        Theme(b, TemplatedControl.BackgroundProperty, "ControlBgBrush");
        Theme(b, TemplatedControl.ForegroundProperty, "SidebarTextBrush");
        return b;
    }

    /// <summary>
    /// Builds a themed combo box wired to the OXSUIT brushes. Pass a width to fix it,
    /// or leave it to stretch. Fill it with <see cref="ComboItem"/>s and read SelectedItem back.
    /// </summary>
    public static ComboBox Combo(double width = 0)
    {
        var c = new ComboBox { MinHeight = 32, CornerRadius = new(4), FontSize = 13 };
        if (width > 0) c.Width = width;
        else c.HorizontalAlignment = HorizontalAlignment.Stretch;

        // Pull colours from the active OXSUIT theme; a theme swap restyles the combo live.
        Theme(c, TemplatedControl.BackgroundProperty,  "ControlBgBrush");
        Theme(c, TemplatedControl.ForegroundProperty,  "SidebarTextBrush");
        Theme(c, TemplatedControl.BorderBrushProperty, "ControlBorderBrush");
        return c;
    }

    /// <summary>Themes a whole window: surface background AND an inherited text colour, so chrome text
    /// always stays readable on the OXSUIT background (TextElement.Foreground cascades to children).</summary>
    public static void ThemeWindow(Window w)
    {
        Theme(w, TemplatedControl.BackgroundProperty, "ContentBgBrush");
        Theme(w, TextElement.ForegroundProperty,      "ContentTextBrush");
        if (AppIcon() is { } icon) w.Icon = icon;   // taskbar + title-bar icon, every window
    }

    // The embedded app logo, decoded once and shared (taskbar icon + the in-app title-bar brand).
    static readonly Uri AppIconUri = new("avares://StructoFox.App/Assets/appicon.png");

    static WindowIcon? _appIcon;
    static WindowIcon? AppIcon()
    {
        if (_appIcon is not null) return _appIcon;
        try { _appIcon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(AppIconUri)); }
        catch { /* no icon is fine */ }
        return _appIcon;
    }

    static Avalonia.Media.Imaging.Bitmap? _appLogo;
    /// <summary>The app logo as a bitmap, for placing in the themed title bar. Cached; null if missing.</summary>
    public static Avalonia.Media.Imaging.Bitmap? AppLogo()
    {
        if (_appLogo is not null) return _appLogo;
        try { _appLogo = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(AppIconUri)); }
        catch { /* no logo is fine */ }
        return _appLogo;
    }

    /// <summary>Binds a property to an OXSUIT theme brush via DynamicResource — present-theme or default,
    /// never crashing. Shared so windows and controls all tint from the same well.</summary>
    public static void Theme(Control c, AvaloniaProperty prop, string key) =>
        c.Bind(prop, new DynamicResourceExtension(key));

    /// <summary>
    /// A clickable colour chip (Border, not Button — so its hover never hides the colour). On hover
    /// it lifts with a chip-coloured corona; <paramref name="selected"/> gives it a thicker ring.
    /// </summary>
    public static Control ColorChip(string hex, string tooltip, Action onClick, bool selected = false, double size = 24)
    {
        var b = new Border
        {
            Width = size, Height = size, Margin = new(3), CornerRadius = new(3),
            BorderBrush = selected ? Brushes.Black : Brushes.Gray,
            BorderThickness = new(selected ? 3 : 1),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        try { b.Background = new SolidColorBrush(Color.Parse(hex)); } catch { b.Background = Brushes.Transparent; }
        ToolTip.SetTip(b, tooltip);

        b.PointerEntered += (_, _) => { try { b.BoxShadow = BoxShadows.Parse($"0 1 7 2 {hex}"); } catch { } b.ZIndex = 1; };
        b.PointerExited  += (_, _) => { b.BoxShadow = default; b.ZIndex = 0; };
        b.PointerPressed += (_, _) => onClick();
        return b;
    }
}
