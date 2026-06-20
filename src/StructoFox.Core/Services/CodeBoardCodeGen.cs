using StructoFox.Core.Models;

namespace StructoFox.Core;

/// <summary>
/// Turns a code board's dataflow wiring into a function/method body — a third authoring surface beside
/// the flowchart and structogram. Connected function cards become an ordered call sequence: each
/// output feeds the matching input of the next, producers emitted before consumers (topological order).
/// The result is a <see cref="StructogramData"/>, so it flows through the normal structogram→code path.
/// </summary>
public static class CodeBoardCodeGen
{
    /// <summary>Builds a body structogram from the board: a sequence of calls wired output→input.</summary>
    public static StructogramData GenerateBody(string title, CodeBoardData data, IReadOnlyDictionary<string, CodeEntity> entities)
    {
        var sd = new StructogramData { Title = title };

        // Only function cards present on the board take part in the call sequence.
        var funcs = data.Positions.Keys
            .Where(entities.ContainsKey)
            .Select(id => entities[id])
            .Where(e => e.EntityType == CodeEntityType.Function)
            .GroupBy(e => e.Id).Select(g => g.First())
            .ToDictionary(e => e.Id);

        if (funcs.Count == 0)
        {
            sd.Root.Add(new NsBlock { Kind = NsBlockKind.Statement, Text = "// no functions wired on the board" });
            return sd;
        }

        // Entity-level edges (producer → consumer) from the port relations, with incoming counts.
        var incoming = funcs.Keys.ToDictionary(id => id, _ => 0);
        var adj      = funcs.Keys.ToDictionary(id => id, _ => new List<string>());
        foreach (var r in data.Relations)
        {
            if (r.FromId == r.ToId || !funcs.ContainsKey(r.FromId) || !funcs.ContainsKey(r.ToId)) continue;
            adj[r.FromId].Add(r.ToId);
            incoming[r.ToId]++;
        }

        // Kahn topological sort (producers first); ties broken by name for stable output.
        var inc   = new Dictionary<string, int>(incoming);
        var queue = new Queue<string>(funcs.Keys.Where(id => inc[id] == 0).OrderBy(id => funcs[id].Name, StringComparer.OrdinalIgnoreCase));
        var order = new List<string>();
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            order.Add(id);
            foreach (var c in adj[id].OrderBy(c => funcs[c].Name, StringComparer.OrdinalIgnoreCase))
                if (--inc[c] == 0) queue.Enqueue(c);
        }
        // Anything left took part in a cycle — emit it too, but flagged for review.
        var cyclic = funcs.Keys.Where(id => !order.Contains(id)).ToList();

        string Var(CodeEntity f) => Camel(f.Name);

        // One call statement: "result = Name(args)" (or just "Name(args)" with no output port).
        string Emit(CodeEntity f)
        {
            var args = f.Ports.Where(p => p.Direction == PortDirection.Input).Select(p =>
            {
                var rel = data.Relations.FirstOrDefault(r => r.ToId == f.Id && r.ToPortId == p.Id && funcs.ContainsKey(r.FromId));
                return rel is not null ? Var(funcs[rel.FromId]) : p.Name;   // wired input → producer var, else placeholder
            });
            var call = $"{f.Name}({string.Join(", ", args)})";
            return f.Ports.Any(p => p.Direction == PortDirection.Output) ? $"{Var(f)} = {call}" : call;
        }

        foreach (var id in order)  sd.Root.Add(new NsBlock { Kind = NsBlockKind.Statement, Text = Emit(funcs[id]) });
        foreach (var id in cyclic) sd.Root.Add(new NsBlock { Kind = NsBlockKind.Statement, Text = Emit(funcs[id]), Flagged = true });
        return sd;
    }

    static string Camel(string s) => string.IsNullOrEmpty(s) ? "result" : char.ToLowerInvariant(s[0]) + s[1..];

    /// <summary>True if the board holds any non-Function entity (class/struct/interface/enum/object/
    /// namespace). Such boards are composition/architecture views and must NOT author a function body —
    /// that would invite nasty nesting loops — so assignment is refused for them.</summary>
    public static bool ContainsNonFunction(string projFolder, string boardId)
    {
        var data = CodeBoardDataService.Load(projFolder, boardId);
        var ents = new Dictionary<string, CodeEntity>();
        foreach (var t in CodeEntityService.EntityTypes)
            foreach (var e in CodeEntityService.LoadAll(projFolder, t))
                ents[e.Id] = e;
        return data.Positions.Keys.Any(id => ents.TryGetValue(id, out var e) && e.EntityType != CodeEntityType.Function);
    }
}
