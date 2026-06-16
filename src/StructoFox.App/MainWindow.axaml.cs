using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
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

    // Lays out the placeholder landing screen: title, tagline, and a live core smoke test.
    // Proves the platform-neutral Core is wired in before any real UI exists.
    static Control BuildContent()
    {
        var root = new StackPanel { Margin = new(24), Spacing = 12 };

        root.Children.Add(new TextBlock
        {
            Text = "🦊 " + Loc.S("App_Title"),
            FontSize = 32, FontWeight = FontWeight.Bold,
        });
        root.Children.Add(new TextBlock
        {
            Text = Loc.S("App_Tagline"),
            FontSize = 16, Opacity = 0.7,
        });

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
}
