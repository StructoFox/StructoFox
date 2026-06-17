using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OXSUIT.Loaders.Avalonia;
using StructoFox.Core.Models;
using StructoFox.Core;

namespace StructoFox.App;

public partial class MainWindow : Window
{
    // Builds the window and drops the assembled content in — the fox's first den.
    public MainWindow()
    {
        InitializeComponent();
        Content = BuildContent();
    }

    // Lays out the placeholder landing screen: title, tagline, and two live smoke tests.
    // Proves both the platform-neutral Core and the OXSUIT theme loader are wired in.
    Control BuildContent()
    {
        var root = new StackPanel { Margin = new(24), Spacing = 12 };

        // Try the OXSUIT loader on an inline theme so we can tint the title from it.
        var theme = LoadSampleTheme(out var brushCount);
        var titleBrush = theme.TryGetResource("AccentBgBrush", null, out var b)
            ? b as IBrush : null;

        root.Children.Add(new TextBlock
        {
            Text = "🦊 " + Loc.S("App_Title"),
            FontSize = 32, FontWeight = FontWeight.Bold,
            Foreground = titleBrush,  // tinted by the loaded theme, or default if it failed
        });
        root.Children.Add(new TextBlock
        {
            Text = Loc.S("App_Tagline"),
            FontSize = 16, Opacity = 0.7,
        });
        root.Children.Add(new TextBlock
        {
            Text = $"OXSUIT loader: {brushCount} brushes parsed.",
            FontSize = 12, Opacity = 0.6,
        });

        // UI-kit smoke test: a button that chains the new MessageDialog + PromptDialog.
        var status = new TextBlock { FontSize = 12, Opacity = 0.6 };
        var demo = Ui.Btn("🦊 Test dialogs", "Try the new MessageDialog and PromptDialog");
        demo.HorizontalAlignment = HorizontalAlignment.Left;
        demo.Click += async (_, _) =>
        {
            var answer = await MessageDialog.Show(this, "Does the fox's dialog work?", "UI kit", DialogButtons.YesNo);
            var name = await PromptDialog.Show(this, "What should we name the demo?", "Reynard", "Prompt");
            status.Text = $"You said {answer}; name = {name ?? "(cancelled)"}.";
        };
        root.Children.Add(demo);
        root.Children.Add(status);

        // UI-kit smoke test: a themed combo of ComboItems, reflecting its pick into the status line.
        var combo = Ui.Combo(220);
        combo.Items.Add(new ComboItem("C#", "csharp"));
        combo.Items.Add(new ComboItem("Rust", "rust"));
        combo.Items.Add(new ComboItem("Python", "python"));
        combo.SelectedIndex = 0;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboItem ci) status.Text = $"Combo pick: {ci.Name} (id={ci.Id})";
        };
        root.Children.Add(combo);

        root.Children.Add(new TextBlock { Text = Loc.S("Smoke_Header"), Margin = new(0, 12, 0, 0) });
        root.Children.Add(new TextBox
        {
            Text = GenerateSample(),
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            AcceptsReturn = true,
            VerticalAlignment = VerticalAlignment.Stretch,
        });

        return root;
    }

    // Hand-rolls a tiny sample class and runs it through the real CodeExportService.
    // If this returns valid C#, the Core pipeline lives happily inside Avalonia.
    static string GenerateSample()
    {
        var greeter = new CodeEntity
        {
            Name       = "Greeter",
            EntityType = CodeEntityType.Class,
            Namespace  = "StructoFox.Demo",
            Fields     = { new CodeField { Name = "_name", DataType = "string", Visibility = CodeVisibility.Private } },
            Methods    =
            {
                new CodeMethod
                {
                    Name       = "Greet",
                    ReturnType = "string",
                    Visibility = CodeVisibility.Public,
                    Parameters = { new CodeParam { Name = "name", DataType = "string" } },
                },
            },
        };

        return CodeExportService.Generate(new[] { greeter }, ExportLanguage.CSharp);
    }

    // Feeds a tiny inline OXSUIT theme through the loader to confirm it parses on Avalonia.
    // Returns the resource dictionary (or null) and reports how many brushes came back.
    static ResourceDictionary LoadSampleTheme(out int brushCount)
    {
        const string xml =
            """
            <oxsuit version="1.0" name="Smoke">
              <colors>
                <color key="AccentBg" value="#E8702A" />
                <color key="ContentText" value="#212121" />
                <color key="ContentBg" value="#FAFAFA" />
              </colors>
            </oxsuit>
            """;
        var dict = OxsuitLoader.LoadXml(xml);
        brushCount = dict.Count;
        return dict;
    }
}
