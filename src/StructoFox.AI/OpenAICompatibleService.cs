using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace StructoFox.AI;

/// <summary>
/// Base class for providers that speak the OpenAI /v1/chat/completions format:
/// Groq, OpenRouter, Mistral, etc.
/// </summary>
public abstract class OpenAICompatibleService : ICloudAIService
{
    public abstract string ProviderName { get; }
    public string CurrentModel { get; set; } = "";

    /// <inheritdoc/>
    public int MaxTokens { get; set; } = 0;

    /// <inheritdoc/>
    public UsageInfo? LastUsage { get; private set; }

    /// <inheritdoc/>
    /// Override in subclasses that know their model's context window precisely.
    public virtual int ContextWindowTokens => 128_000;

    protected readonly HttpClient _http;
    protected readonly string     _baseUrl;

    protected OpenAICompatibleService(
        string  baseUrl,
        string  apiKey,
        string? httpReferer = null,
        string? appTitle    = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http    = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (httpReferer is not null) _http.DefaultRequestHeaders.Add("HTTP-Referer", httpReferer);
        if (appTitle   is not null) _http.DefaultRequestHeaders.Add("X-Title",       appTitle);
    }

    public virtual async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var r = await _http.GetAsync($"{_baseUrl}/models", ct);
            return r.IsSuccessStatusCode || (int)r.StatusCode == 401;
        }
        catch { return false; }
    }

    public virtual async Task<List<string>> GetModelsAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync($"{_baseUrl}/models", ct);
        using var doc = JsonDocument.Parse(json);
        var names = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var arr))
            foreach (var m in arr.EnumerateArray())
                if (m.TryGetProperty("id", out var id))
                    names.Add(id.GetString() ?? "");
        return names;
    }

    public async Task<string> SendAsync(
        IReadOnlyList<CloudAIMessage> messages,
        string? system = null,
        CancellationToken ct = default)
    {
        using var content  = BuildContent(messages, stream: false, system);
        using var response = await _http.PostAsync($"{_baseUrl}/chat/completions", content, ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement
                  .GetProperty("choices")[0]
                  .GetProperty("message")
                  .GetProperty("content")
                  .GetString() ?? string.Empty;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<CloudAIMessage> messages,
        string? system = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions")
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
            var json = line["data: ".Length..].Trim();
            if (json == "[DONE]") yield break;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Usage chunk: arrives just before [DONE] when stream_options.include_usage=true.
            // Has empty or missing choices array and a top-level "usage" object.
            if (root.TryGetProperty("usage", out var usageEl) &&
                usageEl.ValueKind == JsonValueKind.Object)
            {
                var input  = usageEl.TryGetProperty("prompt_tokens",     out var pt) ? pt.GetInt32() : 0;
                var output = usageEl.TryGetProperty("completion_tokens", out var ct2) ? ct2.GetInt32() : 0;
                if (input > 0 || output > 0)
                    LastUsage = new UsageInfo(input, output);
            }

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.GetArrayLength() == 0) continue;

            var delta = choices[0].GetProperty("delta");
            if (delta.TryGetProperty("content", out var contentEl))
            {
                var token = contentEl.GetString();
                if (!string.IsNullOrEmpty(token)) yield return token;
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
        writer.WriteString ("model",  CurrentModel);
        writer.WriteBoolean("stream", stream);
        if (MaxTokens > 0) writer.WriteNumber("max_tokens", MaxTokens);
        if (stream)
        {
            // Ask provider to include token-usage data in the final stream chunk.
            // Providers that don't support this field silently ignore it.
            writer.WriteStartObject("stream_options");
            writer.WriteBoolean("include_usage", true);
            writer.WriteEndObject();
        }
        writer.WriteStartArray("messages");

        if (!string.IsNullOrEmpty(system))
        {
            writer.WriteStartObject();
            writer.WriteString("role",    "system");
            writer.WriteString("content", system);
            writer.WriteEndObject();
        }

        foreach (var m in messages)
        {
            if (m.Role is "system") continue; // already handled above
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
