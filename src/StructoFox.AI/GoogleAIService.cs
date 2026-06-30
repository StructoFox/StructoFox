using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace StructoFox.AI;

public sealed class GoogleAIService : ICloudAIService
{
    public string ProviderName => "Google AI";

    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    public static readonly string[] DefaultModels =
    [
        "gemini-2.0-flash",
        "gemini-2.0-flash-thinking-exp",
        "gemini-1.5-pro",
        "gemini-1.5-flash",
    ];

    private readonly HttpClient _http;
    private readonly string     _apiKey;

    public string CurrentModel { get; set; } = DefaultModels[0];

    /// <inheritdoc/>
    /// 0 = use default (4096 sent to the API).
    public int MaxTokens { get; set; } = 0;

    /// <inheritdoc/>
    public UsageInfo? LastUsage { get; private set; }

    /// <inheritdoc/>
    /// Gemini 1.5 / 2.0 models have a 1 M token context window.
    public int ContextWindowTokens => 1_000_000;

    public GoogleAIService(string apiKey)
    {
        _apiKey = apiKey;
        _http   = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var r = await _http.GetAsync($"{BaseUrl}/models?key={_apiKey}", ct);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<List<string>> GetModelsAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync($"{BaseUrl}/models?key={_apiKey}", ct);
            using var doc = JsonDocument.Parse(json);
            var names = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var arr))
                foreach (var m in arr.EnumerateArray())
                    if (m.TryGetProperty("name", out var nameEl))
                    {
                        var full = nameEl.GetString() ?? "";
                        // "models/gemini-2.0-flash" → "gemini-2.0-flash"
                        var short_ = full.StartsWith("models/") ? full[7..] : full;
                        if (short_.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
                            names.Add(short_);
                    }
            return names.Count > 0 ? names : [.. DefaultModels];
        }
        catch { return [.. DefaultModels]; }
    }

    public async Task<string> SendAsync(
        IReadOnlyList<CloudAIMessage> messages,
        string? system = null,
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/models/{CurrentModel}:generateContent?key={_apiKey}";
        using var content  = BuildContent(messages, system);
        using var response = await _http.PostAsync(url, content, ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return ExtractText(doc.RootElement);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<CloudAIMessage> messages,
        string? system = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/models/{CurrentModel}:streamGenerateContent?key={_apiKey}&alt=sse";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = BuildContent(messages, system)
        };

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null && !ct.IsCancellationRequested)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var json = line["data: ".Length..].Trim();
            if (string.IsNullOrEmpty(json)) continue;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // usageMetadata is present in every chunk; final chunk has the definitive counts.
            if (root.TryGetProperty("usageMetadata", out var meta))
            {
                var input  = meta.TryGetProperty("promptTokenCount",     out var p) ? p.GetInt32() : 0;
                var output = meta.TryGetProperty("candidatesTokenCount", out var c) ? c.GetInt32() : 0;
                if (input > 0 || output > 0)
                    LastUsage = new UsageInfo(input, output);
            }

            var text = ExtractText(root);
            if (!string.IsNullOrEmpty(text)) yield return text;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var candidates) ||
            candidates.GetArrayLength() == 0) return string.Empty;

        var first = candidates[0];
        if (!first.TryGetProperty("content", out var content)) return string.Empty;
        if (!content.TryGetProperty("parts",  out var parts))  return string.Empty;

        var sb = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            // Skip thinking parts (Gemini Flash Thinking)
            if (part.TryGetProperty("thought", out var thought) && thought.GetBoolean())
                continue;
            if (part.TryGetProperty("text", out var textEl))
                sb.Append(textEl.GetString());
        }
        return sb.ToString();
    }

    private StringContent BuildContent(IReadOnlyList<CloudAIMessage> messages, string? system)
    {
        using var ms     = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);

        writer.WriteStartObject();

        // System instruction (Google top-level field)
        if (!string.IsNullOrEmpty(system))
        {
            writer.WriteStartObject("systemInstruction");
            writer.WriteStartArray("parts");
            writer.WriteStartObject();
            writer.WriteString("text", system);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        // Contents — Google uses "model" instead of "assistant"
        writer.WriteStartArray("contents");
        foreach (var m in messages)
        {
            if (m.Role is "system") continue;
            writer.WriteStartObject();
            writer.WriteString("role", m.Role == "assistant" ? "model" : "user");
            writer.WriteStartArray("parts");
            writer.WriteStartObject();
            writer.WriteString("text", m.Content);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartObject("generationConfig");
        writer.WriteNumber("maxOutputTokens", MaxTokens > 0 ? MaxTokens : 4096);
        writer.WriteEndObject();

        writer.WriteEndObject();
        writer.Flush();

        return new StringContent(Encoding.UTF8.GetString(ms.ToArray()), Encoding.UTF8, "application/json");
    }

    public void Dispose() => _http.Dispose();
}
