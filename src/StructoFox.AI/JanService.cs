namespace StructoFox.AI;

/// <summary>
/// Connects to a Jan desktop app server via its OpenAI-compatible API.
/// Jan is an open-source ChatGPT alternative that runs 100% offline.
/// Default local URL: http://localhost:1337/v1
/// API key is not required for local instances.
/// </summary>
public sealed class JanService : OpenAICompatibleService
{
    public override string ProviderName => "Jan";

    public static readonly string DefaultUrl = "http://localhost:1337/v1";

    public JanService(string baseUrl, string apiKey = "")
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
