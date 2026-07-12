using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace StructoFox.Core;

/// <summary>
/// Turns a document's display name into a human-readable, filename- and key-safe unique key. This replaces the old
/// random 8-hex ids so files on disk read like the documents they hold and can be copied between machines. Keys
/// double as cross-document references, so they must avoid the method separator '#', ':' and path characters.
/// </summary>
public static class NameKeys
{
    /// <summary>Joins an entity key and a method key into a diagram key ("Entity#method").</summary>
    public const char MethodSep = '#';

    /// <summary>Filename- and key-safe form of a name: letters/digits/space/-/_ kept, everything else → '_', runs
    /// collapsed and trimmed. Never empty (falls back to "Unnamed"). '#' and ':' are dropped so the key can't clash
    /// with the method-key separator or a drive prefix.</summary>
    public static string Sanitize(string? name)
    {
        var clean = new string((name ?? "")
            .Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '_').ToArray());
        while (clean.Contains("  ")) clean = clean.Replace("  ", " ");
        clean = clean.Trim().Trim('_').Trim();
        return clean.Length == 0 ? "Unnamed" : clean;
    }

    /// <summary>Makes an already-sanitized <paramref name="candidate"/> unique against <paramref name="taken"/>
    /// (case-insensitive) by appending _2, _3, … Nothing is changed when it's already free.</summary>
    public static string Unique(string candidate, IEnumerable<string> taken)
    {
        var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!set.Contains(candidate)) return candidate;
        for (int i = 2; ; i++)
        {
            var c = $"{candidate}_{i}";
            if (!set.Contains(c)) return c;
        }
    }

    /// <summary>A unique, sanitized key derived from a display name.</summary>
    public static string From(string? name, IEnumerable<string> taken) => Unique(Sanitize(name), taken);

    public static string JoinMethodKey(string entityKey, string methodKey) => $"{entityKey}{MethodSep}{methodKey}";

    /// <summary>Splits "entity#method" → (entity, method); method is null when there is no separator.</summary>
    public static (string entity, string? method) SplitMethodKey(string key)
    {
        int i = key.IndexOf(MethodSep);
        return i < 0 ? (key, null) : (key[..i], key[(i + 1)..]);
    }

    // ── Type-name references (a field/param/return/port DataType is free text that may NAME a type entity) ──

    /// <summary>True if the type text names <paramref name="key"/> as a whole token — matches "testEnum",
    /// "List&lt;testEnum&gt;", "testEnum[]", but not "testEnumFoo".</summary>
    public static bool TypeMentions(string? typeText, string key) =>
        !string.IsNullOrEmpty(typeText) && key.Length > 0 &&
        Regex.IsMatch(typeText, $@"(?<!\w){Regex.Escape(key)}(?!\w)");

    /// <summary>Replaces every whole-token occurrence of <paramref name="oldKey"/> in a type text with
    /// <paramref name="newKey"/> (keeps generic/array wrappers intact).</summary>
    public static string RemapType(string? typeText, string oldKey, string newKey)
    {
        if (string.IsNullOrEmpty(typeText) || oldKey.Length == 0) return typeText ?? "";
        return Regex.Replace(typeText, $@"(?<!\w){Regex.Escape(oldKey)}(?!\w)", newKey.Replace("$", "$$"));
    }
}
