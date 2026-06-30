namespace StructoFox.AI;

/// <summary>
/// DeepInfra — budget-friendly hosting for a wide range of open-source models.
/// OpenAI-compatible API.  API key required.
/// https://api.deepinfra.com/v1/openai
/// </summary>
public sealed class DeepInfraService : OpenAICompatibleService
{
    public override string ProviderName => "DeepInfra";

    public static readonly string[] DefaultModels =
    [
        "meta-llama/Llama-3.3-70B-Instruct",
        "meta-llama/Llama-3.1-405B-Instruct",
        "mistralai/Mistral-7B-Instruct-v0.3",
        "Qwen/Qwen2.5-72B-Instruct",
    ];

    public DeepInfraService(string apiKey)
        : base("https://api.deepinfra.com/v1/openai", apiKey) { }
}
