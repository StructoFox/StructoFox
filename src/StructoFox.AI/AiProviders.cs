namespace StructoFox.AI;

/// <summary>Whether a provider authenticates with an API key (cloud) or points at a local endpoint URL.</summary>
public enum AiProviderKind { Cloud, Local }

/// <summary>A selectable provider: its id/display name, kind, how to build the service, and (for local
/// providers) the default server URL to pre-fill into the config UI.</summary>
public sealed record AiProviderInfo(string Id, string Display, AiProviderKind Kind, Func<AiConfig, ICloudAIService> Create)
{
    /// <summary>Default server URL for local providers (empty for cloud).</summary>
    public string DefaultUrl { get; init; } = "";
}

/// <summary>The catalogue of AI providers (the full set ported from ClaudetRelay) and a factory that builds the
/// configured one. Cloud providers take an API key; local ones take a base URL.</summary>
public static class AiProviders
{
    public static IReadOnlyList<AiProviderInfo> All { get; } =
    [
        // ── Cloud (API key) ──
        Cloud("Anthropic",           k => new AnthropicService(k)),
        Cloud("OpenAI",              k => new OpenAIService(k)),
        Cloud("Google",              k => new GoogleAIService(k)),
        Cloud("Groq",                k => new GroqService(k)),
        Cloud("Mistral",             k => new MistralService(k)),
        Cloud("OpenRouter",          k => new OpenRouterService(k)),
        Cloud("xAI (Grok)",          k => new XAIGrokService(k)),
        Cloud("Cerebras",            k => new CerebrasService(k)),
        Cloud("DeepInfra",           k => new DeepInfraService(k)),
        Cloud("DeepSeek",            k => new DeepSeekService(k)),
        Cloud("Fireworks",           k => new FireworksAIService(k)),
        Cloud("Nvidia NIM",          k => new NvidiaNIMService(k)),
        Cloud("Perplexity",          k => new PerplexityAIService(k)),
        Cloud("Together",            k => new TogetherAIService(k)),
        Cloud("Ollama (OpenAI API)", k => new OllamaOpenAIService(k)),
        // ── Local (endpoint URL) ──
        Local("Ollama",         OllamaLocalService.DefaultUrl,    (b, k) => new OllamaLocalService(b, k)),
        Local("LM Studio",      LmStudioService.DefaultLocalUrl,  (b, k) => new LmStudioService(b, k)),
        Local("llama.cpp",      LlamaCppService.DefaultUrl,       (b, k) => new LlamaCppService(b, k)),
        Local("Jan",            JanService.DefaultUrl,            (b, k) => new JanService(b, k)),
        Local("GPT4All",        GPT4AllService.DefaultUrl,        (b, k) => new GPT4AllService(b, k)),
        Local("KoboldCpp",      KoboldCppService.DefaultUrl,      (b, k) => new KoboldCppService(b, k)),
        Local("Llamafile",      LlamafileService.DefaultUrl,      (b, k) => new LlamafileService(b, k)),
        Local("LocalAI",        LocalAIService.DefaultUrl,        (b, k) => new LocalAIService(b, k)),
        Local("TabbyAPI",       TabbyAPIService.DefaultUrl,       (b, k) => new TabbyAPIService(b, k)),
        Local("text-gen-webui", TextGenWebUIService.DefaultUrl,   (b, k) => new TextGenWebUIService(b, k)),
        Local("vLLM",           VllmService.DefaultUrl,           (b, k) => new VllmService(b, k)),
    ];

    /// <summary>Builds the service for the configured provider (falls back to the first if the id is unknown).</summary>
    public static ICloudAIService Create(AiConfig cfg) =>
        (All.FirstOrDefault(p => p.Id == cfg.Provider) ?? All[0]).Create(cfg);

    /// <summary>Looks up a provider by id (null if unknown).</summary>
    public static AiProviderInfo? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>Builds the service for a model card: cloud providers take their key from <see cref="KeyStore"/>,
    /// local providers take the card's server URL.</summary>
    public static ICloudAIService Create(AiModelCard card)
    {
        var info = Find(card.Provider) ?? All[0];
        var cfg  = new AiConfig
        {
            Provider  = info.Id,
            Model     = card.Model,
            MaxTokens = card.MaxTokens,
            ApiKey    = info.Kind == AiProviderKind.Cloud ? (KeyStore.Load(info.Id) ?? "") : "",
            BaseUrl   = card.ServerUrl
        };
        return info.Create(cfg);
    }

    /// <summary>A provider is selectable for a new card when it's local (URL-based) OR has an API key stored.
    /// (Mirrors the requirement: only providers with a configured key appear in the provider dropdown.)</summary>
    public static bool IsSelectable(AiProviderInfo p) =>
        p.Kind == AiProviderKind.Local || KeyStore.Has(p.Id);

    /// <summary>The providers a user may pick for a model card right now (local + key-configured cloud).</summary>
    public static IReadOnlyList<AiProviderInfo> Selectable() => All.Where(IsSelectable).ToList();

    static AiProviderInfo Cloud(string id, Func<string, ICloudAIService> ctor) =>
        new(id, id, AiProviderKind.Cloud, c => Apply(ctor(c.ApiKey), c));

    static AiProviderInfo Local(string id, string defaultUrl, Func<string, string, ICloudAIService> ctor) =>
        new(id, id, AiProviderKind.Local, c => Apply(ctor(c.BaseUrl, c.ApiKey), c)) { DefaultUrl = defaultUrl };

    static ICloudAIService Apply(ICloudAIService s, AiConfig c)
    {
        if (!string.IsNullOrWhiteSpace(c.Model)) s.CurrentModel = c.Model;
        if (c.MaxTokens > 0) s.MaxTokens = c.MaxTokens;
        return s;
    }
}
