namespace StructoFox.AI;

/// <summary>
/// Perplexity AI — chat and online-search models with real-time web access.
/// OpenAI-compatible API.  API key required.
/// https://api.perplexity.ai
/// </summary>
public sealed class PerplexityAIService : OpenAICompatibleService
{
    public override string ProviderName => "Perplexity AI";

    public static readonly string[] DefaultModels =
    [
        "sonar-pro",
        "sonar",
        "sonar-reasoning-pro",
        "sonar-reasoning",
    ];

    public PerplexityAIService(string apiKey)
        : base("https://api.perplexity.ai", apiKey) { }
}
