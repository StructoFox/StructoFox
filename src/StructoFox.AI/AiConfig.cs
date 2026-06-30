using System.IO;
using System.Text.Json;

namespace StructoFox.AI;

/// <summary>Which AI provider/model to use, and the credentials/endpoint for it. Persisted as JSON by the
/// plugin (the host decides where). For a cloud provider set <see cref="ApiKey"/>; for a local one set
/// <see cref="BaseUrl"/>.</summary>
public sealed class AiConfig
{
    /// <summary>Provider id, matching an <see cref="AiProviderInfo.Id"/> in <see cref="AiProviders.All"/>.</summary>
    public string Provider  { get; set; } = "Anthropic";
    public string Model     { get; set; } = "";   // empty = the provider's default model
    public string ApiKey    { get; set; } = "";   // cloud providers
    public string BaseUrl   { get; set; } = "";   // local providers (e.g. http://localhost:1234/v1)
    public int    MaxTokens { get; set; }          // 0 = provider default

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AiConfig Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<AiConfig>(File.ReadAllText(path)) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));
    }
}
