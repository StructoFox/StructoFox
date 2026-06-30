using System.IO;
using System.Reflection;
using System.Text.Json;
using StructoFox.Core;

namespace StructoFox.Plugin.AiCodegen;

/// <summary>
/// Localization for this plugin. English is the built-in default (always present); a translator can drop a
/// <c>Languages/{lang}.json</c> file (a flat {key: text} object) next to the plugin DLL to add or override a
/// language. <see cref="Use"/> is called when a command starts (it knows the host's current language).
/// <c>en.json</c> is shipped as a ready-to-copy template for new translations.
/// </summary>
internal static class PluginLoc
{
    static Dictionary<string, string> _cur = new();

    // Menu titles are read before any command runs (so before Use), so seed from the OS UI culture; Use then
    // refines to the host's exact language once a command supplies the context. The static ctor runs after all
    // field initializers, so Builtin is populated by the time Apply reads it.
    static PluginLoc() => Apply(System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

    /// <summary>Loads the strings for the host's current language (English built-ins + optional JSON overlay).</summary>
    public static void Use(IPluginContext ctx) => Apply(ctx.Language ?? "en");

    static void Apply(string lang)
    {
        var map = new Dictionary<string, string>(Builtin);
        lang = lang.Trim().ToLowerInvariant();

        // Overlay a language file if one exists next to the DLL (translators add e.g. Languages/fr.json).
        // English needs no file — it's the built-in baseline (en.json is shipped only as a template).
        if (lang != "en")
            Overlay(map, LangFile(lang));
        _cur = map;
    }

    public static string T(string key)  => _cur.TryGetValue(key, out var v) ? v : key;
    public static string Tf(string key, params object[] args) => string.Format(T(key), args);

    static string LangFile(string lang)
    {
        var dir = Path.GetDirectoryName(typeof(PluginLoc).Assembly.Location) ?? AppContext.BaseDirectory;
        return Path.Combine(dir, "Languages", lang + ".json");
    }

    static void Overlay(Dictionary<string, string> map, string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (loaded is null) return;
            foreach (var (k, v) in loaded) if (!string.IsNullOrEmpty(v)) map[k] = v;
        }
        catch { /* a broken language file must never break the plugin — keep the built-in strings */ }
    }

    /// <summary>The English baseline AND the canonical key list. Keep en.json in sync with this for translators.</summary>
    public static readonly Dictionary<string, string> Builtin = new()
    {
        // Commands
        ["cmd_generate"] = "🤖  Generate code (AI)",
        ["cmd_config"]   = "⚙  AI configuration",

        // Config window
        ["cfg_title"]    = "AI configuration",
        ["cfg_add"]      = "➕  Add model",
        ["cfg_keys"]     = "🔑  API keys…",
        ["cfg_empty"]    = "No models yet. Click “Add model”.",
        ["cfg_new"]      = "(new)",
        ["menu_edit"]    = "Edit",
        ["menu_enable"]  = "Enable",
        ["menu_disable"] = "Disable",
        ["menu_remove"]  = "Remove",
        ["edit_add"]     = "Add model",
        ["edit_edit"]    = "Edit model",
        ["f_name"]       = "Name (optional)",
        ["f_name_ph"]    = "Display name",
        ["f_provider"]   = "Provider",
        ["local_suffix"] = "  (local)",
        ["no_providers"] = "No providers available – add an API key under “API keys…” first, or pick a local provider.",
        ["f_serverurl"]  = "Server URL (local provider)",
        ["f_model"]      = "Model",
        ["model_ph"]     = "Type a model name or click ↻",
        ["st_loading"]   = "Loading models…",
        ["st_found"]     = "✓ {0} model(s) found — open the list to choose",
        ["st_nokey"]     = "⚠ No API key for this provider.",
        ["f_maxtokens"]  = "Max. tokens (0 = default)",
        ["f_maxcont"]    = "Max. continuations (0 = default 8)",
        ["tip_maxcont"]  = "How many times the AI may continue a reply that was cut off by the token limit before "
                         + "a file counts as incomplete. Higher helps with longer programs on local models that "
                         + "have a small context window (they need more rounds). Very high values cost more "
                         + "time/tokens. 0 = default (8).",
        ["btn_describe"] = "🔍  Fetch self-description",
        ["st_asking"]    = "Asking the model…",
        ["st_pickmodel"] = "Pick a model first.",
        ["btn_save"]     = "Save",
        ["btn_cancel"]   = "Cancel",
        ["st_noprovider"]= "⚠ No provider selected.",
        ["st_nomodel"]   = "⚠ Please specify a model.",

        // API-keys window
        ["keys_title"]      = "Manage API keys",
        ["keys_intro"]      = "API keys are stored only in the operating system's secret store ({0}) — never in a file.",
        ["keys_unavail"]    = "⚠ The secret store is unavailable. Until it is set up, no API keys can be saved.",
        ["keys_details"]    = "Show details",
        ["keys_unavail_t"]  = "Secret store unavailable",
        ["keys_import"]     = "↧  Import keys from ClaudetRelay",
        ["keys_import_none"]= "No new keys found (ClaudetRelay has none stored, or they already exist here).",
        ["keys_import_done"]= "{0} key(s) imported: {1}",
        ["keys_import_err"] = "Import failed.",
        ["keys_save"]       = "Save",
        ["keys_saved_ph"]   = "•••• stored",
        ["keys_ph"]         = "API key…",
        ["keys_save_err"]   = "Unexpected error while saving.",
        ["keys_del_err"]    = "Unexpected error while removing.",

        // Codegen
        ["gen_title"]      = "Generate code (AI)",
        ["gen_noproject"]  = "No project open.",
        ["gen_nomodel"]    = "No AI model configured. Open “AI configuration” and add a model first.",
        ["gen_model"]      = "Model",
        ["gen_lang"]       = "Target language",
        ["gen_expl"]       = "The skeleton (types, signatures) is generated deterministically from the diagram; "
                           + "the chosen model fills the bodies. You then pick a target folder where a buildable "
                           + "project is written.",
        ["gen_go"]         = "Generate…",
        ["gen_nowin"]      = "No window context.",
        ["gen_folder"]     = "Target folder for the generated project",
        ["gen_skeleton"]   = "Generating skeleton…",
        ["gen_filling"]    = "AI filling {0} …",
        ["gen_continuing"] = "AI continuing {0} … ({1})",
        ["gen_done"]       = "✓ Done. {0} file(s) written.",
        ["gen_done_warn"]  = "⚠ Done with {0} incomplete file(s).",
        ["gen_result_t"]   = "Generated project",
        ["gen_written"]    = "Written to:\n{0}\n\n{1} file(s).{2}\n\nC#: dotnet build   ·   C++: cmake . && cmake --build .",
        ["gen_incomplete"] = "\n\n⚠ {0} file(s) were not finished by the model despite continuation attempts "
                           + "(token/context limit):\n  • {1}\nTip: raise Max. tokens on the model card or split "
                           + "the diagram into smaller functions.",

        // Error dialog
        ["err_title"]   = "Error",
        ["err_details"] = "Technical details",
        ["err_copy"]    = "📋  Copy details",
        ["err_copied"]  = "✓ Copied",
        ["err_close"]   = "Close",
    };
}
