namespace StructoFox.AI;

public sealed class MistralService : OpenAICompatibleService
{
    public override string ProviderName => "Mistral";

    public static readonly string[] DefaultModels =
    [
        "mistral-small-latest",
        "mistral-medium-latest",
        "mistral-large-latest",
    ];

    public MistralService(string apiKey)
        : base("https://api.mistral.ai/v1", apiKey)
    {
        CurrentModel = DefaultModels[0];
    }
}
