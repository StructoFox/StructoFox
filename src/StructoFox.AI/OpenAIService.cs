namespace StructoFox.AI;

/// <summary>OpenAI ChatGPT via the standard /v1/chat/completions endpoint.</summary>
public sealed class OpenAIService : OpenAICompatibleService
{
    public override string ProviderName => "OpenAI ChatGPT";

    public static readonly string[] DefaultModels =
    [
        "gpt-4o",
        "gpt-4o-mini",
        "gpt-4-turbo",
        "gpt-3.5-turbo",
    ];

    public OpenAIService(string apiKey)
        : base("https://api.openai.com/v1", apiKey)
    {
        CurrentModel = DefaultModels[0];
    }
}
