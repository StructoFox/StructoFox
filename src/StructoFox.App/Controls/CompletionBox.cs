using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using StructoFox.Core;

namespace StructoFox.App;

/// <summary>
/// A Visual-Studio-style completion dropdown attached to a <see cref="TextBox"/>. As the user types, it queries a
/// provider for suggestions and shows a themed popup list beneath the box; arrows navigate, Enter/Tab accept, Esc
/// dismisses. Accepting replaces the identifier at the caret; methods/functions insert "()" with the caret inside.
/// AI-free — the provider supplies the suggestions (see <see cref="CodeCompletionService"/>).
/// </summary>
public sealed class CompletionBox
{
    readonly TextBox _box;
    readonly Func<string, IReadOnlyList<Completion>> _provider;
    readonly Popup   _popup;
    readonly ListBox _list;
    bool _suppress;

    /// <summary>Attaches completion to <paramref name="box"/> using <paramref name="provider"/> (called with the
    /// text up to the caret). The instance lives as long as the box does.</summary>
    public static void Attach(TextBox box, Func<string, IReadOnlyList<Completion>> provider) => new CompletionBox(box, provider);

    CompletionBox(TextBox box, Func<string, IReadOnlyList<Completion>> provider)
    {
        _box = box;
        _provider = provider;

        _list = new ListBox { MaxHeight = 220, MinWidth = 240, ItemTemplate = ItemTemplate() };
        Ui.Theme(_list, TemplatedControl.BackgroundProperty, "InputBgBrush");
        Ui.Theme(_list, TemplatedControl.ForegroundProperty, "SidebarTextBrush");

        var border = new Border { Child = _list, BorderThickness = new(1), CornerRadius = new(3) };
        Ui.Theme(border, Border.BorderBrushProperty, "ControlBorderBrush");
        Ui.Theme(border, Border.BackgroundProperty, "InputBgBrush");

        _popup = new Popup
        {
            Child = border,
            PlacementTarget = box,
            Placement = PlacementMode.Bottom,
            // Must NOT take focus or the user can't keep typing — we dismiss it ourselves (Esc / lost focus / no
            // matches). Focusable false keeps the caret in the text box while the list updates live.
            IsLightDismissEnabled = false,
            Focusable = false,
        };
        ((ISetLogicalParent)_popup).SetParent(box);

        // A click in the list accepts that item.
        _list.PointerReleased += (_, _) => { if (_list.SelectedItem is Completion) Accept(); };

        box.TextChanged += (_, _) => { if (!_suppress) Refresh(); };
        // Tunnel so we intercept navigation/accept keys BEFORE the TextBox turns them into caret moves / newlines.
        box.AddHandler(InputElement.KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        box.LostFocus += (_, _) => Hide();
    }

    void Refresh()
    {
        var text  = _box.Text ?? "";
        var caret = Math.Clamp(_box.CaretIndex, 0, text.Length);
        var before = text[..caret];

        // Only offer completion while actually typing an identifier (or right after a '.').
        if (!EndsInToken(before)) { Hide(); return; }

        var items = _provider(before);
        if (items.Count == 0) { Hide(); return; }

        _list.ItemsSource = items;
        _list.SelectedIndex = 0;
        _popup.IsOpen = true;
    }

    void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_popup.IsOpen) return;
        switch (e.Key)
        {
            case Key.Down:   Move(+1); e.Handled = true; break;
            case Key.Up:     Move(-1); e.Handled = true; break;
            case Key.Enter:
            case Key.Tab:    Accept();  e.Handled = true; break;
            case Key.Escape: Hide();    e.Handled = true; break;
        }
    }

    void Move(int delta)
    {
        int n = _list.ItemCount;
        if (n == 0) return;
        _list.SelectedIndex = (_list.SelectedIndex + delta + n) % n;
        _list.ScrollIntoView(_list.SelectedIndex);
    }

    void Accept()
    {
        if (_list.SelectedItem is not Completion c) { Hide(); return; }
        var text  = _box.Text ?? "";
        var caret = Math.Clamp(_box.CaretIndex, 0, text.Length);

        // Replace the identifier immediately left of the caret with the completion.
        int start = caret;
        while (start > 0 && IsIdent(text[start - 1])) start--;

        bool call = c.Kind is CompletionKind.Method or CompletionKind.Function;
        var insert = call ? c.Insert + "()" : c.Insert;
        var newText = text[..start] + insert + text[caret..];
        // For a call, drop the caret between the parens; otherwise after the inserted name.
        int newCaret = start + c.Insert.Length + (call ? 1 : 0);

        _suppress = true;
        _box.Text = newText;
        _box.CaretIndex = newCaret;
        _suppress = false;
        Hide();
    }

    void Hide() => _popup.IsOpen = false;

    // True if the text ends in an identifier character or a '.' (member access) — i.e. the user is mid-token.
    static bool EndsInToken(string s)
    {
        if (s.Length == 0) return false;
        var last = s[^1];
        return IsIdent(last) || last == '.';
    }

    static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';

    // Each row: a kind glyph, the label (with params), and a dim right-aligned detail (return/field type).
    static FuncDataTemplate<Completion> ItemTemplate() => new((_, _) =>
    {
        var glyph = new TextBlock { Width = 18, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };
        glyph.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(Completion.Kind)) { Converter = KindGlyph });

        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        label.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(Completion.Label)));

        var detail = new TextBlock { Margin = new(12, 0, 0, 0), Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
        detail.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(Completion.Detail)));

        var grid = new Grid { ColumnDefinitions = new("Auto,*,Auto"), Margin = new(2, 1, 2, 1) };
        Grid.SetColumn(glyph, 0);  grid.Children.Add(glyph);
        Grid.SetColumn(label, 1);  grid.Children.Add(label);
        Grid.SetColumn(detail, 2); grid.Children.Add(detail);
        return grid;
    });

    static readonly Avalonia.Data.Converters.IValueConverter KindGlyph =
        new Avalonia.Data.Converters.FuncValueConverter<CompletionKind, string>(k => k switch
        {
            CompletionKind.Variable  => "𝑥",
            CompletionKind.Object    => "◆",
            CompletionKind.Function or CompletionKind.Method => "ƒ",
            CompletionKind.Field     => "▪",
            CompletionKind.Class     => "C",
            CompletionKind.Struct    => "S",
            CompletionKind.Interface => "I",
            CompletionKind.Enum      => "E",
            CompletionKind.EnumValue => "•",
            CompletionKind.Namespace => "{ }",
            _                        => "",
        });
}
