using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace StructoFox.AI;

public sealed class AnthropicService : ICloudAIService
{
    public string ProviderName => "Anthropic";

    private const string MessagesUrl  = "https://api.anthropic.com/v1/messages";
    private const string ModelsUrl    = "https://api.anthropic.com/v1/models";
    private const string AnthropicVer = "2023-06-01";

    public static readonly string[] DefaultModels =
    [
        "claude-sonnet-4-20250514",
        "claude-haiku-4-5-20251001",
        "claude-opus-4-20250514",
    ];

    private readonly HttpClient _http;
    private int _pendingInputTokens;

    public string CurrentModel { get; set; } = DefaultModels[0];

    /// <inheritdoc/>
    /// Anthropic requires max_tokens — 0 means "use default" (4096 sent to the API).
    public int MaxTokens { get; set; } = 0;

    /// <inheritdoc/>
    public UsageInfo? LastUsage { get; private set; }

    /// <inheritdoc/>
    public string? LastFinishReason { get; private set; }

    /// <inheritdoc/>
    /// All current Claude models have a 200 K context window.
    public int ContextWindowTokens => 200_000;

    public AnthropicService(string apiKey)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.Add("x-api-key",         apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", AnthropicVer);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var r = await _http.GetAsync(ModelsUrl, ct);
            return r.StatusCode is System.Net.HttpStatusCode.OK
                                or System.Net.HttpStatusCode.Unauthorized;
        }
        catch { return false; }
    }

    public async Task<List<string>> GetModelsAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync(ModelsUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var names = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var arr))
                foreach (var m in arr.EnumerateArray())
                    if (m.TryGetProperty("id", out var id))
                        names.Add(id.GetString() ?? "");
            return names.Count > 0 ? names : [.. DefaultModels];
        }
        catch { return [.. DefaultModels]; }
    }

    public async Task<string> SendAsync(
        IReadOnlyList<CloudAIMessage> messages,
        string? system = null,
        CancellationToken ct = default)
    {
        using var content  = BuildContent(messages, stream: false, system);
        using var response = await _http.PostAsync(MessagesUrl, content, ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        LastFinishReason = FinishReason.Normalize(
            root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null);
        return root.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<CloudAIMessage> messages,
        string? system = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, MessagesUrl)
        {
            Content = BuildContent(messages, stream: true, system)
        };

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null && !ct.IsCancellationRequested)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var json = line["data: ".Length..];

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) continue;

            switch (typeEl.GetString())
            {
                case "message_start":
                    // {"type":"message_start","message":{"usage":{"input_tokens":X,...}}}
                    if (root.TryGetProperty("message", out var msgEl) &&
                        msgEl.TryGetProperty("usage", out var startUsage) &&
                        startUsage.TryGetProperty("input_tokens", out var itEl))
                        _pendingInputTokens = itEl.GetInt32();
                    break;

                case "content_block_delta":
                    if (root.TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("text", out var textEl))
                    {
                        var token = textEl.GetString();
                        if (!string.IsNullOrEmpty(token)) yield return token;
                    }
                    break;

                case "message_delta":
                    // {"type":"message_delta","usage":{"output_tokens":Y}}
                    if (root.TryGetProperty("usage", out var deltaUsage) &&
                        deltaUsage.TryGetProperty("output_tokens", out var otEl))
                        LastUsage = new UsageInfo(_pendingInputTokens, otEl.GetInt32());
                    break;

                case "message_stop":
                    yield break;
            }
        }
    }

    private StringContent BuildContent(
        IReadOnlyList<CloudAIMessage> messages,
        bool stream,
        string? system = null)
    {
        using var ms     = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);

        writer.WriteStartObject();
        writer.WriteString ("model",      CurrentModel);
        writer.WriteNumber ("max_tokens", MaxTokens > 0 ? MaxTokens : 4096); // Anthropic requires this field
        writer.WriteBoolean("stream",     stream);
        if (!string.IsNullOrEmpty(system))
            writer.WriteString("system", system);

        writer.WriteStartArray("messages");
        foreach (var m in messages)
        {
            if (m.Role is "system") continue; // Anthropic uses top-level system field
            writer.WriteStartObject();
            writer.WriteString("role",    m.Role);
            writer.WriteString("content", m.Content);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();

        return new StringContent(Encoding.UTF8.GetString(ms.ToArray()), Encoding.UTF8, "application/json");
    }

    public void Dispose() => _http.Dispose();
}
