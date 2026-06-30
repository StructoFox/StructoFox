namespace StructoFox.AI;

/// <summary>
/// Connects to a llamafile server via its OpenAI-compatible API.
/// llamafile (by Mozilla) bundles a model and llama.cpp into a single executable.
/// Default local URL: http://localhost:8080/v1
/// API key is not required for local instances.
/// </summary>
public sealed class LlamafileService : OpenAICompatibleService
{
    public override string ProviderName => "llamafile";

    public static readonly string DefaultUrl = "http://localhost:8080/v1";

    public LlamafileService(string baseUrl, string apiKey = "")
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
