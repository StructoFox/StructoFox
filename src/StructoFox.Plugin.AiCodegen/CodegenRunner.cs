using System.IO;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using StructoFox.AI;
using StructoFox.Core;
using StructoFox.Core.Models;

namespace StructoFox.Plugin.AiCodegen;

/// <summary>Drives one code-generation run: pick a model card + target language, build the deterministic skeleton
/// via <see cref="ProjectExporter"/>, ask the AI to fill the bodies, then write a ready-to-build project.</summary>
internal static class CodegenRunner
{
    static readonly (ExportLanguage Lang, string Label)[] Languages =
    {
        (ExportLanguage.CSharp, "C#"), (ExportLanguage.Cpp, "C++"), (ExportLanguage.Java, "Java"),
        (ExportLanguage.TypeScript, "TypeScript"), (ExportLanguage.Python, "Python"), (ExportLanguage.Kotlin, "Kotlin"),
        (ExportLanguage.Swift, "Swift"), (ExportLanguage.Php, "PHP"), (ExportLanguage.Go, "Go"), (ExportLanguage.Rust, "Rust"),
        (ExportLanguage.Verse, "Verse"),
    };

    public static void Run(IPluginContext ctx)
    {
        if (ctx.ProjectFolder is null) { ctx.Notify("Kein Projekt geöffnet."); return; }

        var cards = AiSettings.Load().Cards.Where(c => c.Enabled).ToList();
        if (cards.Count == 0)
        {
            ctx.Notify("Kein KI-Modell konfiguriert. Erst „KI-Konfiguration“ öffnen und ein Modell anlegen.");
            return;
        }

        var win   = PluginUi.NewWindow(ctx, "KI: Code generieren", 560, 520);
        var panel = new StackPanel { Margin = new(20) };

        panel.Children.Add(PluginUi.Label("Modell"));
        var cardCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var c in cards)
            cardCombo.Items.Add(new ComboBoxItem
            {
                Content = (string.IsNullOrWhiteSpace(c.Name) ? c.Model : c.Name) + $"  ({c.Provider})",
                Tag = c,
            });
        cardCombo.SelectedIndex = 0;
        panel.Children.Add(cardCombo);

        panel.Children.Add(PluginUi.Label("Zielsprache"));
        var langCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (_, label) in Languages) langCombo.Items.Add(label);
        langCombo.SelectedIndex = 0;
        panel.Children.Add(langCombo);

        panel.Children.Add(PluginUi.Dim(
            "Das Gerüst (Typen, Signaturen) wird deterministisch aus dem Diagramm erzeugt; das gewählte Modell "
            + "füllt die Rümpfe. Anschließend wählst du einen Zielordner, in den ein baubares Projekt geschrieben wird."));

        var go     = PluginUi.Btn("Generieren…");
        var status = PluginUi.Dim("");
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 14, 0, 0),
            Children = { go } });
        panel.Children.Add(status);

        go.Click += async (_, _) =>
        {
            var card = (cardCombo.SelectedItem as ComboBoxItem)?.Tag as AiModelCard ?? cards[0];
            var lang = Languages[Math.Max(0, langCombo.SelectedIndex)].Lang;

            if (ctx.OwnerWindow is not Window owner) { status.Text = "Kein Fenster-Kontext."; return; }
            var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Zielordner für das generierte Projekt", AllowMultiple = false,
            });
            var outDir = folders.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrEmpty(outDir)) return;

            go.IsEnabled = false; status.Text = "Erzeuge Gerüst…";
            try
            {
                var written = await Task.Run(() => Generate(ctx.ProjectFolder!, card, lang, outDir,
                    s => status.Text = s));
                status.Text = $"✓ Fertig. {written} Datei(en) geschrieben.";
                ctx.ShowText("Generiertes Projekt",
                    $"Geschrieben nach:\n{outDir}\n\n{written} Datei(en).\n\n"
                    + "C#: dotnet build   ·   C++: cmake . && cmake --build .");
            }
            catch (Exception ex) { status.Text = "⚠ " + ex.Message; }
            finally { go.IsEnabled = true; }
        };

        win.Content = new ScrollViewer { Content = panel };
        win.Open(ctx);
    }

    // ── Core generation ──────────────────────────────────────────────────────

    static int Generate(string projFolder, AiModelCard card, ExportLanguage lang, string outDir,
                        Action<string> report)
    {
        var entities    = AiCodegenPlugin.GatherEntities(projFolder);
        var projectName = new DirectoryInfo(projFolder).Name;
        var files       = ProjectExporter.Build(entities, lang, projectName, projFolder);

        using var svc = AiProviders.Create(card);
        svc.MaxTokens = card.MaxTokens > 0 ? card.MaxTokens : 4096;

        int count = 0;
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(outDir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            var finalContent = content;
            if (IsSource(path))
            {
                report($"KI füllt {Path.GetFileName(path)} …");
                try { finalContent = FillBodies(svc, lang, content).GetAwaiter().GetResult(); }
                catch { /* keep the deterministic skeleton if the AI call fails */ }
            }
            File.WriteAllText(full, finalContent);
            count++;
        }
        return count;
    }

    // A project/build file we never send to the AI; everything else is source to complete.
    static bool IsSource(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase)) return false;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is not (".csproj" or ".sln" or ".txt" or ".json" or ".xml");
    }

    static async Task<string> FillBodies(ICloudAIService svc, ExportLanguage lang, string skeleton)
    {
        var system =
            $"You are a senior {lang} programmer. You complete code skeletons generated from program-flow "
            + "diagrams. The structure (namespaces, types, signatures) is FIXED — do not change it. Implement "
            + "every method/function body so the file compiles and behaves sensibly. Reply with ONLY the complete "
            + "source file: no markdown fences, no commentary.";
        var user = "Complete this skeleton:\n\n" + skeleton;

        var reply = await svc.SendAsync(new[] { new CloudAIMessage("user", user) }, system);
        return StripFences(reply);
    }

    // Models often wrap code in ``` fences despite instructions — remove them.
    static string StripFences(string s)
    {
        var t = s.Trim();
        if (!t.StartsWith("```")) return s;
        var nl = t.IndexOf('\n');
        if (nl >= 0) t = t[(nl + 1)..];                 // drop opening ```lang line
        var close = t.LastIndexOf("```", StringComparison.Ordinal);
        if (close >= 0) t = t[..close];
        return t.TrimEnd();
    }
}
