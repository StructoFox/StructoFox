using StructoFox.Core.Models;

namespace StructoFox.Core;

/// <summary>
/// Heuristically extracts variable DECLARATIONS from a function's diagram (its statements), so the editor can
/// show a "function header" of declared variables. A statement counts as a declaration when — before an
/// optional <c>=</c> — it has at least two tokens (<c>Type Name</c>), e.g. "int summe = 0" or "List&lt;int&gt;
/// liste"; a plain assignment ("summe = summe + 1", one token before <c>=</c>) is ignored. Variables declared
/// inside a loop are reported separately as loop variables. Language-neutral and best-effort.
/// </summary>
public static class VariableScanner
{
    public readonly record struct Decl(string Name, string Type);

    /// <summary>Scans a structogram: declarations inside a While/DoWhile body are loop variables.</summary>
    public static (List<Decl> vars, List<Decl> loopVars) FromStructogram(StructogramData sd)
    {
        var vars = new List<Decl>(); var loop = new List<Decl>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Walk(List<NsBlock> blocks, bool inLoop)
        {
            foreach (var b in blocks)
            {
                if (b.Kind is NsBlockKind.Statement)
                    AddFrom(b.Text, inLoop, vars, loop, seen);
                if (b.Kind is NsBlockKind.While or NsBlockKind.DoWhile) Walk(b.Body, true);
                else
                {
                    Walk(b.Body, inLoop);
                    Walk(b.Else, inLoop);
                    foreach (var arm in b.Arms) Walk(arm.Body, inLoop);
                }
            }
        }
        Walk(sd.Root, false);
        return (vars, loop);
    }

    /// <summary>Scans a flowchart: a declaration on a node that lies on a cycle is a loop variable.</summary>
    public static (List<Decl> vars, List<Decl> loopVars) FromFlowChart(FlowChartData fc)
    {
        var vars = new List<Decl>(); var loop = new List<Decl>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        List<string> Succ(string id) => fc.Connections
            .Where(c => c.FromId == id && !string.IsNullOrEmpty(c.ToId)).Select(c => c.ToId).ToList();
        bool InCycle(string id)
        {
            var stack = new Stack<string>(Succ(id)); var visited = new HashSet<string>();
            while (stack.Count > 0)
            {
                var x = stack.Pop();
                if (x == id) return true;
                if (!visited.Add(x)) continue;
                foreach (var s in Succ(x)) stack.Push(s);
            }
            return false;
        }

        foreach (var n in fc.Nodes.Where(n => n.Kind == FlowNodeKind.Process))
            AddFrom(n.Text, InCycle(n.Id), vars, loop, seen);
        return (vars, loop);
    }

    static void AddFrom(string? text, bool inLoop, List<Decl> vars, List<Decl> loop, HashSet<string> seen)
    {
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            if (ParseDecl(line) is { } d && seen.Add(d.Name))
                (inLoop ? loop : vars).Add(d);
    }

    /// <summary>Parses "Type Name" / "Type Name = value" into a declaration, or null if the line is not one
    /// (e.g. a plain assignment, a call, or a control keyword).</summary>
    public static Decl? ParseDecl(string line)
    {
        var s = line.Trim().TrimEnd(';').Trim();
        if (s.Length == 0) return null;

        var left = s;                                   // part before an optional '='
        int eq = FirstAssign(s);
        if (eq >= 0) left = s[..eq].Trim();
        if (left.Contains('(') || left.Contains('[') || left.Contains('.')) return null;   // call / index / member

        var tokens = left.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return null;             // one token before '=' → assignment, not a declaration

        var name = tokens[^1];
        if (!IsIdentifier(name)) return null;
        var type = string.Join(' ', tokens[..^1]);
        // Reject control keywords masquerading as a "type" (return/if/while/for/…).
        if (Keywords.Contains(tokens[0].ToLowerInvariant())) return null;
        return new Decl(name, type);
    }

    // Index of the '=' that is an assignment (not ==, <=, >=, !=).
    static int FirstAssign(string s)
    {
        for (int i = 0; i < s.Length; i++)
            if (s[i] == '=' && (i == 0 || "=<>!".IndexOf(s[i - 1]) < 0) && (i + 1 >= s.Length || s[i + 1] != '='))
                return i;
        return -1;
    }

    static bool IsIdentifier(string t)
        => t.Length > 0 && (char.IsLetter(t[0]) || t[0] == '_') && t.All(c => char.IsLetterOrDigit(c) || c == '_');

    static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    { "return", "if", "else", "while", "for", "do", "switch", "case", "break", "continue", "goto", "throw", "using" };
}
