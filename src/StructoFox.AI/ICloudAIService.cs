namespace StructoFox.AI;

/// <summary>Unified message type for all cloud AI providers.</summary>
public record CloudAIMessage(string Role, string Content, string Sender = "");

/// <summary>Contract every cloud AI provider service must implement.</summary>
public interface ICloudAIService : IDisposable
{
    /// <summary>Human-readable provider name, e.g. "Anthropic", "Groq".</summary>
    string ProviderName { get; }

    string CurrentModel { get; set; }

    /// <summary>
    /// Maximum tokens the model may generate in a single reply.
    /// Maps to max_tokens (Anthropic/OpenAI) or maxOutputTokens (Google).
    /// 0 = use provider default.
    /// </summary>
    int MaxTokens { get; set; }

    Task<string> SendAsync(
        IReadOnlyList<CloudAIMessage> messages,
        string? system = null,
        CancellationToken ct = default);

    IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<CloudAIMessage> messages,
        string? system = null,
        CancellationToken ct = default);

    Task<bool>         IsAvailableAsync(CancellationToken ct = default);
    Task<List<string>> GetModelsAsync  (CancellationToken ct = default);

    /// <summary>
    /// Token usage from the last completed StreamAsync or SendAsync call.
    /// Null until the first call completes. Used for context-window bar and AIMEM.SYS accounting.
    /// </summary>
    UsageInfo? LastUsage { get; }

    /// <summary>
    /// Maximum context window for the currently selected model (tokens).
    /// Used to compute the fill ratio of the usage bar.
    /// </summary>
    int ContextWindowTokens { get; }
}
