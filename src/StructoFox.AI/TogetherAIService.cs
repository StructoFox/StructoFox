namespace StructoFox.AI;

/// <summary>
/// Together AI — large catalogue of open-source models (LLaMA, Mistral, Qwen, …)
/// via an OpenAI-compatible API.  API key required.
/// https://api.together.xyz/v1
/// </summary>
public sealed class TogetherAIService : OpenAICompatibleService
{
    public override string ProviderName => "Together AI";

    public static readonly string[] DefaultModels =
    [
        "meta-llama/Llama-3.3-70B-Instruct-Turbo",
        "meta-llama/Llama-3.2-11B-Vision-Instruct-Turbo",
        "mistralai/Mistral-7B-Instruct-v0.3",
        "Qwen/Qwen2.5-72B-Instruct-Turbo",
    ];

    public TogetherAIService(string apiKey)
        : base("https://api.together.xyz/v1", apiKey) { }
}
