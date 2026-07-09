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
        (ExportLanguage.CSharp, "C#"), (ExportLanguage.Cpp, "C++"), (ExportLanguage.C, "C"), (ExportLanguage.Java, "Java"),
        (ExportLanguage.TypeScript, "TypeScript"), (ExportLanguage.JavaScript, "JavaScript"),
        (ExportLanguage.Python, "Python"), (ExportLanguage.Kotlin, "Kotlin"),
        (ExportLanguage.Swift, "Swift"), (ExportLanguage.Php, "PHP"), (ExportLanguage.Go, "Go"), (ExportLanguage.Rust, "Rust"),
        (ExportLanguage.Verse, "Verse"),
    };

    public static void Run(IPluginContext ctx)
    {
        PluginLoc.Use(ctx);
        if (ctx.ProjectFolder is null) { ctx.Notify(PluginLoc.T("gen_noproject")); return; }

        var cards = AiSettings.Load().Cards.Where(c => c.Enabled).ToList();
        if (cards.Count == 0)
        {
            ctx.Notify(PluginLoc.T("gen_nomodel"));
            return;
        }

        var win   = PluginUi.NewWindow(ctx, PluginLoc.T("gen_title"), 560, 520);
        var panel = new StackPanel { Margin = new(20) };

        panel.Children.Add(PluginUi.Label(PluginLoc.T("gen_model")));
        var cardCombo = PluginUi.Combo();
        foreach (var c in cards)
            cardCombo.Items.Add(new ComboBoxItem
            {
                Content = (string.IsNullOrWhiteSpace(c.Name) ? c.Model : c.Name) + $"  ({c.Provider})",
                Tag = c,
            });
        cardCombo.SelectedIndex = 0;
        panel.Children.Add(cardCombo);

        // Warn when the picked model looks like a BASE model (not instruction-tuned) — those ignore the prompt
        // and produce garbage for code generation.
        var baseWarn = new TextBlock
        {
            Text = PluginLoc.T("gen_basewarn"), TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false,
            FontSize = 11, Margin = new(0, 4, 0, 0),
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(210, 140, 70)),
        };
        panel.Children.Add(baseWarn);
        void SyncBaseWarn() =>
            baseWarn.IsVisible = (cardCombo.SelectedItem as ComboBoxItem)?.Tag is AiModelCard c && LooksLikeBaseModel(c.Model);
        cardCombo.SelectionChanged += (_, _) => SyncBaseWarn();
        SyncBaseWarn();

        var pinfo = ProjectService.Load(ctx.ProjectFolder!);

        panel.Children.Add(PluginUi.Label(PluginLoc.T("gen_lang")));
        var langCombo = PluginUi.Combo();
        foreach (var (_, label) in Languages) langCombo.Items.Add(label);
        // Preselect the language this project last generated in (falls back to the first).
        var lastIdx = pinfo?.LastExportLanguage is { } ll ? Array.FindIndex(Languages, l => l.Lang.ToString() == ll) : -1;
        langCombo.SelectedIndex = lastIdx >= 0 ? lastIdx : 0;
        panel.Children.Add(langCombo);

        // Target platform — only relevant for native languages (C/C++), so shown only for them.
        var platLabel = PluginUi.Label(PluginLoc.T("gen_platform"));
        var platCombo = PluginUi.Combo();
        foreach (var (key, label) in Platforms) platCombo.Items.Add(new ComboBoxItem { Content = label(), Tag = key });
        var platIdx = pinfo?.TargetPlatform is { } tp ? Array.FindIndex(Platforms, p => p.Key == tp) : -1;
        platCombo.SelectedIndex = platIdx >= 0 ? platIdx : 0;
        ToolTip.SetTip(platCombo, PluginLoc.T("gen_platform_tip"));
        panel.Children.Add(platLabel);
        panel.Children.Add(platCombo);

        // Multi-file (one file per type) — only offered for the languages that support it.
        var multiCheck = new CheckBox { Content = PluginLoc.T("gen_multifile"), IsChecked = pinfo?.MultiFile ?? false, Margin = new(0, 6, 0, 0) };
        ToolTip.SetTip(multiCheck, PluginLoc.T("gen_multifile_tip"));
        panel.Children.Add(multiCheck);

        void SyncLangUi()
        {
            var l = Languages[Math.Max(0, langCombo.SelectedIndex)].Lang;
            platLabel.IsVisible = platCombo.IsVisible = l is ExportLanguage.Cpp or ExportLanguage.C;
            multiCheck.IsVisible = SupportsMultiFile(l);
        }
        langCombo.SelectionChanged += (_, _) => SyncLangUi();
        SyncLangUi();

        // Target folder — defaults to the project's own "Code" subfolder; the user can edit it or browse.
        panel.Children.Add(PluginUi.Label(PluginLoc.T("gen_target")));
        var dirBox = new TextBox { Text = Path.Combine(ctx.ProjectFolder!, "Code") };
        var browse = PluginUi.Btn(PluginLoc.T("gen_browse")); browse.Margin = new(8, 0, 0, 0);
        var dirRow = new Grid { ColumnDefinitions = new("*,Auto") };
        Grid.SetColumn(dirBox, 0); dirRow.Children.Add(dirBox);
        Grid.SetColumn(browse, 1); dirRow.Children.Add(browse);
        panel.Children.Add(dirRow);

        panel.Children.Add(PluginUi.Dim(PluginLoc.T("gen_expl")));

        var go      = PluginUi.Btn(PluginLoc.T("gen_go"));
        var status  = PluginUi.Dim("");
        // Indeterminate progress while the AI works. Hidden until a run starts.
        var spinner = new ProgressBar { IsIndeterminate = true, IsVisible = false, Height = 4, Margin = new(0, 10, 0, 0) };
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 14, 0, 0),
            Children = { go } });
        panel.Children.Add(spinner);
        panel.Children.Add(status);

        // Browse only overrides the default: pick a folder and drop it into the (editable) target box.
        browse.Click += async (_, _) =>
        {
            if (ctx.OwnerWindow is not Window owner) { status.Text = PluginLoc.T("gen_nowin"); return; }
            var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = PluginLoc.T("gen_folder"), AllowMultiple = false,
            });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } picked) dirBox.Text = picked;
        };

        var busy = false;   // guard re-entry WITHOUT disabling the button (a disabled button greys its label white)
        go.Click += async (_, _) =>
        {
            if (busy) return;
            var card = (cardCombo.SelectedItem as ComboBoxItem)?.Tag as AiModelCard ?? cards[0];
            var lang = Languages[Math.Max(0, langCombo.SelectedIndex)].Lang;
            var platform = (platCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Portable";
            // Clamp to the languages that actually support it: the checkbox keeps its state when hidden, so a
            // leftover tick from a prior Java/C# run must NOT leak into e.g. JS (which is always single-file —
            // otherwise the model is told there's a header and StripLocalImports is skipped).
            var multiFile = multiCheck.IsChecked == true && SupportsMultiFile(lang);

            var outDir = dirBox.Text?.Trim();
            if (string.IsNullOrEmpty(outDir)) { status.Text = PluginLoc.T("gen_notarget"); return; }
            Directory.CreateDirectory(outDir);

            busy = true; spinner.IsVisible = true; status.Text = PluginLoc.T("gen_skeleton");
            try
            {
                // report runs on a worker thread → marshal status updates back to the UI thread.
                void Report(string s) => Dispatcher.UIThread.Post(() => status.Text = s);
                var (written, incomplete, replaced) = await Task.Run(() =>
                    Generate(ctx.ProjectFolder!, card, lang, platform, multiFile, outDir, Report));

                // Remember this language + platform + multi-file choice as the project's defaults for next time.
                var info = ProjectService.Load(ctx.ProjectFolder!) ?? ProjectService.Create(ctx.ProjectFolder!, new DirectoryInfo(ctx.ProjectFolder!).Name);
                info.LastExportLanguage = lang.ToString();
                info.TargetPlatform = platform;
                info.MultiFile = multiFile;
                ProjectService.Save(ctx.ProjectFolder!, info);

                var warn = incomplete.Count == 0 ? ""
                    : PluginLoc.Tf("gen_incomplete", incomplete.Count, string.Join("\n  • ", incomplete));
                status.Text = incomplete.Count == 0
                    ? PluginLoc.Tf("gen_done", written)
                    : PluginLoc.Tf("gen_done_warn", incomplete.Count);
                // Build command shown per-generated-language (was hardcoded to C#/C++).
                var hint = BuildHint(lang, multiFile);
                var tail = hint.Length == 0 ? "" : $"\n\n{PluginLoc.T("gen_build")} {hint}";
                // Tell the user which existing files were replaced (their prior versions are in Replaced/).
                var replacedNote = replaced.Count == 0 ? ""
                    : "\n\n" + PluginLoc.Tf("gen_replaced", replaced.Count, ProjectExporter.ReplacedFolder,
                        "  • " + string.Join("\n  • ", replaced));
                ctx.ShowText(PluginLoc.T("gen_result_t"),
                    PluginLoc.Tf("gen_written", outDir, written, warn, tail) + replacedNote);
            }
            catch (Exception ex)
            {
                status.Text = "⚠ " + ex.Message;
                // Full, copyable detail (type + message + stack) so the user can actually report it — the old
                // non-selectable status line couldn't be copied.
                ErrorDialog.Show(ctx, PluginLoc.T("gen_failed"), ex.ToString());
            }
            finally { busy = false; spinner.IsVisible = false; }
        };

        win.Content = new ScrollViewer { Content = panel };
        win.Open(ctx);
    }

    // Languages that emit a real multi-file project (one file per type + build file); everything else is
    // always single-file, so the "one file per type" toggle must be ignored for them.
    static bool SupportsMultiFile(ExportLanguage l) =>
        l is ExportLanguage.CSharp or ExportLanguage.C or ExportLanguage.Cpp or ExportLanguage.Java
          or ExportLanguage.Go or ExportLanguage.Php or ExportLanguage.TypeScript or ExportLanguage.Python
          or ExportLanguage.Rust or ExportLanguage.JavaScript or ExportLanguage.Kotlin or ExportLanguage.Swift;

    // A deterministic bootstrap file that must NOT be sent to the AI (it has no bodies, only wiring). Most have a
    // unique name handled in IsSource; Rust's `main.rs` and Swift's `main.swift` are the SAME names single-file
    // uses as its (fillable) whole program — so they can only be recognised as bootstraps in MULTI-file mode.
    static bool IsMultiFileBootstrap(ExportLanguage lang, bool multiFile, string path)
    {
        if (!multiFile) return false;
        var n = Path.GetFileName(path);
        return (lang == ExportLanguage.Rust  && n.Equals("main.rs",    StringComparison.OrdinalIgnoreCase))
            || (lang == ExportLanguage.Swift && n.Equals("main.swift", StringComparison.OrdinalIgnoreCase));
    }

    // ── Core generation ──────────────────────────────────────────────────────

    static (int count, List<string> incomplete, IReadOnlyList<string> replaced) Generate(
        string projFolder, AiModelCard card, ExportLanguage lang, string platform, bool multiFile, string outDir, Action<string> report)
    {
        var entities    = AiCodegenPlugin.GatherEntities(projFolder);
        var projectName = new DirectoryInfo(projFolder).Name;
        var files       = ProjectExporter.Build(entities, lang, projectName, projFolder, outDir, multiFile);
        // Move any existing generated files (possibly another language, possibly user-edited) into Replaced/
        // so nothing is silently overwritten and no stale mix is left behind.
        var replaced    = ProjectExporter.BackupReplaced(outDir, projectName, lang);

        using var svc = AiProviders.Create(card);
        // A generous per-reply budget so most files finish in one call; the continuation loop covers the rest.
        svc.MaxTokens = card.MaxTokens > 0 ? card.MaxTokens : 8192;
        var maxCont = card.MaxContinuations > 0 ? card.MaxContinuations : DefaultContinuations;

        // In a multi-file project each source file is filled on its own, so the model can't see the other files'
        // declarations and would INVENT or RENAME function/method names. Give it the other files' declarations as
        // reference: C/C++ = the headers; Rust = the per-type module files (so cross-file method calls use the
        // EXACT names, not a snake_cased guess). The FillBodies prompt tells the model to use these verbatim.
        bool IsRef(string path)
        {
            var n = Path.GetFileName(path);
            return lang switch
            {
                // Rust/Swift fill each file independently, so give every file the per-type declaration files as
                // reference — pins method names (Rust) and argument labels (Swift) so cross-file calls match.
                ExportLanguage.Rust  => path.EndsWith(".rs")    && n != "main.rs"    && n != "functions.rs",
                ExportLanguage.Swift => path.EndsWith(".swift") && n != "main.swift" && n != "Functions.swift",
                _                    => path.EndsWith(".h") || path.EndsWith(".hpp"),
            };
        }
        var refHeaders = multiFile
            ? string.Join("\n\n", files.Where(f => IsRef(f.path))
                .Select(f => $"// ===== {Path.GetFileName(f.path)} =====\n{f.content}"))
            : "";

        int count = 0;
        var incomplete = new List<string>();
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(outDir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            var finalContent = content;
            if (IsSource(path) && !IsMultiFileBootstrap(lang, multiFile, path))
            {
                report(PluginLoc.Tf("gen_filling", Path.GetFileName(path)));
                try
                {
                    var (text, complete) = FillBodies(svc, lang, content, report, Path.GetFileName(path), maxCont, PlatformHint(lang, platform), multiFile, refHeaders)
                        .GetAwaiter().GetResult();
                    // An empty/whitespace reply (model returned nothing) must NOT wipe the good deterministic
                    // skeleton — keep it and flag the file as not fully done.
                    if (string.IsNullOrWhiteSpace(text)) incomplete.Add(path);
                    else
                    {
                        finalContent = text;
                        if (!complete) incomplete.Add(path);
                        // Drop any `// ===== file =====` reference-block marker the model copied from the refHeaders
                        // context into its own output (never legitimate generated code).
                        finalContent = StripRefMarkers(finalContent);
                        // Brace-delimited languages: drop any natural-language explanation a weak model appended
                        // AFTER the final closing brace (invalid code that breaks the compile).
                        if (IsBraceLang(lang)) finalContent = StripTrailingProse(finalContent);
                        // Native languages: a focused pass to add any missing platform/API #includes.
                        if (lang is ExportLanguage.C or ExportLanguage.Cpp)
                        {
                            report(PluginLoc.Tf("gen_includes", Path.GetFileName(path)));
                            finalContent = FixIncludes(svc, lang, finalContent, PlatformHint(lang, platform)).GetAwaiter().GetResult();
                        }
                        else if (lang is ExportLanguage.TypeScript or ExportLanguage.JavaScript)
                        {
                            // Single-file: drop any bogus relative import the model invented for same-file types.
                            if (!multiFile) finalContent = StripLocalImports(finalContent);
                            // A `const` that is later reassigned (e.g. a teardown `x = null`) is a compile error →
                            // relax it to `let`.
                            finalContent = FixConstReassign(finalContent);
                        }
                    }
                }
                catch { /* keep the deterministic skeleton if the AI call fails */ }
            }
            File.WriteAllText(full, finalContent);
            count++;
        }
        return (count, incomplete, replaced);
    }

    // Default continuation rounds when a card doesn't override it (0 = use this).
    const int DefaultContinuations = 8;

    // A project/build file we never send to the AI; everything else is source to complete. C headers (.h) are
    // pure declarations (no bodies to fill), so they stay deterministic too — else the model fills them with a
    // whole program. (C++ .hpp DO carry inline method bodies, so those are still sent.) The PHP index.php is a
    // pure bootstrap (autoloader + entry-point call, no bodies), so it's excluded too — else the model invents a
    // main() full of hallucinated pseudocode.
    static bool IsSource(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Equals("index.php", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Equals("index.ts", StringComparison.OrdinalIgnoreCase)) return false;   // TS multi-file bootstrap
        if (name.Equals("index.js", StringComparison.OrdinalIgnoreCase)) return false;   // JS multi-file bootstrap
        if (name.Equals("__main__.py", StringComparison.OrdinalIgnoreCase)) return false;   // Python multi-file bootstrap
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is not (".csproj" or ".sln" or ".txt" or ".json" or ".xml" or ".h");
    }

    // Fills the bodies and GUARANTEES a complete file as far as the model allows: if the API stops because it
    // hit the output-token cap (finish_reason "length"), we ask it to continue from where it left off and
    // concatenate, until it finishes naturally or we reach MaxContinuations. Returns (source, wasCompleted).
    static async Task<(string content, bool complete)> FillBodies(
        ICloudAIService svc, ExportLanguage lang, string skeleton, Action<string> report, string fileName, int maxCont,
        string platformHint = "", bool multiFile = false, string refHeaders = "")
    {
        // Multi-file: each file is filled on its own, so remind the model this is only ONE file of a project —
        // every type/function is already declared in the project header, and it must stay in the SAME language.
        // Single-file: everything is in this one file, so forbid inventing project-local imports.
        // How the other files' declarations are reachable differs by language: C-family via an included header,
        // Go via the shared package (no import of local files), PHP via an autoloader (no require of local files).
        var reachNote = lang switch
        {
            ExportLanguage.Go  => "All other types and functions live in sibling files of the SAME Go package, so "
                                + "you can use them directly WITHOUT importing any local file (only import the "
                                + "standard-library packages this file actually needs).",
            ExportLanguage.Php => "All other classes are loaded by the project's autoloader, so you can use them "
                                + "directly WITHOUT any require/include of local files (do not add require/include "
                                + "statements).",
            ExportLanguage.TypeScript or ExportLanguage.JavaScript =>
                                  "The imports this file needs are ALREADY written at the top — use those "
                                + "types directly and do NOT add, remove or change any import statement.",
            ExportLanguage.Python => "The imports this file needs are ALREADY written at the top (each sibling "
                                + "type is its own module) — use those types directly and do NOT add, remove or "
                                + "change any import statement.",
            ExportLanguage.Kotlin => "All other types and functions are in the SAME package (this file's package) "
                                + "— use them directly; NO import statement is needed for them.",
            ExportLanguage.Swift => "All other types and functions are in the SAME module — use them directly; NO "
                                + "import statement is needed for them (do not `import` any local file). Every "
                                + "parameter is declared with `_` (no argument label), so call functions and "
                                + "methods WITHOUT argument labels: `TestFunktion(x)`, `obj.Compare(a, b)`.",
            ExportLanguage.Rust => "Sibling types are separate modules of this crate; the `use crate::...` imports "
                                + "this file needs are ALREADY written at the top — use those types directly and do "
                                + "NOT add, remove or change any `use` or `mod` statement. Keep every type, method "
                                + "and field name EXACTLY as written in the skeleton — do NOT convert names to "
                                + "snake_case; other files call these methods by their exact names, so renaming "
                                + "breaks the build.",
            _                  => "Every type and function is already declared in the project's header, which this "
                                + "file includes.",
        };
        var multiNote = multiFile
            ? $" IMPORTANT: This is ONE file (\"{fileName}\") of a multi-file {lang} project. {reachNote} Do NOT "
              + $"redeclare or redefine any type or function, and do NOT switch to another language: write {lang} "
              + "ONLY. Fill in the bodies for THIS file only, keeping its structure exactly as given."
            : " This is a SINGLE self-contained source file — every type and function you need is already defined "
              + "in it. Only standard-library imports are allowed; do NOT import or reference any project-local "
              + "module/header (there are none).";
        var system =
            $"You are a senior {lang} programmer. You complete code skeletons generated from program-flow "
            + "diagrams. "
            + $"The statements already inside the bodies are PSEUDOCODE from the diagram and may use ANOTHER "
            + $"language's syntax (e.g. `new`/`delete`, BASIC, Pascal, Python). TRANSLATE them into idiomatic "
            + $"{lang}. The target language is ALWAYS {lang}, no matter what syntax the pseudocode uses — never "
            + $"switch languages or infer the language from the body content. "
            + "A `delete x` / `delete(x)` statement is object TEARDOWN — translate it to the target language's "
            + "idiom, and if (and ONLY if) the class actually declares a cleanup method (e.g. Dispose/Close/close), "
            + "call that first; NEVER call a cleanup method the class does not declare. In garbage-collected "
            + "languages (C#, Java, JavaScript, TypeScript, Python, Kotlin, Swift, PHP, Go) there is no manual "
            + "delete: just release the reference (e.g. `x = null`/`x = None`/`x = nil`) or simply let it go out "
            + "of scope — but if you reassign a reference to null, it must be declared with a MUTABLE binding "
            + "(`let`/`var`, not `const`/`val`/`final`); if it was declared immutable, just let it go out of scope "
            + "instead. In C++ use `delete x;` "
            + "(optionally `x = nullptr;` after). In C use the generated `Type_free(x)`. "
            + "The structure is FIXED — implement method/function BODIES only. Do NOT change any "
            + "declaration: keep every namespace, type, signature, parameter, return type, visibility "
            + "(public/private/protected) and static/instance modifier EXACTLY as given. Do NOT add, remove or "
            + "rename members, fields or constructors, and do NOT introduce design patterns (no singletons, no "
            + "extra helper types) to make calls fit — if a call looks inconsistent, implement the body as-is and "
            + "leave the declarations untouched. "
            + "Do NOT add or alter any forward declarations or function prototypes: the declarations given here "
            + "(and in the project header) are the SINGLE source of truth, and every definition MUST match its "
            + "declaration EXACTLY — same name, parameters and return type (e.g. never write `void main()` when "
            + "it is declared `int main()`). Build everything to conform to those declarations; the file must stay "
            + "consistent with them. Implement every body so the file compiles and behaves sensibly. "
            + "You MAY add the #include / import / using directives the code needs at the top of the file — but "
            + "NOTHING else at file scope (no new forward declarations, functions or types). "
            + "Reply with ONLY the complete source file: no markdown fences, no commentary."
            + LangHints(lang) + platformHint + multiNote;

        // Give the model the project's header declarations so it uses the EXACT function/type names (in
        // multi-file mode it otherwise can't see them and invents names).
        var refBlock = string.IsNullOrWhiteSpace(refHeaders) ? ""
            : "Project declarations you MUST use verbatim (do not invent or rename functions/types):\n\n"
              + refHeaders + "\n\n";
        var messages = new List<CloudAIMessage> { new("user", refBlock + "Complete this skeleton:\n\n" + skeleton) };
        var sb       = new System.Text.StringBuilder();

        var reply = await svc.SendAsync(messages, system);
        sb.Append(reply);

        int rounds = 0;
        while (svc.LastFinishReason == FinishReason.Length && rounds++ < maxCont)
        {
            report(PluginLoc.Tf("gen_continuing", fileName, rounds));
            messages.Add(new CloudAIMessage("assistant", reply));
            messages.Add(new CloudAIMessage("user",
                "Continue the source code EXACTLY where you left off. Do not repeat anything already written, "
              + "do not add explanations or markdown fences — output only the remaining code. "
              // Restate the key rules so they survive even if the earlier skeleton scrolls out of a small window.
              + "Reminder: keep every declaration/signature UNCHANGED, implement bodies only, and add any needed "
              + "imports / #include directives at the top."));
            reply = await svc.SendAsync(messages, system);
            sb.Append(reply);
        }

        var complete = svc.LastFinishReason != FinishReason.Length;   // false only if still truncated at the cap
        return (StripFences(sb.ToString()), complete);
    }

    // Remove any markdown fence lines (```), wherever they appear — robust against fences that show up at the
    // seams when a truncated reply is continued across several calls.
    // Target platforms offered for native languages. Label is a func so it localizes after PluginLoc.Use.
    static readonly (string Key, Func<string> Label)[] Platforms =
    {
        ("Portable", () => PluginLoc.T("plat_portable")),
        ("Windows",  () => "Windows"),
        ("Linux",    () => "Linux"),
        ("macOS",    () => "macOS"),
    };

    // A system-prompt hint about the target platform — only for native languages (C/C++); empty otherwise.
    static string PlatformHint(ExportLanguage lang, string platform)
    {
        if (lang is not (ExportLanguage.Cpp or ExportLanguage.C)) return "";
        var head = platform == "Portable"
            ? " Target platform: portable — use only portable standard-library APIs; avoid OS-specific calls, or guard them per platform with #ifdef."
            : $" Target platform: {platform} — you may use its native system APIs.";
        // Missing headers for platform APIs is a common compile break, so spell it out.
        return head + " Whenever you use a platform-specific API, also add its required #include " +
               "(e.g. <windows.h> for Sleep, <unistd.h> for sleep/usleep).";
    }

    // Short, language-specific idiom/translation hints appended to the system prompt — the pseudocode in the
    // bodies must be turned into each language's idioms, and these are the gotchas models most often miss.
    static string LangHints(ExportLanguage lang) => lang switch
    {
        ExportLanguage.C =>
            " C has no classes, exceptions, `new`/`delete` or references: create an object with the generated "
            + "`Type_new()`, release it with `Type_free(obj)`, and call methods as `Type_method(obj, args)`. "
            + "Use printf for output and malloc/free for memory.",
        ExportLanguage.Cpp =>
            " Use idiomatic C++: std::string, std::cout for output, `new`/`delete` (or RAII), and "
            + "std::this_thread::sleep_for(std::chrono::seconds(n)) (include <thread>/<chrono>) for delays.",
        ExportLanguage.Java =>
            " Java has no free functions (use the given static methods) and no destructors (implement "
            + "AutoCloseable if cleanup is needed). Use System.out.println for output and try/catch for errors.",
        ExportLanguage.Python =>
            " Python uses indentation, no braces/semicolons; instance methods take `self`; use print() for output.",
        ExportLanguage.Go =>
            " Go returns errors (no exceptions); use fmt.Println for output and time.Sleep for delays; methods "
            + "have a receiver. All types live in ONE flat package, so drop any `Namespace.` prefix when "
            + "referring to a type (e.g. `TestSpace.TestKlasse` → `TestKlasse`); construct with `&Type{}` or the "
            + "generated `NewType(...)`.",
        ExportLanguage.Rust =>
            " Rust has no exceptions (use Result or panic!) and enforces ownership/borrowing; use println! for "
            + "output and std::thread::sleep for delays.",
        ExportLanguage.TypeScript =>
            " Use console.log for output, `new` for objects, and no manual memory management.",
        ExportLanguage.JavaScript =>
            " Use console.log for output and `new` for objects; no manual memory management. JavaScript has NO "
            + "namespaces — every class is top-level, so drop any `Namespace.` prefix when referring to a class "
            + "(e.g. `TestSpace.TestKlasse` → `TestKlasse`). Match method names case-sensitively.",
        ExportLanguage.Kotlin =>
            " Kotlin: println for output, `val`/`var`, no `new` (call the constructor directly). All types are in "
            + "ONE flat package, so drop any `Namespace.` prefix when referring to a type "
            + "(e.g. `TestSpace.TestKlasse` → `TestKlasse`).",
        ExportLanguage.Swift =>
            " Swift: print for output, no `new` (call the initializer), and optionals where a value may be absent. "
            + "All types are in ONE module, so drop any `Namespace.` prefix when referring to a type "
            + "(e.g. `TestSpace.TestKlasse` → `TestKlasse`).",
        ExportLanguage.Php =>
            " PHP: variables start with `$`, `echo` for output, `->` for member access, `new` for objects.",
        _ => "",
    };

    // A model whose name marks it as a pretrained BASE (not instruction/chat-tuned). Such models don't follow
    // the prompt format and produce garbage for codegen — matched by a standalone "base" token in the name.
    static bool LooksLikeBaseModel(string model) =>
        !string.IsNullOrWhiteSpace(model) &&
        System.Text.RegularExpressions.Regex.IsMatch(model, @"(?<![a-z])base(?![a-z])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // The build/run command for a generated language, shown in the result window. Empty = single source file.
    static string BuildHint(ExportLanguage lang, bool multiFile) => lang switch
    {
        ExportLanguage.CSharp                          => "dotnet build",
        ExportLanguage.Cpp or ExportLanguage.C         => "cmake . && cmake --build .",
        ExportLanguage.Java                            => "find . -name \"*.java\" | xargs javac",
        ExportLanguage.TypeScript                      => "npm install && npm run build && node dist/" + (multiFile ? "index.js" : "main.js"),
        ExportLanguage.JavaScript                      => "npm install && node src/" + (multiFile ? "index.js" : "main.js"),
        ExportLanguage.Python                          => multiFile ? "python __main__.py" : "python main.py",
        ExportLanguage.Go                              => "go build",
        ExportLanguage.Rust                            => "cargo run",
        ExportLanguage.Kotlin                          => "gradle build",
        ExportLanguage.Php                             => multiFile ? "php index.php" : "php main.php",
        ExportLanguage.Swift                           => "swiftc " + (multiFile ? "*.swift" : "main.swift") + " -o app && ./app",
        _                                              => "",
    };

    // A focused C/C++ pass: ask the model ONLY for the #include lines the file still needs (for the APIs it uses,
    // correct for the platform), then prepend the missing ones. Small, safe response — it never rewrites the file
    // (so no truncation / code loss) and only ADDS includes not already present. Best-effort; failure = no change.
    static async Task<string> FixIncludes(ICloudAIService svc, ExportLanguage lang, string content, string platformHint)
    {
        var system =
            $"You are a senior {lang} programmer. Given the source file below, output ONLY the additional #include "
            + "directives it needs for the functions/APIs it actually uses that are NOT already present"
            + (string.IsNullOrEmpty(platformHint) ? "" : " —" + platformHint)
            + " If an API is used only inside a platform #ifdef branch (e.g. Sleep under _WIN32, usleep/sleep "
            + "otherwise), guard its #include with the SAME #ifdef so it only compiles on that platform. "
            + "Output only preprocessor lines (#include and any #ifdef/#else/#endif needed to guard them) and "
            + "NOTHING else. If none are missing, reply with an empty message.";
        string reply;
        try { reply = await svc.SendAsync(new[] { new CloudAIMessage("user", content) }, system) ?? ""; }
        catch { return content; }

        // Keep only preprocessor lines; prepend the block only if it introduces at least one NEW #include.
        var lines = reply.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim())
            .Where(l => l.StartsWith("#include") || l.StartsWith("#if") || l.StartsWith("#else")
                        || l.StartsWith("#elif") || l.StartsWith("#endif"))
            .ToList();
        var hasNew = lines.Any(l => l.StartsWith("#include") && !content.Contains(l));
        return hasNew ? string.Join("\n", lines) + "\n\n" + content : content;
    }

    // A single self-contained TS/JS file has no sibling modules, so any RELATIVE import (`from './…'`) the model
    // invents is bogus. Strip those lines deterministically (package/std imports like `from 'fs'` are kept).
    static readonly System.Text.RegularExpressions.Regex RelativeImport = new(
        @"(?m)^[ \t]*import\b[^\n]*\bfrom\s+['""]\.[^'""]*['""];?[ \t]*(//[^\n]*)?\r?\n?");
    static string StripLocalImports(string s) => RelativeImport.Replace(s, "");

    // The reference declarations handed to the model are separated by `// ===== <file> =====` markers; weak models
    // sometimes echo one into their output. Strip such marker lines (a `//`/`#` comment of the `=== … ===` shape) —
    // they're never legitimate generated code.
    static readonly System.Text.RegularExpressions.Regex RefMarker = new(
        @"(?m)^[ \t]*(?://|#)[ \t]*={3,}.*={3,}[ \t]*\r?\n?");
    static string StripRefMarkers(string s) => RefMarker.Replace(s, "");

    // JS/TS `const` forbids reassignment, but a translated teardown (`x = null`) or any later `x = …` needs a
    // mutable binding. Deterministically relax a `const NAME =` declaration to `let` whenever NAME is reassigned
    // elsewhere in the file (a plain `NAME = …`, not `==`/`===`/`=>` and not another declaration).
    static string FixConstReassign(string s)
    {
        var text = s.Replace("\r\n", "\n");
        var assigned = new HashSet<string>();
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(text, @"(?m)^[ \t]*([A-Za-z_$][\w$]*)[ \t]*=(?![=>])"))
            assigned.Add(m.Groups[1].Value);
        foreach (var name in assigned)
            text = System.Text.RegularExpressions.Regex.Replace(text,
                $@"(?m)^([ \t]*)const([ \t]+{System.Text.RegularExpressions.Regex.Escape(name)}[ \t]*=)", "$1let$2");
        return text;
    }

    // Languages whose source files end at a closing brace `}` — used to trim trailing model chatter.
    static bool IsBraceLang(ExportLanguage l) =>
        l is ExportLanguage.CSharp or ExportLanguage.Cpp or ExportLanguage.C or ExportLanguage.Java
          or ExportLanguage.TypeScript or ExportLanguage.JavaScript or ExportLanguage.Go or ExportLanguage.Php
          or ExportLanguage.Rust or ExportLanguage.Kotlin or ExportLanguage.Swift;

    // A brace-language source file ends at its last `}`. Some weak models append a natural-language explanation
    // after it despite being told not to — invalid code that breaks the build. If the first non-blank line after
    // the final `}` is NOT a comment (i.e. it's prose), cut everything from the brace onward. A real trailing
    // comment (starts with // /* * #) is kept, so we never delete legitimate trailing content. (Checking only the
    // first line, not code-delimiters, avoids being fooled by prose that quotes code like `wait 3 Seconds;`.)
    static string StripTrailingProse(string s)
    {
        var lines = s.Replace("\r\n", "\n").Split('\n');
        int lastBrace = -1;
        for (int i = lines.Length - 1; i >= 0; i--)
            if (lines[i].TrimEnd().EndsWith("}")) { lastBrace = i; break; }
        if (lastBrace < 0 || lastBrace == lines.Length - 1) return s;   // no brace, or nothing trails it

        int j = lastBrace + 1;
        while (j < lines.Length && lines[j].Trim().Length == 0) j++;
        if (j >= lines.Length) return string.Join("\n", lines.Take(lastBrace + 1)).TrimEnd() + "\n";  // only blanks

        var t = lines[j].Trim();
        if (t.StartsWith("//") || t.StartsWith("/*") || t.StartsWith("*") || t.StartsWith("#")) return s;  // real comment
        return string.Join("\n", lines.Take(lastBrace + 1)).TrimEnd() + "\n";                              // prose → cut
    }

    static string StripFences(string s)
    {
        var lines = s.Replace("\r\n", "\n").Split('\n');
        var kept  = lines.Where(l => !l.TrimStart().StartsWith("```")).ToArray();
        return kept.Length == lines.Length ? s : string.Join("\n", kept).Trim('\n');
    }
}
