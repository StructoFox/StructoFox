namespace StructoFox.AI;

/// <summary>
/// Asks a model to describe itself for code generation and fills the result into an <see cref="AiModelCard"/>.
/// Mirrors ClaudetRelay's SelfDescriptionService, but the second question is code-specific: instead of
/// "what do you like / dislike doing" we ask for the programming languages the model most and least enjoys —
/// surfaced on the card as Strengths / Weaknesses. Fire-and-forget; never throws.
/// </summary>
public static class CodeSelfDescription
{
    const string DescriptionPrompt =
        "IMPORTANT: Reply with EXACTLY these two lines. No greeting, no explanation — just these two lines.\n" +
        "TITLE: [your coding role in 1-3 words]\n" +
        "DESC: [one sentence on what you do best when writing code]\n\n" +
        "Example:\n" +
        "TITLE: Systems Programmer\n" +
        "DESC: I excel at writing efficient, well-structured code and clean APIs.\n\n" +
        "Now write your two lines:";

    // Completion-style (not instruction-style): base/coder models continue this line naturally.
    const string StrengthsCompletionPrompt =
        "Continue the following line with a comma-separated list of programming languages and nothing else.\n" +
        "Programming languages I am strongest at writing:";

    const string LanguagesPrompt =
        "IMPORTANT: Reply with EXACTLY these two lines. No greeting, no explanation — just these two lines.\n" +
        "STRONG: [comma-separated list of the programming languages you most enjoy writing]\n" +
        "WEAK: [comma-separated list of the programming languages you least enjoy writing]\n\n" +
        "Example:\n" +
        "STRONG: Python, C#, TypeScript\n" +
        "WEAK: COBOL, assembly, Perl\n\n" +
        "Now write your two lines:";

    /// <summary>Queries the model and writes Role/SelfDescription/Strengths/Weaknesses (or LastApiError) into
    /// <paramref name="card"/>. Returns true if anything was updated. Caller persists the card.</summary>
    public static async Task<bool> FetchAsync(AiModelCard card, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(card.Model)) return false;

        var info = AiProviders.Find(card.Provider);
        if (info is { Kind: AiProviderKind.Cloud } && !KeyStore.Has(info.Id)) return false;

        try
        {
            using var svc = AiProviders.Create(card);
            svc.MaxTokens = card.MaxTokens > 0 ? card.MaxTokens : 200;

            var desc = await Ask(svc, DescriptionPrompt, ct);
            if (desc is not null)
            {
                var (title, sentence) = ParsePair(desc, "TITLE:", "DESC:");
                if (!string.IsNullOrEmpty(title))    card.Role            = title;
                if (!string.IsNullOrEmpty(sentence)) card.SelfDescription = sentence;
            }

            var langs = await Ask(svc, LanguagesPrompt, ct);
            if (langs is not null)
            {
                var (strong, weak) = ParsePair(langs, "STRONG:", "WEAK:");
                if (!string.IsNullOrEmpty(strong)) card.Strengths  = strong;
                if (!string.IsNullOrEmpty(weak))   card.Weaknesses = weak;
            }

            // Fallback for coder / base models that ignore the structured format (yet are exactly the ones we
            // most want for generation): prime a sentence completion and harvest known languages from the text.
            if (string.IsNullOrWhiteSpace(card.Strengths))
            {
                var free = await Ask(svc, StrengthsCompletionPrompt, ct);
                var found = free is null ? new() : ExtractLanguages(free);
                if (found.Count > 0) card.Strengths = string.Join(", ", found);
            }

            card.LastApiError = "";
            return true;
        }
        catch (Exception ex)
        {
            card.LastApiError = ex.Message;
            return true;   // updated the error so the UI can show it / stop retrying
        }
    }

    // Common programming languages, scanned for in free-text replies from models that don't follow the format.
    static readonly string[] KnownLanguages =
    [
        "C++", "C#", "Objective-C", "F#", "Visual Basic", "JavaScript", "TypeScript", "Python", "Java",
        "Kotlin", "Swift", "Rust", "Golang", "Go", "Ruby", "PHP", "Scala", "Haskell", "Perl", "Julia", "Lua",
        "Dart", "Elixir", "Erlang", "Clojure", "Assembly", "COBOL", "Fortran", "SQL", "PowerShell", "Bash",
        "Shell", "MATLAB", "Pascal", "Zig", "Nim", "Crystal", "OCaml", "Groovy", "Solidity", "Verse", "R", "C",
    ];

    /// <summary>Harvests known programming-language names from free text, in order of first appearance, deduped.
    /// Word boundaries treat <c>+</c> and <c>#</c> as part of the token so "C" doesn't match inside "C++"/"C#".</summary>
    static List<string> ExtractLanguages(string text)
    {
        var hits = new List<(int pos, string lang)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in KnownLanguages)
        {
            var pattern = $@"(?<![A-Za-z0-9+#]){System.Text.RegularExpressions.Regex.Escape(lang)}(?![A-Za-z0-9+#])";
            var m = System.Text.RegularExpressions.Regex.Match(text, pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success && seen.Add(lang == "Golang" ? "Go" : lang))
                hits.Add((m.Index, lang == "Golang" ? "Go" : lang));
        }
        return hits.OrderBy(h => h.pos).Select(h => h.lang).Take(8).ToList();
    }

    static async Task<string?> Ask(ICloudAIService svc, string prompt, CancellationToken ct)
    {
        try
        {
            var reply = await svc.SendAsync(new[] { new CloudAIMessage("user", prompt) }, system: null, ct);
            return string.IsNullOrWhiteSpace(reply) ? null : reply;
        }
        catch { return null; }
    }

    static (string a, string b) ParsePair(string raw, string keyA, string keyB)
    {
        string a = "", b = "";
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (t.StartsWith(keyA, StringComparison.OrdinalIgnoreCase))
                a = Clean(t[keyA.Length..]);
            else if (t.StartsWith(keyB, StringComparison.OrdinalIgnoreCase))
                b = Clean(t[keyB.Length..]);
        }
        return (a, b);
    }

    // Trims brackets/quotes and treats a non-answer (dashes, "n/a", "none", empty) as empty — base models
    // that don't follow the format often emit a bare "-" or placeholder rather than a real list.
    static string Clean(string s)
    {
        s = s.Trim().Trim('[', ']', '"', '\'', ' ');
        var bare = s.Replace("-", "").Replace("–", "").Replace("—", "").Trim();
        if (bare.Length == 0) return "";
        if (bare.Equals("n/a", StringComparison.OrdinalIgnoreCase) ||
            bare.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            bare.Equals("keine", StringComparison.OrdinalIgnoreCase)) return "";
        return s;
    }
}
