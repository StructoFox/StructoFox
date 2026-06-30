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
            Text = $"API-Schlüssel werden ausschließlich im Schlüsselspeicher des Betriebssystems abgelegt "
                 + $"({KeyStore.BackendName}) — niemals in einer Datei.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new(0, 0, 0, 12),
        });

        // If the native store isn't reachable, warn up front with copy-able details (no insecure fallback).
        var (backendOk, backendDetails) = KeyStore.Probe();
        if (!backendOk)
        {
            var warn = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(40, 210, 90, 70)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(210, 90, 70)), BorderThickness = new(1),
                CornerRadius = new(6), Padding = new(12), Margin = new(0, 0, 0, 12),
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = "⚠  Der Schlüsselspeicher ist nicht verfügbar. "
                            + "Bis er eingerichtet ist, können keine API-Keys gespeichert werden.",
                            TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold },
                    },
                },
            };
            var showDetails = PluginUi.Btn("Details anzeigen");
            showDetails.Click += (_, _) => ErrorDialog.Show(ctx, "Schlüsselspeicher nicht verfügbar",
                backendDetails ?? "(keine Details)");
            ((StackPanel)warn.Child).Children.Add(showDetails);
            panel.Children.Add(warn);
        }

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
                PasswordChar = '•', PlaceholderText = KeyStore.Has(prov.Id) ? "•••• gespeichert" : "API-Key…",
                Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(box, 1);
            row.Children.Add(box);

            var save = PluginUi.Btn("Speichern"); save.Margin = new(0, 0, 6, 0);
            save.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(box.Text)) return;
                try
                {
                    KeyStore.Save(prov.Id, box.Text.Trim());
                    box.Text = ""; box.PlaceholderText = "•••• gespeichert";
                    Refresh();
                }
                catch (KeyStoreException ex) { ErrorDialog.Show(ctx, ex.Message, ex.Details); }
                catch (Exception ex)         { ErrorDialog.Show(ctx, "Unerwarteter Fehler beim Speichern.", ex.ToString()); }
            };
            Grid.SetColumn(save, 2);
            row.Children.Add(save);

            var del = PluginUi.Btn("✕");
            del.Click += (_, _) =>
            {
                try { KeyStore.Delete(prov.Id); box.Text = ""; box.PlaceholderText = "API-Key…"; Refresh(); }
                catch (KeyStoreException ex) { ErrorDialog.Show(ctx, ex.Message, ex.Details); }
                catch (Exception ex)         { ErrorDialog.Show(ctx, "Unerwarteter Fehler beim Entfernen.", ex.ToString()); }
            };
            Grid.SetColumn(del, 3);
            row.Children.Add(del);

            panel.Children.Add(row);
        }

        win.Content = new ScrollViewer { Content = panel };
        win.Open(ctx);
    }
}
