namespace StructoFox.AI;

/// <summary>
/// Connects to LM Studio (local GUI app) or LM Studio Cloud via OpenAI-compatible API.
/// Local default: http://localhost:1234/v1  (no API key needed)
/// Cloud:         https://api.lmstudio.ai/v1  (API key required)
/// </summary>
public sealed class LmStudioService : OpenAICompatibleService
{
    public override string ProviderName => _providerName;

    public static readonly string DefaultLocalUrl = "http://localhost:1234/v1";
    public static readonly string DefaultCloudUrl = "https://api.lmstudio.ai/v1";

    private readonly string _providerName;

    public LmStudioService(string baseUrl, string apiKey = "")
        : base(string.IsNullOrWhiteSpace(baseUrl) ? DefaultLocalUrl : baseUrl, apiKey)
    {
        _providerName = baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? "LM Studio ☁"
            : "LM Studio";
    }

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
