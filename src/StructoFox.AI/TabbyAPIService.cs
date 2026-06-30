namespace StructoFox.AI;

/// <summary>
/// Connects to a TabbyAPI server via its OpenAI-compatible API.
/// TabbyAPI is a FastAPI-based server optimised for GPTQ and EXL2 quantised models.
/// Default local URL: http://localhost:5000/v1
/// API key is optional (set in TabbyAPI config.yml).
/// </summary>
public sealed class TabbyAPIService : OpenAICompatibleService
{
    public override string ProviderName => "TabbyAPI";

    public static readonly string DefaultUrl = "http://localhost:5000/v1";

    public TabbyAPIService(string baseUrl, string apiKey = "")
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
