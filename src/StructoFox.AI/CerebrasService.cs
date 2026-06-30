namespace StructoFox.AI;

/// <summary>
/// Cerebras — wafer-scale AI chips delivering extremely fast inference.
/// OpenAI-compatible API.  API key required (free tier available).
/// https://api.cerebras.ai/v1
/// </summary>
public sealed class CerebrasService : OpenAICompatibleService
{
    public override string ProviderName => "Cerebras";

    public static readonly string[] DefaultModels =
    [
        "llama-3.3-70b",
        "llama-3.1-8b",
        "qwen-3-32b",
    ];

    public CerebrasService(string apiKey)
        : base("https://api.cerebras.ai/v1", apiKey) { }
}
