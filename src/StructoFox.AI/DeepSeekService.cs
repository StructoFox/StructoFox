namespace StructoFox.AI;

/// <summary>
/// DeepSeek — high-performance reasoning and chat models (DeepSeek-R1, V3, …).
/// OpenAI-compatible API.  API key required.
/// https://api.deepseek.com/v1
/// </summary>
public sealed class DeepSeekService : OpenAICompatibleService
{
    public override string ProviderName => "DeepSeek";

    public static readonly string[] DefaultModels =
    [
        "deepseek-chat",
        "deepseek-reasoner",
    ];

    public DeepSeekService(string apiKey)
        : base("https://api.deepseek.com/v1", apiKey) { }
}
