namespace StructoFox.AI;

/// <summary>
/// Connects to a KoboldCpp server via its OpenAI-compatible API.
/// KoboldCpp is a popular single-file inference engine especially for creative writing and RP.
/// Default local URL: http://localhost:5001/v1
/// API key is not required for local instances.
/// </summary>
public sealed class KoboldCppService : OpenAICompatibleService
{
    public override string ProviderName => "KoboldCpp";

    public static readonly string DefaultUrl = "http://localhost:5001/v1";

    public KoboldCppService(string baseUrl, string apiKey = "")
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
