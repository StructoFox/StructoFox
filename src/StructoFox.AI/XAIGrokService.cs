namespace StructoFox.AI;

/// <summary>xAI Grok via the OpenAI-compatible /v1/chat/completions endpoint.</summary>
public sealed class XAIGrokService : OpenAICompatibleService
{
    public override string ProviderName => "xAI Grok";

    public static readonly string[] DefaultModels =
    [
        "grok-3",
        "grok-3-fast",
        "grok-3-mini",
        "grok-2",
    ];

    public XAIGrokService(string apiKey)
        : base("https://api.x.ai/v1", apiKey)
    {
        CurrentModel = DefaultModels[0];
    }
}
