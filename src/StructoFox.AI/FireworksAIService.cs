namespace StructoFox.AI;

/// <summary>
/// Fireworks AI — fast, cost-efficient inference for popular open-source models.
/// OpenAI-compatible API.  API key required.
/// https://api.fireworks.ai/inference/v1
/// </summary>
public sealed class FireworksAIService : OpenAICompatibleService
{
    public override string ProviderName => "Fireworks AI";

    public static readonly string[] DefaultModels =
    [
        "accounts/fireworks/models/llama-v3p3-70b-instruct",
        "accounts/fireworks/models/llama-v3p1-405b-instruct",
        "accounts/fireworks/models/mixtral-8x22b-instruct",
        "accounts/fireworks/models/qwen2p5-72b-instruct",
    ];

    public FireworksAIService(string apiKey)
        : base("https://api.fireworks.ai/inference/v1", apiKey) { }
}
