namespace StructoFox.AI;

/// <summary>Whether a provider authenticates with an API key (cloud) or points at a local endpoint URL.</summary>
public enum AiProviderKind { Cloud, Local }

/// <summary>A selectable provider: its id/display name, kind, and how to build the service from a config.</summary>
public sealed record AiProviderInfo(string Id, string Display, AiProviderKind Kind, Func<AiConfig, ICloudAIService> Create);

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
        // ── Local (endpoint URL) ── (for native Ollama use "Ollama (OpenAI API)" above, endpoint /v1)
        Local("LM Studio",      (b, k) => new LmStudioService(b, k)),
        Local("llama.cpp",      (b, k) => new LlamaCppService(b, k)),
        Local("Jan",            (b, k) => new JanService(b, k)),
        Local("GPT4All",        (b, k) => new GPT4AllService(b, k)),
        Local("KoboldCpp",      (b, k) => new KoboldCppService(b, k)),
        Local("Llamafile",      (b, k) => new LlamafileService(b, k)),
        Local("LocalAI",        (b, k) => new LocalAIService(b, k)),
        Local("TabbyAPI",       (b, k) => new TabbyAPIService(b, k)),
        Local("text-gen-webui", (b, k) => new TextGenWebUIService(b, k)),
        Local("vLLM",           (b, k) => new VllmService(b, k)),
    ];

    /// <summary>Builds the service for the configured provider (falls back to the first if the id is unknown).</summary>
    public static ICloudAIService Create(AiConfig cfg) =>
        (All.FirstOrDefault(p => p.Id == cfg.Provider) ?? All[0]).Create(cfg);

    static AiProviderInfo Cloud(string id, Func<string, ICloudAIService> ctor) =>
        new(id, id, AiProviderKind.Cloud, c => Apply(ctor(c.ApiKey), c));

    static AiProviderInfo Local(string id, Func<string, string, ICloudAIService> ctor) =>
        new(id, id, AiProviderKind.Local, c => Apply(ctor(c.BaseUrl, c.ApiKey), c));

    static ICloudAIService Apply(ICloudAIService s, AiConfig c)
    {
        if (!string.IsNullOrWhiteSpace(c.Model)) s.CurrentModel = c.Model;
        if (c.MaxTokens > 0) s.MaxTokens = c.MaxTokens;
        return s;
    }
}
