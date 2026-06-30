namespace StructoFox.AI;

public sealed class GroqService : OpenAICompatibleService
{
    public override string ProviderName => "Groq";

    public static readonly string[] DefaultModels =
    [
        "llama-3.3-70b-versatile",
        "mixtral-8x7b-32768",
        "gemma2-9b-it",
    ];

    public GroqService(string apiKey)
        : base("https://api.groq.com/openai/v1", apiKey)
    {
        CurrentModel = DefaultModels[0];
    }
}
