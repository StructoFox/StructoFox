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
    public static StructogramData Convert(FlowChartData fc, string title)
    {
        var sd = new StructogramData { Title = title };
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
        return sd;
    }

    private sealed class Ctx
    {
        private readonly record struct Edge(string ToId, string Label);

        private readonly Dictionary<string, FlowNode> _nodes;
        private readonly Dictionary<string, List<Edge>> _succ;
        private static readonly List<Edge> _noEdges = new();
        private int _budget = 2000;   // overall block budget (guards against pathological graphs)

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
                    blocks.Add(Flag("diagram too complex or cyclic to structure here"));
                    break;
                }
                if (!_nodes.TryGetValue(cur, out var node)) break;
                if (node.Kind == FlowNodeKind.End) break;
                if (node.Kind == FlowNodeKind.Start) { cur = One(cur); continue; }

                var outs = Succ(cur);

                if (outs.Count == 0) { blocks.Add(Stmt(node)); break; }
                if (outs.Count == 1) { blocks.Add(Stmt(node)); cur = outs[0].ToId; continue; }

                // ── Multi-way (3+ exits) → Case ──
                if (outs.Count >= 3)
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
                if (tBack && !fBack)
                {
                    blocks.Add(new NsBlock { Kind = NsBlockKind.While, Text = node.Text, Body = ParseRegion(tConn.ToId, cur, depth + 1) });
                    cur = fConn.ToId;
                    continue;
                }
                if (fBack && !tBack)
                {
                    blocks.Add(new NsBlock { Kind = NsBlockKind.While, Text = $"!({node.Text})", Body = ParseRegion(fConn.ToId, cur, depth + 1) });
                    cur = tConn.ToId;
                    continue;
                }

                // 3) Anything else (both branches loop, irreducible) → flag and stop.
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
