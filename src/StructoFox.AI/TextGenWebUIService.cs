namespace StructoFox.AI;

/// <summary>
/// Connects to oobabooga's text-generation-webui via its OpenAI-compatible extension.
/// Enable the OpenAI extension in the webui to activate the API.
/// Default local URL: http://localhost:5000/v1
/// API key is not required for local instances.
/// </summary>
public sealed class TextGenWebUIService : OpenAICompatibleService
{
    public override string ProviderName => "text-gen-webui";

    public static readonly string DefaultUrl = "http://localhost:5000/v1";

    public TextGenWebUIService(string baseUrl, string apiKey = "")
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
