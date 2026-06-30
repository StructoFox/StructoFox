using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using StructoFox.AI;
using StructoFox.Core;

namespace StructoFox.Plugin.AiCodegen;

/// <summary>
/// The plugin's central AI configuration: a grid of model "cards" (à la ClaudetRelay participants, simplified).
/// "Hinzufügen" creates a card; each card picks a provider + model, and for LOCAL providers the server URL is
/// set on the same card. Only providers with a stored API key (or local ones) are offered. We fetch the model
/// list live and ask the model to describe itself — its favourite / least-favourite programming languages become
/// the card's Strengths / Weaknesses.
/// </summary>
internal static class AiConfigWindow
{
    public static void Show(IPluginContext ctx)
    {
        PluginLoc.Use(ctx);
        var win = PluginUi.NewWindow(ctx, PluginLoc.T("cfg_title"), 720, 640);

        var cards = new WrapPanel { Orientation = Orientation.Horizontal };

        void Rebuild()
        {
            cards.Children.Clear();
            var s = AiSettings.Load();
            foreach (var card in s.Cards) cards.Children.Add(BuildCard(ctx, card, Rebuild));
            if (s.Cards.Count == 0)
                cards.Children.Add(PluginUi.Dim(PluginLoc.T("cfg_empty")));
        }

        var add = PluginUi.Btn(PluginLoc.T("cfg_add"));
        add.Click += async (_, _) =>
        {
            var card = new AiModelCard();
            if (await EditDialog(ctx, card, isNew: true))
            {
                var s = AiSettings.Load();
                s.Cards.Add(card);
                s.Save();
                Rebuild();
            }
        };

        var keys = PluginUi.Btn(PluginLoc.T("cfg_keys"));
        keys.Click += (_, _) => ApiKeysWindow.Show(ctx);

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 12) };
        toolbar.Children.Add(add);
        toolbar.Children.Add(keys);

        var root = new DockPanel { Margin = new(16) };
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(new ScrollViewer { Content = cards });

        win.Content = root;
        Rebuild();
        win.Open(ctx);
    }

    static Border BuildCard(IPluginContext ctx, AiModelCard card, Action rebuild)
    {
        var inner = new StackPanel { Spacing = 2 };

        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(card.Name) ? (string.IsNullOrWhiteSpace(card.Model) ? PluginLoc.T("cfg_new") : card.Model) : card.Name,
            FontWeight = FontWeight.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        inner.Children.Add(title);

        inner.Children.Add(new TextBlock { Text = card.Provider, Opacity = 0.7, FontSize = 11 });
        if (!string.IsNullOrWhiteSpace(card.Model))
            inner.Children.Add(new TextBlock { Text = card.Model, Opacity = 0.7, FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis });

        if (!string.IsNullOrWhiteSpace(card.Role))
            inner.Children.Add(new TextBlock { Text = card.Role, FontStyle = FontStyle.Italic, FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(120, 170, 255)), Margin = new(0, 4, 0, 0) });

        if (!string.IsNullOrWhiteSpace(card.Strengths))
            inner.Children.Add(new TextBlock { Text = "💪 " + card.Strengths, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(90, 190, 110)), Margin = new(0, 4, 0, 0) });
        if (!string.IsNullOrWhiteSpace(card.Weaknesses))
            inner.Children.Add(new TextBlock { Text = "🚫 " + card.Weaknesses, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(210, 140, 70)) });
        if (!string.IsNullOrWhiteSpace(card.LastApiError))
            inner.Children.Add(new TextBlock { Text = "⚠ " + card.LastApiError, FontSize = 10, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(210, 80, 60)), Margin = new(0, 4, 0, 0) });

        var border = new Border
        {
            Width = 210, Margin = new(0, 0, 12, 12), Padding = new(14, 12),
            CornerRadius = new(8), BorderThickness = new(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
            Background = new SolidColorBrush(Color.FromArgb(20, 128, 128, 128)),
            Opacity = card.Enabled ? 1.0 : 0.5,
            Child = inner,
        };

        var menu = new ContextMenu();
        var edit = new MenuItem { Header = PluginLoc.T("menu_edit") };
        edit.Click += async (_, _) => { if (await EditDialog(ctx, card, isNew: false)) { Persist(card); rebuild(); } };
        var toggle = new MenuItem { Header = PluginLoc.T(card.Enabled ? "menu_disable" : "menu_enable") };
        toggle.Click += (_, _) => { card.Enabled = !card.Enabled; Persist(card); rebuild(); };
        var del = new MenuItem { Header = PluginLoc.T("menu_remove") };
        del.Click += (_, _) =>
        {
            var s = AiSettings.Load();
            s.Cards.RemoveAll(c => Same(c, card));
            s.Save();
            rebuild();
        };
        menu.Items.Add(edit);
        menu.Items.Add(toggle);
        menu.Items.Add(new Separator());
        menu.Items.Add(del);
        border.ContextMenu = menu;

        return border;
    }

    // ── Edit dialog ─────────────────────────────────────────────────────────

    static async Task<bool> EditDialog(IPluginContext ctx, AiModelCard card, bool isNew)
    {
        var dlg = PluginUi.NewWindow(ctx, PluginLoc.T(isNew ? "edit_add" : "edit_edit"), 460, 600);

        var panel = new StackPanel { Margin = new(20) };

        // Name
        panel.Children.Add(PluginUi.Label(PluginLoc.T("f_name")));
        var nameBox = new TextBox { Text = card.Name, PlaceholderText = PluginLoc.T("f_name_ph") };
        panel.Children.Add(nameBox);

        // Provider — only selectable (local, or cloud with a key)
        panel.Children.Add(PluginUi.Label(PluginLoc.T("f_provider")));
        var selectable = AiProviders.Selectable();
        var provCombo  = PluginUi.Combo();
        foreach (var p in selectable)
            provCombo.Items.Add(new ComboBoxItem { Content = p.Display + (p.Kind == AiProviderKind.Local ? PluginLoc.T("local_suffix") : ""), Tag = p });
        provCombo.SelectedIndex = Math.Max(0, selectable.ToList().FindIndex(p => p.Id == card.Provider));
        panel.Children.Add(provCombo);
        if (selectable.Count == 0)
            panel.Children.Add(PluginUi.Dim(PluginLoc.T("no_providers")));

        AiProviderInfo? Prov() => (provCombo.SelectedItem as ComboBoxItem)?.Tag as AiProviderInfo;

        // Server URL — local providers only
        var urlLabel = PluginUi.Label(PluginLoc.T("f_serverurl"));
        var urlBox   = new TextBox { Text = card.ServerUrl, PlaceholderText = "http://localhost:11434/v1" };
        panel.Children.Add(urlLabel);
        panel.Children.Add(urlBox);

        // Model + fetch
        panel.Children.Add(PluginUi.Label(PluginLoc.T("f_model")));
        var currentModels = new List<string>();   // the models currently offered (defaults or fetched)
        var modelBox = new AutoCompleteBox
        {
            Text = card.Model, PlaceholderText = PluginLoc.T("model_ph"),
            FilterMode = AutoCompleteFilterMode.Custom, MinimumPrefixLength = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        PluginUi.Theme(modelBox, TemplatedControl.ForegroundProperty, "SidebarTextBrush");
        PluginUi.Theme(modelBox, TemplatedControl.BackgroundProperty,  "ControlBgBrush");
        PluginUi.Theme(modelBox, TemplatedControl.BorderBrushProperty, "ControlBorderBrush");
        // Custom filter: empty text or a text that already equals a full model name → show the WHOLE list
        // (so the dropdown stays browsable after a pick); otherwise narrow by substring.
        modelBox.ItemFilter = (search, item) =>
        {
            var s = (search ?? "").Trim();
            if (s.Length == 0) return true;
            if (currentModels.Any(m => string.Equals(m, s, StringComparison.OrdinalIgnoreCase))) return true;
            return (item?.ToString() ?? "").Contains(s, StringComparison.OrdinalIgnoreCase);
        };
        // Always pop the dropdown open on focus, so the user can browse without clearing the field first.
        modelBox.GotFocus += (_, _) => { if (currentModels.Count > 0) modelBox.IsDropDownOpen = true; };
        var fetch  = PluginUi.Btn("↻"); fetch.Margin = new(6, 0, 0, 0);
        var status = PluginUi.Dim("");
        var modelRow = new Grid { ColumnDefinitions = new("*,Auto") };
        Grid.SetColumn(modelBox, 0); modelRow.Children.Add(modelBox);
        Grid.SetColumn(fetch, 1);    modelRow.Children.Add(fetch);
        panel.Children.Add(modelRow);
        panel.Children.Add(status);

        // Default URLs of all local providers — so when we pre-fill we only overwrite a still-default value
        // (never a URL the user typed themselves).
        var localDefaults = AiProviders.All
            .Where(p => p.Kind == AiProviderKind.Local).Select(p => p.DefaultUrl).ToHashSet();

        void SyncProviderUi()
        {
            var p     = Prov();
            var local = p?.Kind == AiProviderKind.Local;
            urlLabel.IsVisible = local;
            urlBox.IsVisible   = local;
            if (local && (string.IsNullOrWhiteSpace(urlBox.Text) || localDefaults.Contains(urlBox.Text.Trim())))
                urlBox.Text = p!.DefaultUrl;   // pre-fill the provider's default URL as an editable value
        }
        SyncProviderUi();
        provCombo.SelectionChanged += (_, _) => SyncProviderUi();

        void SetModels(IEnumerable<string> models)
        {
            currentModels.Clear();
            currentModels.AddRange(models);
            modelBox.ItemsSource = currentModels.ToList();
        }
        void PopulateDefaults() => SetModels(Prov() is { } p ? DefaultModels(p.Id) : []);
        PopulateDefaults();
        provCombo.SelectionChanged += (_, _) => PopulateDefaults();

        var cts = new System.Threading.CancellationTokenSource();
        dlg.Closed += (_, _) => cts.Cancel();

        fetch.Click += async (_, _) =>
        {
            var p = Prov();
            if (p is null) return;
            // build a temp card to construct the service with the current url
            var probe = new AiModelCard { Provider = p.Id, ServerUrl = urlBox.Text?.Trim() ?? "" };
            if (p.Kind == AiProviderKind.Cloud && !KeyStore.Has(p.Id))
            { status.Text = PluginLoc.T("st_nokey"); return; }
            fetch.IsEnabled = false; status.Text = PluginLoc.T("st_loading");
            try
            {
                using var svc = AiProviders.Create(probe);
                var models = await svc.GetModelsAsync(cts.Token);
                SetModels(models);
                // Keep what the user already had; do NOT auto-pick the first model (let the dropdown show all).
                status.Text = PluginLoc.Tf("st_found", models.Count);
                if (currentModels.Count > 0) modelBox.IsDropDownOpen = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { status.Text = "⚠ " + ex.Message; }
            finally { fetch.IsEnabled = true; }
        };

        // Max tokens
        panel.Children.Add(PluginUi.Label(PluginLoc.T("f_maxtokens")));
        var maxBox = new TextBox { Text = card.MaxTokens.ToString() };
        panel.Children.Add(maxBox);

        // Max continuations — the safety cap on "continue the cut-off reply" rounds (matters most for local
        // models with a small context window). Localized mouse-over explains it.
        panel.Children.Add(PluginUi.Label(PluginLoc.T("f_maxcont")));
        var contBox = new TextBox { Text = card.MaxContinuations.ToString() };
        ToolTip.SetTip(contBox, PluginLoc.T("tip_maxcont"));
        panel.Children.Add(contBox);

        // Self-describe
        var describe = PluginUi.Btn(PluginLoc.T("btn_describe")); describe.Margin = new(0, 14, 0, 0);
        var spinner  = new ProgressBar { IsIndeterminate = true, IsVisible = false, Height = 4, Margin = new(0, 6, 0, 0) };
        var descStatus = PluginUi.Dim("");
        var busy = false;   // guard re-entry WITHOUT disabling the button (disabled state would grey the label)
        describe.Click += async (_, _) =>
        {
            if (busy) return;
            ApplyTo(card); // capture current selections first
            if (string.IsNullOrWhiteSpace(card.Model)) { descStatus.Text = PluginLoc.T("st_pickmodel"); return; }
            busy = true; spinner.IsVisible = true; descStatus.Text = PluginLoc.T("st_asking");
            try
            {
                await CodeSelfDescription.FetchAsync(card, cts.Token);
                descStatus.Text = string.IsNullOrWhiteSpace(card.LastApiError)
                    ? $"✓ {card.Role}\n💪 {card.Strengths}\n🚫 {card.Weaknesses}"
                    : "⚠ " + card.LastApiError;
            }
            catch (Exception ex) { descStatus.Text = "⚠ " + ex.Message; }
            finally { busy = false; spinner.IsVisible = false; }
        };
        panel.Children.Add(describe);
        panel.Children.Add(spinner);
        panel.Children.Add(descStatus);

        // Buttons
        var save   = PluginUi.Btn(PluginLoc.T("btn_save"));
        var cancel = PluginUi.Btn(PluginLoc.T("btn_cancel"));
        save.Click += (_, _) =>
        {
            if (Prov() is null) { status.Text = PluginLoc.T("st_noprovider"); return; }
            if (string.IsNullOrWhiteSpace(modelBox.Text)) { status.Text = PluginLoc.T("st_nomodel"); return; }
            ApplyTo(card);
            dlg.Close(true);
        };
        cancel.Click += (_, _) => dlg.Close(false);
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 18, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right };
        btnRow.Children.Add(save); btnRow.Children.Add(cancel);
        panel.Children.Add(btnRow);

        void ApplyTo(AiModelCard c)
        {
            c.Name      = nameBox.Text?.Trim() ?? "";
            c.Provider  = Prov()?.Id ?? c.Provider;
            c.Model     = modelBox.Text?.Trim() ?? "";
            c.ServerUrl = urlBox.Text?.Trim() ?? "";
            c.MaxTokens = int.TryParse(maxBox.Text, out var mt) ? mt : 0;
            c.MaxContinuations = int.TryParse(contBox.Text, out var mc) && mc >= 0 ? mc : 8;
        }

        dlg.Content = new ScrollViewer { Content = panel };

        // True modal when we have an owner; otherwise a non-modal window bridged through a TCS.
        if (ctx.OwnerWindow is Window owner)
            return await dlg.ShowDialog<bool>(owner);

        var tcs = new TaskCompletionSource<bool>();
        dlg.Closed += (_, e) => tcs.TrySetResult(false);
        // dlg.Close(true) sets the Window's result, but without ShowDialog we capture it via Closing.
        dlg.Show();
        return await tcs.Task;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    static string[] DefaultModels(string provider) => provider switch
    {
        "Anthropic"  => AnthropicService.DefaultModels,
        "OpenAI"     => OpenAIService.DefaultModels,
        "Google"     => GoogleAIService.DefaultModels,
        "Groq"       => GroqService.DefaultModels,
        "Mistral"    => MistralService.DefaultModels,
        "OpenRouter" => OpenRouterService.DefaultModels,
        "xAI (Grok)" => XAIGrokService.DefaultModels,
        "Cerebras"   => CerebrasService.DefaultModels,
        "DeepInfra"  => DeepInfraService.DefaultModels,
        "DeepSeek"   => DeepSeekService.DefaultModels,
        "Nvidia NIM" => NvidiaNIMService.DefaultModels,
        _            => [],
    };

    static bool Same(AiModelCard a, AiModelCard b) =>
        a.Provider == b.Provider && a.Model == b.Model && a.Name == b.Name;

    static void Persist(AiModelCard card)
    {
        var s = AiSettings.Load();
        var i = s.Cards.FindIndex(c => Same(c, card));
        if (i >= 0) s.Cards[i] = card; else s.Cards.Add(card);
        s.Save();
    }
}
