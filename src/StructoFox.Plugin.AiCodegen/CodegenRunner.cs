using System.IO;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
        var cardCombo = PluginUi.Combo();
        foreach (var c in cards)
            cardCombo.Items.Add(new ComboBoxItem
            {
                Content = (string.IsNullOrWhiteSpace(c.Name) ? c.Model : c.Name) + $"  ({c.Provider})",
                Tag = c,
            });
        cardCombo.SelectedIndex = 0;
        panel.Children.Add(cardCombo);

        panel.Children.Add(PluginUi.Label("Zielsprache"));
        var langCombo = PluginUi.Combo();
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
                // report runs on a worker thread → marshal status updates back to the UI thread.
                void Report(string s) => Dispatcher.UIThread.Post(() => status.Text = s);
                var (written, incomplete) = await Task.Run(() =>
                    Generate(ctx.ProjectFolder!, card, lang, outDir, Report));

                var warn = incomplete.Count == 0 ? "" :
                    $"\n\n⚠ {incomplete.Count} Datei(en) wurden trotz Fortsetzungs-Versuchen vom Modell nicht "
                  + "vollständig beendet (Token-/Kontextgrenze):\n  • " + string.Join("\n  • ", incomplete)
                  + "\nTipp: Max. Tokens auf der Modell-Karte erhöhen oder das Diagramm in kleinere "
                  + "Funktionen aufteilen.";
                status.Text = incomplete.Count == 0
                    ? $"✓ Fertig. {written} Datei(en) geschrieben."
                    : $"⚠ Fertig mit {incomplete.Count} unvollständigen Datei(en).";
                ctx.ShowText("Generiertes Projekt",
                    $"Geschrieben nach:\n{outDir}\n\n{written} Datei(en).{warn}\n\n"
                    + "C#: dotnet build   ·   C++: cmake . && cmake --build .");
            }
            catch (Exception ex) { status.Text = "⚠ " + ex.Message; }
            finally { go.IsEnabled = true; }
        };

        win.Content = new ScrollViewer { Content = panel };
        win.Open(ctx);
    }

    // ── Core generation ──────────────────────────────────────────────────────

    static (int count, List<string> incomplete) Generate(
        string projFolder, AiModelCard card, ExportLanguage lang, string outDir, Action<string> report)
    {
        var entities    = AiCodegenPlugin.GatherEntities(projFolder);
        var projectName = new DirectoryInfo(projFolder).Name;
        var files       = ProjectExporter.Build(entities, lang, projectName, projFolder);

        using var svc = AiProviders.Create(card);
        // A generous per-reply budget so most files finish in one call; the continuation loop covers the rest.
        svc.MaxTokens = card.MaxTokens > 0 ? card.MaxTokens : 8192;
        var maxCont = card.MaxContinuations > 0 ? card.MaxContinuations : DefaultContinuations;

        int count = 0;
        var incomplete = new List<string>();
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(outDir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            var finalContent = content;
            if (IsSource(path))
            {
                report($"KI füllt {Path.GetFileName(path)} …");
                try
                {
                    var (text, complete) = FillBodies(svc, lang, content, report, Path.GetFileName(path), maxCont)
                        .GetAwaiter().GetResult();
                    finalContent = text;
                    if (!complete) incomplete.Add(path);
                }
                catch { /* keep the deterministic skeleton if the AI call fails */ }
            }
            File.WriteAllText(full, finalContent);
            count++;
        }
        return (count, incomplete);
    }

    // Default continuation rounds when a card doesn't override it (0 = use this).
    const int DefaultContinuations = 8;

    // A project/build file we never send to the AI; everything else is source to complete.
    static bool IsSource(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase)) return false;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is not (".csproj" or ".sln" or ".txt" or ".json" or ".xml");
    }

    // Fills the bodies and GUARANTEES a complete file as far as the model allows: if the API stops because it
    // hit the output-token cap (finish_reason "length"), we ask it to continue from where it left off and
    // concatenate, until it finishes naturally or we reach MaxContinuations. Returns (source, wasCompleted).
    static async Task<(string content, bool complete)> FillBodies(
        ICloudAIService svc, ExportLanguage lang, string skeleton, Action<string> report, string fileName, int maxCont)
    {
        var system =
            $"You are a senior {lang} programmer. You complete code skeletons generated from program-flow "
            + "diagrams. The structure (namespaces, types, signatures) is FIXED — do not change it. Implement "
            + "every method/function body so the file compiles and behaves sensibly. Reply with ONLY the complete "
            + "source file: no markdown fences, no commentary.";

        var messages = new List<CloudAIMessage> { new("user", "Complete this skeleton:\n\n" + skeleton) };
        var sb       = new System.Text.StringBuilder();

        var reply = await svc.SendAsync(messages, system);
        sb.Append(reply);

        int rounds = 0;
        while (svc.LastFinishReason == FinishReason.Length && rounds++ < maxCont)
        {
            report($"KI setzt {fileName} fort … ({rounds})");
            messages.Add(new CloudAIMessage("assistant", reply));
            messages.Add(new CloudAIMessage("user",
                "Continue the source code EXACTLY where you left off. Do not repeat anything already written, "
              + "do not add explanations or markdown fences — output only the remaining code."));
            reply = await svc.SendAsync(messages, system);
            sb.Append(reply);
        }

        var complete = svc.LastFinishReason != FinishReason.Length;   // false only if still truncated at the cap
        return (StripFences(sb.ToString()), complete);
    }

    // Remove any markdown fence lines (```), wherever they appear — robust against fences that show up at the
    // seams when a truncated reply is continued across several calls.
    static string StripFences(string s)
    {
        var lines = s.Replace("\r\n", "\n").Split('\n');
        var kept  = lines.Where(l => !l.TrimStart().StartsWith("```")).ToArray();
        return kept.Length == lines.Length ? s : string.Join("\n", kept).Trim('\n');
    }
}
