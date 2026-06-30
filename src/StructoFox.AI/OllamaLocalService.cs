namespace StructoFox.AI;

/// <summary>
/// Local Ollama via its OpenAI-compatible endpoint (default http://localhost:11434/v1). No API key needed.
/// (For Ollama Cloud at api.ollama.com use <see cref="OllamaOpenAIService"/> instead.)
/// </summary>
public sealed class OllamaLocalService : OpenAICompatibleService
{
    public static readonly string DefaultUrl = "http://localhost:11434/v1";

    public override string ProviderName => "Ollama";

    public OllamaLocalService(string baseUrl, string apiKey = "")
        : base(string.IsNullOrWhiteSpace(baseUrl) ? DefaultUrl : baseUrl, apiKey) { }
}
