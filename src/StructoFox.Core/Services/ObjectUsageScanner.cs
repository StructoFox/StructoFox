using System.Text.RegularExpressions;
using StructoFox.Core.Models;

namespace StructoFox.Core;

/// <summary>
/// Finds where an Object (instance) is created, destroyed and used across all function/method diagrams.
/// Instances are namespace-neutral, so we scan every owner's statement text and classify each mention of
/// the object's name heuristically: a <c>new</c> (or a typed declaration) counts as created, a
/// <c>Dispose()</c> / <c>= null</c> / <c>delete</c> as destroyed, and any other member access as used.
/// Best-effort on free-text nodes — it reads intent, it doesn't parse a language.
/// </summary>
public static class ObjectUsageScanner
{
    public enum UseKind { Create, Destroy, Use }

    /// <summary>One reference to the object: which diagram owner, what kind, and the line that triggered it.</summary>
    public readonly record struct ObjectUse(string OwnerKey, string OwnerLabel, UseKind Kind, string Snippet);

    // "x = new Class(...)" and the target-typed "x = new(Class(...))".
    static readonly System.Text.RegularExpressions.Regex NewStmt = new(@"\b([A-Za-z_]\w*)\s*=\s*new\s+([\w.]+)");
    static readonly System.Text.RegularExpressions.Regex NewStmtTargetTyped = new(@"\b([A-Za-z_]\w*)\s*=\s*new\s*\(\s*([\w.]+)");

    /// <summary>Scans free-text statements for object instantiations (<c>x = new SomeClass(...)</c>) and creates
    /// a matching Object entity for each variable that isn't one yet — but only when the class name resolves to
    /// an existing Class/Struct (so typos don't invent entities). Returns how many objects were created.</summary>
    public static int RecognizeInstantiations(string projFolder, IEnumerable<string> texts)
    {
        var classes = CodeEntityService.LoadAll(projFolder, "Class")
            .Concat(CodeEntityService.LoadAll(projFolder, "Struct")).ToList();
        if (classes.Count == 0) return 0;

        var names = new HashSet<string>(CodeEntityService.LoadAll(projFolder, "Object").Select(o => o.Name), StringComparer.Ordinal);
        int created = 0;
        foreach (var text in texts)
            foreach (var line in (text ?? "").Split('\n'))
            {
                var m = NewStmt.Match(line);
                if (!m.Success) m = NewStmtTargetTyped.Match(line);
                if (!m.Success) continue;

                var varName = m.Groups[1].Value;
                if (names.Contains(varName)) continue;
                var raw = m.Groups[2].Value;
                var className = raw.Contains('.') ? raw[(raw.LastIndexOf('.') + 1)..] : raw;
                var cls = classes.FirstOrDefault(c => c.Name == className);
                if (cls is null) continue;

                CodeEntityService.Save(projFolder, "Object", new CodeEntity
                { Name = varName, EntityType = CodeEntityType.Object, InstanceOfId = cls.Id, Namespace = "" });
                names.Add(varName);
                created++;
            }
        return created;
    }

    public static List<ObjectUse> Scan(string projFolder, CodeEntity obj)
    {
        var hits = new List<ObjectUse>();
        var name = obj.Name?.Trim() ?? "";
        if (name.Length == 0) return hits;

        var classes   = CodeEntityService.LoadAll(projFolder, "Class");
        var className = classes.FirstOrDefault(c => c.Id == obj.InstanceOfId)?.Name ?? "";
        var word      = new Regex($@"(?<![A-Za-z0-9_]){Regex.Escape(name)}(?![A-Za-z0-9_])");

        void ScanOwner(string key, string label)
        {
            foreach (var line in OwnerLines(projFolder, key))
            {
                if (!word.IsMatch(line)) continue;
                if (Classify(line, name, className) is { } kind)
                    hits.Add(new ObjectUse(key, label, kind, line.Trim()));
            }
        }

        // Functions (key = id) and every class method (key = classId#methodId).
        foreach (var f in CodeEntityService.LoadAll(projFolder, "Function"))
            ScanOwner(f.Id, f.Name);
        foreach (var c in classes)
            foreach (var m in c.Methods)
                ScanOwner($"{c.Id}#{m.Id}", $"{c.Name}.{m.Name}");

        return hits;
    }

    // All statement lines of one owner, from whichever body diagram exists (structogram and/or flowchart).
    static IEnumerable<string> OwnerLines(string projFolder, string key)
    {
        var lines = new List<string>();
        if (StructogramService.Exists(projFolder, key))
            CollectStruct(StructogramService.Load(projFolder, key).Root, lines);
        if (FlowChartService.Exists(projFolder, key))
            foreach (var n in FlowChartService.Load(projFolder, key).Nodes)
                lines.AddRange((n.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        return lines;
    }

    static void CollectStruct(List<NsBlock> blocks, List<string> into)
    {
        foreach (var b in blocks)
        {
            foreach (var l in (b.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)) into.Add(l);
            CollectStruct(b.Body, into);
            CollectStruct(b.Else, into);
            foreach (var arm in b.Arms) CollectStruct(arm.Body, into);
        }
    }

    static UseKind? Classify(string line, string name, string className)
    {
        var t = line.Trim();
        // Destroyed: explicit teardown of the instance.
        if (Regex.IsMatch(t, $@"{Regex.Escape(name)}\s*\.\s*(Dispose|Close)\s*\(") ||
            Regex.IsMatch(t, $@"{Regex.Escape(name)}\s*=\s*null\b") ||
            // "delete TestObject", "delete(TestObject)", "delete( TestObject )" — C/C++-style teardown verbs.
            Regex.IsMatch(t, $@"\b(delete|destroy|free)\b\s*\(?\s*{Regex.Escape(name)}\b"))
            return UseKind.Destroy;
        // Created: "new …" alongside the name, or a typed declaration "ClassName name".
        if (Regex.IsMatch(t, @"\bnew\b") ||
            (className.Length > 0 && Regex.IsMatch(t, $@"\b{Regex.Escape(className)}\b\s+{Regex.Escape(name)}\b")))
            return UseKind.Create;
        // Otherwise a plain use (member access, argument, …).
        return UseKind.Use;
    }
}
