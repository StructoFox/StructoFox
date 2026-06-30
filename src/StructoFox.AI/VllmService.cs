namespace StructoFox.AI;

/// <summary>
/// Connects to a vLLM inference server (local or remote) via its OpenAI-compatible API.
/// API key is optional — local instances typically run without authentication.
/// Default local URL: http://localhost:8000/v1
/// </summary>
public sealed class VllmService : OpenAICompatibleService
{
    public override string ProviderName => "vLLM";

    public static readonly string DefaultUrl = "http://localhost:8000/v1";

    public VllmService(string baseUrl, string apiKey = "")
        : base(string.IsNullOrWhiteSpace(baseUrl) ? DefaultUrl : baseUrl, apiKey)
    { }

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
