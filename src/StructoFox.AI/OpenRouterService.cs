namespace StructoFox.AI;

public sealed class OpenRouterService : OpenAICompatibleService
{
    public override string ProviderName => "OpenRouter";

    public static readonly string[] DefaultModels =
    [
        "anthropic/claude-sonnet-4",
        "google/gemini-2.0-flash",
        "openai/gpt-4o",
        "meta-llama/llama-3.3-70b-instruct",
        "mistralai/mistral-small",
    ];

    // Only surface models from well-known upstream providers.
    // OpenRouter aggregates 600+ entries; we filter to the recognisable ones.
    private static readonly string[] _knownPrefixes =
    [
        "anthropic/", "openai/", "google/", "meta-llama/", "meta/",
        "mistralai/", "x-ai/", "qwen/", "deepseek/", "cohere/",
        "perplexity/", "amazon/", "microsoft/", "nvidia/", "01-ai/",
        "nousresearch/", "databricks/", "inflection/", "ai21/",
        "writer/", "sao10k/", "neversleep/", "pygmalionai/",
    ];

    public OpenRouterService(string apiKey)
        : base("https://openrouter.ai/api/v1", apiKey,
               httpReferer: "https://github.com/ClaudetRelay",
               appTitle:    "ClaudetRelay")
    {
        CurrentModel = DefaultModels[0];
    }

    /// <summary>
    /// Returns a curated subset of OpenRouter models: only models from major recognised
    /// providers, with no variant suffixes (e.g. ":free", ":extended", ":thinking", ":beta"),
    /// sorted alphabetically.  The raw OpenRouter catalogue has 600+ entries — the full
    /// list is overwhelming in a ComboBox drop-down.
    /// </summary>
    public override async Task<List<string>> GetModelsAsync(CancellationToken ct = default)
    {
        var all = await base.GetModelsAsync(ct);

        return all
            .Where(m => _knownPrefixes.Any(p => m.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                     && !m.Contains(':'))          // drop :free / :beta / :extended / :thinking
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
