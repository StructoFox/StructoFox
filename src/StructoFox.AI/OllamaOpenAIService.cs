namespace StructoFox.AI;

/// <summary>
/// Connects to the Ollama Cloud API (https://api.ollama.com/v1).
/// API key is obtained from your Ollama account at ollama.com.
/// Handled exactly like other cloud providers — no per-participant base URL.
/// </summary>
public sealed class OllamaOpenAIService : OpenAICompatibleService
{
    public override string ProviderName => "Ollama ☁";

    /// <summary>Populated dynamically via Test Connection from the Ollama Cloud model catalogue.</summary>
    public static readonly string[] DefaultModels = [];

    public OllamaOpenAIService(string apiKey)
        : base("https://api.ollama.com/v1", apiKey)
    { }

    /// <summary>
    /// Ollama Cloud returns 403 (not 401) when a key is invalid but the server is reachable.
    /// Accept both so Test Connection shows "Connected" when the endpoint is up.
    /// </summary>
    public override async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var r = await _http.GetAsync($"{_baseUrl}/models", ct);
            var status = (int)r.StatusCode;
            return r.IsSuccessStatusCode || status == 401 || status == 403;
        }
        catch { return false; }
    }
}
