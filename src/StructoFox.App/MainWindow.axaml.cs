using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace StructoFox.App;

/// <summary>
/// The StructoFox shell — a "Dev Cockpit": a top bar (fox brand + theme/palette), a slim icon rail
/// of sections on the left, and a themed content area with a faint blueprint grid. Sections are
/// scaffolded; the diagram editors already work, the entity/board galleries are filled in as their
/// editors get ported.
/// </summary>
public partial class MainWindow : Window
{
    enum Section { Boards, Classes, Functions, Export }

    static readonly FontFamily Mono = new("Consolas, Menlo, Courier New, monospace");

    Section _section = Section.Functions;
    readonly ContentControl _content = new();
    readonly Dictionary<Section, Button> _railButtons = new();

    // Builds the shell window and shows the default section.
    public MainWindow()
    {
        InitializeComponent();
        Title  = "StructoFox";
        Width  = 1140;
        Height = 740;
        MinWidth = 760; MinHeight = 480;
        Ui.ThemeWindow(this);

        Content = BuildShell();
        ShowSection(_section);
    }

    // Lays out the three regions: top bar (row 0), then rail + content (row 1).
    Control BuildShell()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));

        var top = BuildTopBar(); Grid.SetRow(top, 0); root.Children.Add(top);

        var body = new Grid(); Grid.SetRow(body, 1);
        body.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        body.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var rail = BuildRail(); Grid.SetColumn(rail, 0); body.Children.Add(rail);
        var host = BuildContentHost(); Grid.SetColumn(host, 1); body.Children.Add(host);
        root.Children.Add(body);

        return root;
    }

    // The top bar: fox brand + tagline on the left, theme switcher + palette editor on the right.
    Control BuildTopBar()
    {
        var bar = new Border { Padding = new(14, 8) };
        Ui.Theme(bar, Border.BackgroundProperty, "SidebarBgBrush");

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new TextBlock { Text = "🦊", FontSize = 22, VerticalAlignment = VerticalAlignment.Center });
        var title = new TextBlock { Text = "StructoFox", FontSize = 17, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        Ui.Theme(title, TextBlock.ForegroundProperty, "SidebarTextBrush");
        var tag = new TextBlock { Text = "Flow · Struct · Code", FontSize = 11, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(2, 0, 0, 2) };
        Ui.Theme(tag, TextBlock.ForegroundProperty, "SidebarTextBrush");
        brand.Children.Add(title);
        brand.Children.Add(tag);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(BuildThemeSwitcher());
        var palette = Ui.Btn("🎨 Palettes", "Open the palette editor");
        palette.Click += (_, _) => new PaletteEditorWindow().Show();
        right.Children.Add(palette);

        var dock = new DockPanel();
        DockPanel.SetDock(brand, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(brand);
        dock.Children.Add(right);
        bar.Child = dock;
        return bar;
    }

    // The live theme switcher combo (every shipped OXSUIT theme).
    Control BuildThemeSwitcher()
    {
        var themes = ThemeManager.Available().ToList();
        var combo = Ui.Combo(220);
        foreach (var (name, _) in themes) combo.Items.Add(new ComboItem(name, name));
        if (themes.Count > 0)
            combo.SelectedIndex = Math.Max(0, themes.FindIndex(t => t.Name.Equals(ThemeManager.DefaultThemeName, StringComparison.OrdinalIgnoreCase)));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboItem ci)
            {
                var hit = themes.FirstOrDefault(t => t.Name == ci.Id);
                if (hit.Path is not null) ThemeManager.Apply(Application.Current!, hit.Path);
            }
        };
        return combo;
    }

    // The left icon rail: one button per section, the active one highlighted.
    Control BuildRail()
    {
        var rail = new Border { Width = 84, Padding = new(6, 10) };
        Ui.Theme(rail, Border.BackgroundProperty, "SidebarBgBrush");

        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(RailButton(Section.Boards,    "🗂", "Boards"));
        stack.Children.Add(RailButton(Section.Classes,   "🧩", "Classes"));
        stack.Children.Add(RailButton(Section.Functions, "ƒ",  "Functions"));
        stack.Children.Add(RailButton(Section.Export,    "⇩",  "Export"));
        rail.Child = stack;
        return rail;
    }

    // Builds one rail button (icon over label) wired to switch sections.
    Button RailButton(Section section, string icon, string label)
    {
        var b = new Button
        {
            Padding = new(0, 8), CornerRadius = new(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Content = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = icon, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = label, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };
        b.Click += (_, _) => ShowSection(section);
        _railButtons[section] = b;
        return b;
    }

    // The content area: a faint grid backdrop with the scrollable section view on top.
    Control BuildContentHost()
    {
        var host = new Border();
        Ui.Theme(host, Border.BackgroundProperty, "ContentBgBrush");

        var layered = new Grid();
        layered.Children.Add(new HoneycombBackdrop());
        layered.Children.Add(new ScrollViewer
        {
            Padding = new(24),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _content,
        });
        host.Child = layered;
        return host;
    }

    // Switches the active section: restyle the rail and rebuild the content view.
    void ShowSection(Section section)
    {
        _section = section;
        foreach (var (s, b) in _railButtons)
        {
            var active = s == section;
            Ui.Theme(b, TemplatedControl.BackgroundProperty, active ? "AccentBgBrush"  : "ControlBgBrush");
            Ui.Theme(b, TemplatedControl.ForegroundProperty, active ? "AccentTextBrush" : "SidebarTextBrush");
        }
        _content.Content = BuildSectionView(section);
    }

    // Builds the view for a section — a heading + (for now) a hint or the working demo actions.
    Control BuildSectionView(Section section)
    {
        var root = new StackPanel { Spacing = 12, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };

        var (title, blurb) = section switch
        {
            Section.Boards    => ("Boards",    "Structure boards — arrange entities on a canvas. (Board canvas port coming.)"),
            Section.Classes   => ("Classes",   "Namespaces, classes, structs, interfaces, enums & objects. (Entity editor port coming.)"),
            Section.Functions => ("Functions", "Functions & methods — sketch their logic as a flowchart or structogram."),
            _                 => ("Export",    "Generate source from your structures in 10 languages. (Wiring coming.)"),
        };

        root.Children.Add(Heading(title));
        root.Children.Add(Note(blurb));

        if (section == Section.Functions)
        {
            // Working today: open the diagram chooser for a sample function.
            var open = Ui.Btn("🔁 New diagram (demo)", "Open the PAP / structogram chooser");
            open.HorizontalAlignment = HorizontalAlignment.Left;
            open.Click += async (_, _) =>
                await DiagramLauncher.ChooseAndOpen(this, System.IO.Path.GetTempPath(), "demo", "Greeter.Greet()", null);
            root.Children.Add(open);
            root.Children.Add(new TextBlock { Text = "demo key: Greeter.Greet()", FontFamily = Mono, FontSize = 11, Opacity = 0.6 });
        }

        return root;
    }

    // A section heading, themed to the content text colour.
    static TextBlock Heading(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 22, FontWeight = FontWeight.Bold };
        Ui.Theme(t, TextBlock.ForegroundProperty, "ContentTextBrush");
        return t;
    }

    // A muted descriptive note under a heading.
    static TextBlock Note(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        Ui.Theme(t, TextBlock.ForegroundProperty, "ContentTextBrush");
        return t;
    }
}
