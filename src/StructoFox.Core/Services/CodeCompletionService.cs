using StructoFox.Core.Models;

namespace StructoFox.Core;

/// <summary>What a completion refers to — drives its list glyph and ranking.</summary>
public enum CompletionKind { Variable, Object, Function, Method, Field, Class, Struct, Interface, Enum, EnumValue, Namespace }

/// <summary>One autocomplete suggestion. <see cref="Insert"/> is the bare identifier written into the text
/// (the UI adds "()" for a <see cref="CompletionKind.Method"/>/<see cref="CompletionKind.Function"/>);
/// <see cref="Label"/> is what the dropdown shows (methods include their parameter list);
/// <see cref="Detail"/> is a short right-hand hint (return/field type, or the object's class).</summary>
public sealed class Completion
{
    public required string         Label  { get; init; }
    public required string         Insert { get; init; }
    public string                  Detail { get; init; } = "";
    public required CompletionKind Kind   { get; init; }
}

/// <summary>
/// AI-free code completion for the flowchart node editor. Suggests the project's own libraries (classes, structs,
/// interfaces, enums, objects, namespaces, functions — methods with parameters) plus the variables the user wrote
/// earlier in the same flow, all rendered in the project's chosen language syntax. UI-free and deterministic.
/// </summary>
public static class CodeCompletionService
{
    const int MaxResults = 40;
    static readonly IReadOnlyDictionary<string, string> EmptyLocals = new Dictionary<string, string>();

    /// <summary>Ranked suggestions for the identifier being typed at the caret. <paramref name="locals"/> maps
    /// in-scope variable names (from earlier nodes) to an inferred type (empty when unknown, used for member
    /// access); <paramref name="textBeforeCaret"/> is the node text up to the caret.</summary>
    public static IReadOnlyList<Completion> Suggest(
        string projFolder, ExportLanguage lang, IReadOnlyDictionary<string, string> locals, string textBeforeCaret)
    {
        locals ??= EmptyLocals;
        var (receiver, prefix) = ParseContext(textBeforeCaret ?? "");
        var all = LoadEntities(projFolder);
        var pool = new List<Completion>();

        if (receiver is not null)
        {
            // Member access `receiver.` — resolve to a type and offer its members / enum values / namespace types.
            // A local variable with an inferred type resolves through that type, else the receiver name itself.
            var type = locals.TryGetValue(receiver, out var lt) && lt.Length > 0
                ? all.FirstOrDefault(e => IsType(e) && string.Equals(e.Name, lt, StringComparison.OrdinalIgnoreCase))
                : ResolveType(all, receiver);
            if (type is { EntityType: CodeEntityType.Enum })
                foreach (var v in type.EnumValues)
                    pool.Add(new Completion { Label = v, Insert = v, Kind = CompletionKind.EnumValue });
            else if (type is not null)
            {
                foreach (var m in type.Methods)
                    pool.Add(MethodCompletion(lang, m));
                foreach (var f in type.Fields)
                    pool.Add(new Completion { Label = f.Name, Insert = f.Name, Detail = f.DataType, Kind = CompletionKind.Field });
            }
            else
            {
                // A namespace qualifier `Ns.` → the types declared in it.
                var ns = all.FirstOrDefault(e => e.EntityType == CodeEntityType.Namespace
                    && string.Equals(e.Name, receiver, StringComparison.OrdinalIgnoreCase));
                if (ns is not null)
                    foreach (var e in all.Where(e => IsType(e) && (e.Namespace == ns.Id || string.Equals(e.Namespace, ns.Name, StringComparison.OrdinalIgnoreCase))))
                        pool.Add(TypeCompletion(e));
            }
        }
        else if (PrecededByNew(textBeforeCaret ?? "", prefix))
        {
            // After `new` only an instantiable type makes sense → offer classes/structs (constructors).
            foreach (var e in all.Where(e => e.EntityType is CodeEntityType.Class or CodeEntityType.Struct))
                pool.Add(TypeCompletion(e));
        }
        else
        {
            // Top level: locals, objects, functions, types, namespaces.
            foreach (var v in locals.Keys)
                pool.Add(new Completion { Label = v, Insert = v, Kind = CompletionKind.Variable });
            foreach (var o in all.Where(e => e.EntityType == CodeEntityType.Object))
                pool.Add(new Completion { Label = o.Name, Insert = o.Name, Kind = CompletionKind.Object,
                    Detail = ClassName(all, o.InstanceOfId) });
            foreach (var f in all.Where(e => e.EntityType == CodeEntityType.Function))
                pool.Add(FunctionCompletion(lang, f));
            foreach (var e in all.Where(IsType))
                pool.Add(TypeCompletion(e));
            foreach (var n in all.Where(e => e.EntityType == CodeEntityType.Namespace))
                pool.Add(new Completion { Label = n.Name, Insert = n.Name, Kind = CompletionKind.Namespace });
        }

        return Rank(pool, prefix);
    }

    // ── Ranking / filtering ──────────────────────────────────────────────────
    static IReadOnlyList<Completion> Rank(List<Completion> pool, string prefix)
    {
        IEnumerable<Completion> filtered = pool;
        if (prefix.Length > 0)
            filtered = pool.Where(c => c.Insert.Contains(prefix, StringComparison.OrdinalIgnoreCase));

        return filtered
            .Select(c => (c, score: Score(c, prefix)))
            .OrderBy(t => t.score)
            .ThenBy(t => t.c.Label, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.c)
            .Take(MaxResults)
            .ToList();
    }

    // Lower = earlier. Prefix matches beat substring matches; within a tie, prefer nearer-scope kinds.
    static int Score(Completion c, string prefix)
    {
        int match = prefix.Length == 0 || c.Insert.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? 0 : 100;
        int kind = c.Kind switch
        {
            CompletionKind.Variable or CompletionKind.EnumValue => 0,
            CompletionKind.Object or CompletionKind.Field       => 1,
            CompletionKind.Function or CompletionKind.Method     => 2,
            CompletionKind.Class or CompletionKind.Struct or CompletionKind.Interface or CompletionKind.Enum => 3,
            _                                                    => 4,
        };
        return match + kind;
    }

    // ── Completion builders ────────────────────────────────────────────────
    static Completion MethodCompletion(ExportLanguage lang, CodeMethod m) => new()
    {
        Label  = $"{m.Name}({FormatParams(lang, m.Parameters)})",
        Insert = m.Name,
        Detail = m.ReturnType,
        Kind   = CompletionKind.Method,
    };

    static Completion FunctionCompletion(ExportLanguage lang, CodeEntity f)
    {
        var ins = f.Ports.Where(p => p.Direction == PortDirection.Input).ToList();
        var ps  = string.Join(", ", ins.Select(p => FormatParam(lang, p.Name, p.DataType)));
        var ret = f.Ports.FirstOrDefault(p => p.Direction == PortDirection.Output)?.DataType ?? "";
        return new Completion { Label = $"{f.Name}({ps})", Insert = f.Name, Detail = ret, Kind = CompletionKind.Function };
    }

    static Completion TypeCompletion(CodeEntity e) => new()
    {
        Label  = e.Name,
        Insert = e.Name,
        Kind   = e.EntityType switch
        {
            CodeEntityType.Struct    => CompletionKind.Struct,
            CodeEntityType.Interface => CompletionKind.Interface,
            CodeEntityType.Enum      => CompletionKind.Enum,
            _                        => CompletionKind.Class,
        },
    };

    // ── Parameter rendering (display only — per-language surface syntax) ──────
    static string FormatParams(ExportLanguage lang, List<CodeParam> ps) =>
        string.Join(", ", ps.Select(p => FormatParam(lang, p.Name, p.DataType)));

    static string FormatParam(ExportLanguage lang, string name, string type) => lang switch
    {
        ExportLanguage.JavaScript                                             => name,
        ExportLanguage.Go                                                     => $"{name} {type}",
        ExportLanguage.Php                                                    => $"{type} ${name}",
        ExportLanguage.TypeScript or ExportLanguage.Python or ExportLanguage.Rust
            or ExportLanguage.Kotlin or ExportLanguage.Swift                  => $"{name}: {type}",
        _                                                                     => $"{type} {name}",   // C-family
    };

    // ── Entity loading / resolution ──────────────────────────────────────────
    static readonly string[] EntityTypes =
        { "Class", "Struct", "Interface", "Enum", "Function", "Object", "Namespace" };

    static List<CodeEntity> LoadEntities(string projFolder)
    {
        var all = new List<CodeEntity>();
        foreach (var t in EntityTypes)
            all.AddRange(CodeEntityService.LoadAll(projFolder, t));
        return all;
    }

    static bool IsType(CodeEntity e) =>
        e.EntityType is CodeEntityType.Class or CodeEntityType.Struct or CodeEntityType.Interface or CodeEntityType.Enum;

    // Resolves a receiver name to a type entity: an Object → its class; or a directly named type. Null otherwise.
    static CodeEntity? ResolveType(List<CodeEntity> all, string receiver)
    {
        var obj = all.FirstOrDefault(e => e.EntityType == CodeEntityType.Object
            && string.Equals(e.Name, receiver, StringComparison.OrdinalIgnoreCase));
        if (obj is not null)
            return all.FirstOrDefault(e => e.Id == obj.InstanceOfId && IsType(e));
        return all.FirstOrDefault(e => IsType(e) && string.Equals(e.Name, receiver, StringComparison.OrdinalIgnoreCase));
    }

    static string ClassName(List<CodeEntity> all, string classId) =>
        all.FirstOrDefault(e => e.Id == classId)?.Name ?? "";

    // ── Caret-context parsing ────────────────────────────────────────────────
    // Splits the text at the caret into (receiver, prefix): the trailing identifier being typed = prefix; if it is
    // preceded by `<ident>.`, that identifier = receiver (member access), else receiver is null (top level).
    static (string? receiver, string prefix) ParseContext(string text)
    {
        int end = text.Length;
        int start = end;
        while (start > 0 && IsIdent(text[start - 1])) start--;
        var prefix = text[start..end];

        if (start > 0 && text[start - 1] == '.')
        {
            int rEnd = start - 1, rStart = rEnd;
            while (rStart > 0 && IsIdent(text[rStart - 1])) rStart--;
            var receiver = text[rStart..rEnd];
            if (receiver.Length > 0) return (receiver, prefix);
        }
        return (null, prefix);
    }

    static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';

    // True when the identifier being typed directly follows the `new` keyword (`… = new Te|`), so only
    // instantiable types should be suggested.
    static bool PrecededByNew(string text, string prefix)
    {
        if (text.Length < prefix.Length) return false;
        var head = text[..(text.Length - prefix.Length)].TrimEnd();
        return head.EndsWith("new", StringComparison.Ordinal) && (head.Length == 3 || !IsIdent(head[^4]));
    }
}
