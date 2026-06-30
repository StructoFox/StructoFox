using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using StructoFox.AI;
using StructoFox.Core;

namespace StructoFox.Plugin.AiCodegen;

/// <summary>Manage per-provider API keys. Keys are stored in the OS key store (Windows Credential Manager),
/// never in the settings file. Only cloud providers need a key; once set, a provider becomes selectable when
/// configuring a model card.</summary>
internal static class ApiKeysWindow
{
    public static void Show(IPluginContext ctx)
    {
        var win   = PluginUi.NewWindow(ctx, "API-Keys verwalten", 560, 620);
        var panel = new StackPanel { Margin = new(18) };

        panel.Children.Add(new TextBlock
        {
            Text = "API-Schlüssel werden im Schlüsselspeicher des Betriebssystems abgelegt "
                 + "(unter Windows: Anmeldeinformationsverwaltung), nicht in einer Klartextdatei.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new(0, 0, 0, 12),
        });

        foreach (var p in AiProviders.All.Where(p => p.Kind == AiProviderKind.Cloud))
        {
            var prov = p;
            var row  = new Grid { Margin = new(0, 4, 0, 4), ColumnDefinitions = new("160,*,Auto,Auto") };

            var dot = new Ellipse { Width = 9, Height = 9, Margin = new(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center };
            void Refresh() => dot.Fill = new SolidColorBrush(
                KeyStore.Has(prov.Id) ? Color.FromRgb(80, 190, 80) : Color.FromRgb(150, 150, 150));
            Refresh();

            var name = new StackPanel { Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(dot);
            name.Children.Add(new TextBlock { Text = prov.Display, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(name, 0);
            row.Children.Add(name);

            var box = new TextBox
            {
                PasswordChar = '•', Watermark = KeyStore.Has(prov.Id) ? "•••• gespeichert" : "API-Key…",
                Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(box, 1);
            row.Children.Add(box);

            var save = new Button { Content = "Speichern", Margin = new(0, 0, 6, 0) };
            save.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(box.Text))
                {
                    KeyStore.Save(prov.Id, box.Text.Trim());
                    box.Text = ""; box.Watermark = "•••• gespeichert";
                    Refresh();
                }
            };
            Grid.SetColumn(save, 2);
            row.Children.Add(save);

            var del = new Button { Content = "✕" };
            del.Click += (_, _) => { KeyStore.Delete(prov.Id); box.Text = ""; box.Watermark = "API-Key…"; Refresh(); };
            Grid.SetColumn(del, 3);
            row.Children.Add(del);

            panel.Children.Add(row);
        }

        win.Content = new ScrollViewer { Content = panel };
        win.Open(ctx);
    }
}
