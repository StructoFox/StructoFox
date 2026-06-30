namespace StructoFox.AI;

/// <summary>
/// NVIDIA NIM — NVIDIA's cloud inference platform for optimised open models.
/// OpenAI-compatible API.  API key (NGC API key) required.
/// https://integrate.api.nvidia.com/v1
/// </summary>
public sealed class NvidiaNIMService : OpenAICompatibleService
{
    public override string ProviderName => "Nvidia NIM";

    public static readonly string[] DefaultModels =
    [
        "meta/llama-3.3-70b-instruct",
        "meta/llama-3.1-405b-instruct",
        "mistralai/mistral-7b-instruct-v0.3",
        "nvidia/llama-3.1-nemotron-70b-instruct",
    ];

    public NvidiaNIMService(string apiKey)
        : base("https://integrate.api.nvidia.com/v1", apiKey) { }
}
