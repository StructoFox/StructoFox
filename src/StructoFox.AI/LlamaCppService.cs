namespace StructoFox.AI;

/// <summary>
/// Connects to a llama.cpp HTTP server via its OpenAI-compatible API.
/// Run with: llama-server -m model.gguf --port 8080
/// Default local URL: http://localhost:8080/v1
/// API key is not required for local instances.
/// </summary>
public sealed class LlamaCppService : OpenAICompatibleService
{
    public override string ProviderName => "llama.cpp";

    public static readonly string DefaultUrl = "http://localhost:8080/v1";

    public LlamaCppService(string baseUrl, string apiKey = "")
        : base(string.IsNullOrWhiteSpace(baseUrl) ? DefaultUrl : baseUrl, apiKey) { }

    public override async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var r = await _http.GetAsync($"{_baseUrl}/models", ct);
            return r.IsSuccessStatusCode || (int)r.StatusCode is 401 or 403;
        }
        catch { return false; }
    }
}
