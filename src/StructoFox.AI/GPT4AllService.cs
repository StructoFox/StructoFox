namespace StructoFox.AI;

/// <summary>
/// Connects to a GPT4All local server via its OpenAI-compatible API.
/// Enable "Enable API server" in GPT4All Settings → Application to activate.
/// Default local URL: http://localhost:4891/v1
/// API key is not required for local instances.
/// </summary>
public sealed class GPT4AllService : OpenAICompatibleService
{
    public override string ProviderName => "GPT4All";

    public static readonly string DefaultUrl = "http://localhost:4891/v1";

    public GPT4AllService(string baseUrl, string apiKey = "")
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
