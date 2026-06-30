using System.IO;
using System.Text.Json;

namespace StructoFox.AI;

/// <summary>
/// One configured AI model — the StructoFox equivalent of a ClaudetRelay "participant", simplified for code
/// generation. A card is a provider + model, plus (for local providers) the server URL on the SAME card.
/// The API key is NOT stored here — cloud providers read it from <see cref="KeyStore"/> by provider id.
/// The self-description is code-focused: instead of "what do you like / dislike doing" we ask the model which
/// programming languages it most and least enjoys, shown as <see cref="Strengths"/> / <see cref="Weaknesses"/>.
/// </summary>
public sealed class AiModelCard
{
    /// <summary>Friendly name shown on the card (blank → model name).</summary>
    public string Name      { get; set; } = "";

    /// <summary>Provider id, matching an <see cref="AiProviderInfo.Id"/>.</summary>
    public string Provider  { get; set; } = "Anthropic";

    public string Model     { get; set; } = "";

    /// <summary>Local providers only: the inference server base URL (e.g. http://localhost:11434/v1).</summary>
    public string ServerUrl { get; set; } = "";

    public int    MaxTokens { get; set; }          // 0 = provider default
    public bool   Enabled   { get; set; } = true;

    // ── Self-description (code flavour) ──
    public string Role            { get; set; } = "";   // short title the model gives itself
    public string SelfDescription { get; set; } = "";   // one sentence
    public string Strengths       { get; set; } = "";   // languages it likes most
    public string Weaknesses      { get; set; } = "";   // languages it likes least
    public string LastApiError    { get; set; } = "";   // last error, so we don't hammer a misconfigured key
}

/// <summary>The persisted set of model cards (JSON in the user's app-data; keys live in <see cref="KeyStore"/>).</summary>
public sealed class AiSettings
{
    public List<AiModelCard> Cards { get; set; } = new();

    /// <summary>Default on-disk location: %AppData%/StructoFox/ai-settings.json (cross-platform app-data root).</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StructoFox", "ai-settings.json");

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AiSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try { return File.Exists(path) ? JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(path)) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));
    }
}
