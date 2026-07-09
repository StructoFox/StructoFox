using System.Text.RegularExpressions;

namespace StructoFox.Core;

/// <summary>
/// Extracts local variable names from flowchart node texts, heuristically and language-agnostically, so the
/// node-editor autocomplete can suggest variables the user already introduced earlier in the flow. It only reads
/// NAMES (no type inference) — a best-effort scan, not a parser.
/// </summary>
public static class LocalVariableScanner
{
    // LHS of an assignment: `name = …` (but not ==, <=, >=, !=). Captures a bare identifier at a line/segment start.
    static readonly Regex Assignment = new(
        @"(?<![\w.])([A-Za-z_][A-Za-z0-9_]*)\s*=(?![=])",
        RegexOptions.Compiled);

    // A simple declaration: an optional keyword or type, then the declared name — `int x`, `var x`, `let x`,
    // `string name`, `List<int> xs`. The LAST identifier before `=`/`;`/end is the variable.
    static readonly Regex Declaration = new(
        @"\b(?:var|let|const|val|dim|int|long|short|byte|float|double|bool|boolean|char|string|str|auto)\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A construction `[Type] name = new Type(...)` — the strong, reliable signal for a variable's TYPE. Captures
    // the variable name (1) and the constructed type (2, possibly namespace-qualified).
    static readonly Regex NewAssignment = new(
        @"(?<![\w.])([A-Za-z_][A-Za-z0-9_]*)\s*=\s*new\s+([A-Za-z_][A-Za-z0-9_.]*)",
        RegexOptions.Compiled);

    /// <summary>Returns the distinct variable names found across all the given node texts (order-stable).</summary>
    public static IReadOnlyList<string> FromNodeTexts(IEnumerable<string> texts) =>
        TypedFromNodeTexts(texts).Keys.ToList();

    /// <summary>Returns each variable found → its inferred type name (empty when unknown), order-stable. Type is
    /// inferred only from a reliable `name = new Type()` construction (best-effort); primitives/plain assignments
    /// yield an empty type. The last known type for a name wins.</summary>
    public static IReadOnlyDictionary<string, string> TypedFromNodeTexts(IEnumerable<string> texts)
    {
        var order = new List<string>();
        var type  = new Dictionary<string, string>(StringComparer.Ordinal);   // name → type ("" if unknown)
        void Note(string name, string t)
        {
            if (name.Length == 0 || IsKeyword(name)) return;
            if (!type.ContainsKey(name)) { order.Add(name); type[name] = ""; }
            if (t.Length > 0) type[name] = LastSegment(t);   // a known type upgrades / overrides
        }

        foreach (var raw in texts)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            foreach (Match m in NewAssignment.Matches(raw)) Note(m.Groups[1].Value, m.Groups[2].Value);
            foreach (Match m in Declaration.Matches(raw))   Note(m.Groups[1].Value, "");
            foreach (Match m in Assignment.Matches(raw))    Note(m.Groups[1].Value, "");
        }
        return order.ToDictionary(n => n, n => type[n]);
    }

    // The trailing segment of a possibly-qualified name (`TestSpace.TestKlasse` → `TestKlasse`).
    static string LastSegment(string name)
    {
        var i = name.LastIndexOf('.');
        return i < 0 ? name : name[(i + 1)..];
    }

    // Words that look like an assignment target but are language keywords, not variables.
    static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    { "if", "else", "elif", "while", "for", "foreach", "do", "switch", "case", "return", "new", "true", "false", "null", "none", "and", "or", "not" };

    static bool IsKeyword(string w) => Keywords.Contains(w);
}
