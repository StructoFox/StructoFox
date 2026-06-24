using StructoFox.Core.Models;

namespace StructoFox.Core;

/// <summary>
/// Converts a flowchart (Programmablaufplan) into a Nassi-Shneiderman structogram by
/// recursive structural analysis. Well-formed flowcharts (reconverging decisions,
/// proper loops, multi-way selections) convert cleanly. Regions that cannot be
/// structured (arbitrary jumps / spaghetti) become a single <see cref="NsBlock.Flagged"/>
/// block so the user sees exactly where it failed instead of getting a wrong result.
/// </summary>
public static class StructogramConverter
{
    public static StructogramData Convert(FlowChartData fc, string title) => Convert(fc, title, out _);

    /// <summary>Converts and also reports the flowchart node ids the converter could NOT structure, so the
    /// caller can mark them in the PAP (where they can actually be fixed).</summary>
    public static StructogramData Convert(FlowChartData fc, string title, out List<string> unstructured)
    {
        var sd = new StructogramData { Title = title };
        unstructured = new List<string>();
        if (fc.Nodes.Count == 0) return sd;

        var ctx = new Ctx(fc);
        // Entry: a Start node (skip into its successor), else a node with no incoming edge.
        var start = fc.Nodes.FirstOrDefault(n => n.Kind == FlowNodeKind.Start);
        string? entry;
        if (start is not null)
            entry = ctx.One(start.Id);
        else
        {
            var hasIn = fc.Connections.Select(c => c.ToId).ToHashSet();
            entry = (fc.Nodes.FirstOrDefault(n => !hasIn.Contains(n.Id)) ?? fc.Nodes[0]).Id;
        }

        sd.Root.AddRange(ctx.ParseRegion(entry, null, 0));
        unstructured = ctx.Flagged.ToList();
        return sd;
    }

    private sealed class Ctx
    {
        private readonly record struct Edge(string ToId, string Label);

        private readonly Dictionary<string, FlowNode> _nodes;
        private readonly Dictionary<string, List<Edge>> _succ;
        private static readonly List<Edge> _noEdges = new();
        private int _budget = 2000;   // overall block budget (guards against pathological graphs)
        public readonly HashSet<string> Flagged = new();   // flowchart node ids that could not be structured

        public Ctx(FlowChartData fc)
        {
            _nodes = fc.Nodes.ToDictionary(n => n.Id);
            _succ  = fc.Nodes.ToDictionary(n => n.Id, _ => new List<Edge>());

            // A "tap" connection ends on another LINE, not a node (ToId is empty). Resolve it to the node
            // that the tapped line eventually leads to, so the flow can be followed — and so a stray empty
            // id never reaches the graph helpers (which would throw on _succ[""]).
            string? Resolve(FlowConnection c, int d)
            {
                if (d > 64) return null;
                if (string.IsNullOrEmpty(c.ToTapConn)) return string.IsNullOrEmpty(c.ToId) ? null : c.ToId;
                var tapped = fc.Connections.FirstOrDefault(x => x.Id == c.ToTapConn);
                return tapped is null ? null : Resolve(tapped, d + 1);
            }

            foreach (var c in fc.Connections)
            {
                if (!_succ.TryGetValue(c.FromId, out var list)) continue;
                var to = Resolve(c, 0);
                if (to is not null && _nodes.ContainsKey(to)) list.Add(new Edge(to, c.Label ?? ""));
            }

            // Connector pairs (Sammelpunkte) replace a drawn line: a terminal connector "A" (a line goes IN,
            // none out) continues at the matching connector "A" that has an outgoing edge. Link them so the
            // flow follows the jump — when the match is unambiguous.
            var connectors = fc.Nodes.Where(n => n.Kind == FlowNodeKind.Connector).ToList();
            foreach (var ex in connectors)
            {
                if (_succ[ex.Id].Count > 0) continue;                       // already has its own continuation
                var label = (ex.Text ?? "").Trim();
                if (label.Length == 0) continue;
                var entries = connectors.Where(d => d.Id != ex.Id
                        && string.Equals((d.Text ?? "").Trim(), label, StringComparison.OrdinalIgnoreCase)
                        && _succ[d.Id].Count > 0).ToList();
                if (entries.Count == 1) _succ[ex.Id].Add(new Edge(entries[0].Id, ""));
            }
        }

        // Successors of a node id — never throws on an unknown id.
        private List<Edge> Succ(string id) => _succ.TryGetValue(id, out var l) ? l : _noEdges;

        /// <summary>The single successor of a node (or null).</summary>
        public string? One(string id) { var l = Succ(id); return l.Count > 0 ? l[0].ToId : null; }

        public List<NsBlock> ParseRegion(string? startId, string? stopId, int depth)
        {
            var blocks = new List<NsBlock>();
            var cur = startId;
            var localGuard = 0;

            while (cur is not null && cur != stopId)
            {
                if (--_budget <= 0 || ++localGuard > 1000 || depth > 200)
                {
                    if (cur is not null) Flagged.Add(cur);
                    blocks.Add(Flag("diagram too complex or cyclic to structure here"));
                    break;
                }
                if (!_nodes.TryGetValue(cur, out var node)) break;
                if (node.Kind == FlowNodeKind.End) break;
                if (node.Kind == FlowNodeKind.Start) { cur = One(cur); continue; }
                // Collector points / junctions carry no statement of their own. With one (or no) exit they
                // are transparent — the flow passes straight through. But with several exits they ARE the
                // fan-out (a labelled bus / multi-way), so fall through to the If/Case handling below.
                if (node.Kind is FlowNodeKind.Connector or FlowNodeKind.Junction && Succ(cur).Count <= 1)
                { cur = One(cur); continue; }

                var outs = Succ(cur);

                if (outs.Count == 0) { blocks.Add(Stmt(node)); break; }
                if (outs.Count == 1) { blocks.Add(Stmt(node)); cur = outs[0].ToId; continue; }

                // ── Multi-way → Case ── (3+ exits, or a Multi-Verzweigung node with 2+ tines: it is an
                // explicit switch/case, so even two labelled tines render as a Case rather than an if/else).
                if (outs.Count >= 3 || (node.Kind == FlowNodeKind.MultiDecision && outs.Count >= 2))
                {
                    var join = FindJoinMulti(outs.Select(o => o.ToId).ToList(), cur);
                    var caseBlock = new NsBlock { Kind = NsBlockKind.Case, Text = node.Text };
                    foreach (var o in outs)
                        caseBlock.Arms.Add(new NsArm
                        {
                            Label = string.IsNullOrWhiteSpace(o.Label) ? "case" : o.Label,
                            Body  = ParseRegion(o.ToId, join, depth + 1)
                        });
                    blocks.Add(caseBlock);
                    if (join is null) break;
                    cur = join;
                    continue;
                }

                // ── Two exits → if/else or loop ──
                var (tConn, fConn) = OrderTrueFalse(outs);

                // 1) Reconverging branches → if/else (preferred interpretation).
                var ifJoin = FindJoin(tConn.ToId, fConn.ToId, cur);
                if (ifJoin is not null || (!Reaches(tConn.ToId, cur) && !Reaches(fConn.ToId, cur)))
                {
                    var ifBlock = new NsBlock
                    {
                        Kind = NsBlockKind.If,
                        Text = node.Text,
                        TrueLabel  = tConn.Label,
                        FalseLabel = fConn.Label,
                        Body = ParseRegion(tConn.ToId, ifJoin, depth + 1),
                        Else = ParseRegion(fConn.ToId, ifJoin, depth + 1)
                    };
                    blocks.Add(ifBlock);
                    if (ifJoin is null) break;   // both branches reached End
                    cur = ifJoin;
                    continue;
                }

                // 2) One branch loops back to the decision → pre-test while loop.
                bool tBack = Reaches(tConn.ToId, cur);
                bool fBack = Reaches(fConn.ToId, cur);
                // The loop runs WHILE the back-edge branch is taken — so label the loop with that branch's
                // own caption (e.g. "!chicken") when it has one, which keeps the decision's wording visible
                // instead of a bare "condition" / "!(condition)".
                if (tBack && !fBack)
                {
                    var cond = string.IsNullOrWhiteSpace(tConn.Label) ? node.Text : tConn.Label;
                    blocks.Add(new NsBlock { Kind = NsBlockKind.While, Text = cond, Body = ParseRegion(tConn.ToId, cur, depth + 1) });
                    cur = fConn.ToId;
                    continue;
                }
                if (fBack && !tBack)
                {
                    var cond = string.IsNullOrWhiteSpace(fConn.Label) ? $"!({node.Text})" : fConn.Label;
                    blocks.Add(new NsBlock { Kind = NsBlockKind.While, Text = cond, Body = ParseRegion(fConn.ToId, cur, depth + 1) });
                    cur = tConn.ToId;
                    continue;
                }

                // 3) Anything else (both branches loop, irreducible) → flag and stop.
                Flagged.Add(node.Id);
                blocks.Add(Flag($"unstructured branch at: {Short(node.Text)}"));
                break;
            }

            return blocks;
        }

        // ── Graph helpers ──────────────────────────────────────────────────

        /// <summary>Forward-reachable node set from <paramref name="from"/>, never passing through
        /// <paramref name="boundary"/> or beyond End nodes.</summary>
        private HashSet<string> Reachable(string from, string boundary)
        {
            var seen = new HashSet<string>();
            var stack = new Stack<string>();
            stack.Push(from);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (id == boundary || !seen.Add(id)) continue;
                if (_nodes.TryGetValue(id, out var n) && n.Kind == FlowNodeKind.End) continue;
                foreach (var c in Succ(id)) stack.Push(c.ToId);
            }
            return seen;
        }

        /// <summary>Nearest node reachable from BOTH branches (the reconvergence point), or null.</summary>
        private string? FindJoin(string a, string b, string boundary)
        {
            var reachA = Reachable(a, boundary);
            // BFS from b for the nearest node also reachable from a.
            var seen = new HashSet<string>();
            var queue = new Queue<string>();
            queue.Enqueue(b);
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (id == boundary || !seen.Add(id)) continue;
                if (reachA.Contains(id)) return id;
                if (_nodes.TryGetValue(id, out var n) && n.Kind == FlowNodeKind.End) continue;
                foreach (var c in Succ(id)) queue.Enqueue(c.ToId);
            }
            return null;
        }

        private string? FindJoinMulti(List<string> branches, string boundary)
        {
            if (branches.Count == 0) return null;
            var common = Reachable(branches[0], boundary);
            for (int i = 1; i < branches.Count; i++)
                common.IntersectWith(Reachable(branches[i], boundary));
            common.Remove(boundary);
            // Pick the join nearest to the first branch (BFS order from branches[0]).
            var seen = new HashSet<string>();
            var queue = new Queue<string>();
            queue.Enqueue(branches[0]);
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (!seen.Add(id)) continue;
                if (common.Contains(id)) return id;
                foreach (var c in Succ(id)) queue.Enqueue(c.ToId);
            }
            return null;
        }

        /// <summary>True if <paramref name="target"/> is forward-reachable from <paramref name="from"/>.</summary>
        private bool Reaches(string from, string target)
        {
            var seen = new HashSet<string>();
            var stack = new Stack<string>();
            stack.Push(from);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (id == target) return true;
                if (!seen.Add(id)) continue;
                if (_nodes.TryGetValue(id, out var n) && n.Kind == FlowNodeKind.End) continue;
                foreach (var c in Succ(id)) stack.Push(c.ToId);
            }
            return false;
        }

        private static (Edge t, Edge f) OrderTrueFalse(List<Edge> outs)
        {
            var affirmative = new[] { "yes", "true", "ja", "y", "1", "wahr" };
            int ti = outs.FindIndex(o => affirmative.Contains((o.Label ?? "").Trim().ToLowerInvariant()));
            if (ti >= 0)
                return (outs[ti], outs[ti == 0 ? 1 : 0]);
            return (outs[0], outs[1]);
        }

        private NsBlock Stmt(FlowNode n) => new()
        {
            Kind = NsBlockKind.Statement,
            Text = string.IsNullOrWhiteSpace(n.Text) ? "…" : n.Text
        };

        private static NsBlock Flag(string note) => new()
        {
            Kind    = NsBlockKind.Statement,
            Flagged = true,
            Text    = note
        };

        private static string Short(string s) =>
            string.IsNullOrWhiteSpace(s) ? "(node)" : (s.Length > 40 ? s[..40] + "…" : s);
    }
}
