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
        private readonly Dictionary<string, string> _ann = new();   // element id → its Bemerkung text(s)
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
                // A Bemerkung (Annotation) is documentary — its dashed links are not control flow, so drop any
                // edge touching one; the converter never walks into or out of it.
                if (_nodes.TryGetValue(c.FromId, out var fn) && fn.Kind == FlowNodeKind.Annotation) continue;
                var to = Resolve(c, 0);
                if (to is null || !_nodes.TryGetValue(to, out var tn) || tn.Kind == FlowNodeKind.Annotation) continue;
                list.Add(new Edge(to, c.Label ?? ""));
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

            // Map each Bemerkung (Annotation) to the element its dashed link touches, so its text can be
            // re-attached as a comment on that element's structogram block.
            foreach (var c in fc.Connections)
            {
                string? annId = null, elemId = null;
                if (_nodes.TryGetValue(c.FromId, out var f) && f.Kind == FlowNodeKind.Annotation)
                { annId = c.FromId; elemId = Resolve(c, 0); }
                else if (Resolve(c, 0) is { } to && _nodes.TryGetValue(to, out var tn) && tn.Kind == FlowNodeKind.Annotation)
                { annId = to; elemId = c.FromId; }
                if (annId is null || elemId is null) continue;
                if (!_nodes.TryGetValue(elemId, out var en) || en.Kind == FlowNodeKind.Annotation) continue;
                var text = (_nodes[annId].Text ?? "").Trim();
                if (text.Length == 0) continue;
                _ann[elemId] = _ann.TryGetValue(elemId, out var prev) && prev.Length > 0 ? prev + "; " + text : text;
            }
        }

        private string Ann(string id) => _ann.TryGetValue(id, out var t) ? t : "";

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
                    var join = ImmediatePostDom(cur, stopId);
                    var caseBlock = new NsBlock { Kind = NsBlockKind.Case, Text = node.Text, Note = Ann(node.Id) };
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

                // ── Two exits → loop or if/else ──
                var (tConn, fConn) = OrderTrueFalse(outs);
                bool tBack = Reaches(tConn.ToId, cur);
                bool fBack = Reaches(fConn.ToId, cur);

                // EXACTLY one branch loops back to the decision → pre-test while loop. The loop runs WHILE the
                // back-edge branch is taken — labelled with that branch's own caption when it has one, keeping
                // the decision's wording visible instead of a bare "condition" / "!(condition)".
                if (tBack ^ fBack)
                {
                    var back = tBack ? tConn : fConn;
                    var exit = tBack ? fConn : tConn;
                    // Keep the decision's wording AND which branch loops ("getroffen? = nein"); an expression
                    // label like "!chicken" stands on its own.
                    var cond = !string.IsNullOrWhiteSpace(back.Label)
                             ? LoopCond(node.Text, back.Label)
                             : tBack ? node.Text : $"!({node.Text})";
                    var hdr = cur;   // cut the back-edge while parsing the body, so nodes inside don't read the
                                     // loop's own cycle as their own loop (which duplicated inner ifs).
                    var body = WithLoopCut(hdr, () => ParseRegion(back.ToId, hdr, depth + 1));
                    blocks.Add(new NsBlock { Kind = NsBlockKind.While, Text = cond, Note = Ann(node.Id), Body = body });
                    cur = exit.ToId;
                    continue;
                }

                // BOTH branches return here → cur is the header of an enclosing loop (e.g. a back-edge from far
                // below, via a connector pair). Try to structure the whole natural loop as one While/DoWhile.
                if (tBack && fBack && TryStructureLoop(cur, node, blocks, out var loopExit, depth))
                {
                    cur = loopExit;
                    continue;
                }

                // Otherwise (both branches go forward, or both re-join an enclosing loop) → if/else. The join is
                // the decision's immediate post-dominator, so the code AFTER the if is emitted once — not
                // duplicated at every nesting level (which made deep if-ladders explode exponentially).
                {
                    var ifJoin = ImmediatePostDom(cur, stopId);
                    var ifBlock = new NsBlock
                    {
                        Kind = NsBlockKind.If,
                        Text = node.Text,
                        Note = Ann(node.Id),
                        TrueLabel  = tConn.Label,
                        FalseLabel = fConn.Label,
                        Body = ParseRegion(tConn.ToId, ifJoin, depth + 1),
                        Else = ParseRegion(fConn.ToId, ifJoin, depth + 1)
                    };
                    blocks.Add(ifBlock);
                    if (ifJoin is null) break;   // branches don't reconverge before the region exit
                    cur = ifJoin;
                    continue;
                }
            }

            return blocks;
        }

        // ── Graph helpers ──────────────────────────────────────────────────

        /// <summary>The decision's immediate post-dominator within the region: the NEAREST node that EVERY path
        /// from <paramref name="from"/> must pass through before leaving the region (an End node, the
        /// <paramref name="boundary"/>, or a dead end). Unlike "nearest common-reachable node", a post-dominator
        /// is the TRUE reconvergence point — paths can't slip past it — so the code after a branch is emitted
        /// once instead of being duplicated at every nesting level (which made deep if-ladders blow up
        /// exponentially). Cycles back to <paramref name="from"/> are cut, so a loop header returns null here
        /// (its post-dominator lies past the loop) and is handled as a loop, not an if/else. Null = no
        /// single reconvergence before the region exit.</summary>
        private string? ImmediatePostDom(string from, string? boundary)
        {
            // Candidate joins, nearest first (BFS from the decision; don't expand past End / the boundary).
            var order = new List<string>();
            var seen = new HashSet<string> { from };
            var q = new Queue<string>();
            q.Enqueue(from);
            while (q.Count > 0)
            {
                var id = q.Dequeue();
                if (id == boundary) continue;
                if (_nodes.TryGetValue(id, out var n) && n.Kind == FlowNodeKind.End) continue;
                foreach (var e in Succ(id)) if (seen.Add(e.ToId)) { order.Add(e.ToId); q.Enqueue(e.ToId); }
            }
            foreach (var cand in order)
            {
                if (cand == from) continue;
                // The region boundary itself IS a valid reconvergence: a branch whose only join is the segment
                // end must close THERE, not spill past it into the next segment (which exploded deep ladders).
                if (!CanReachExitAvoiding(from, cand, boundary)) return cand;   // every exit path goes through cand
            }
            return null;
        }

        /// <summary>True if <paramref name="from"/> can reach a region exit (an End node, the
        /// <paramref name="boundary"/>, or a dead end) WITHOUT passing through <paramref name="blocked"/>.
        /// Cycles are cut by the visited set (a path that only loops never reaches an exit).</summary>
        private bool CanReachExitAvoiding(string from, string blocked, string? boundary)
        {
            var seen = new HashSet<string>();
            var stack = new Stack<string>();
            stack.Push(from);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (id == blocked || !seen.Add(id)) continue;
                if (id == boundary) return true;                       // left the region without `blocked`
                if (!_nodes.TryGetValue(id, out var n)) return true;
                if (n.Kind == FlowNodeKind.End) return true;
                var outs = Succ(id);
                if (outs.Count == 0) return true;                      // dead end = an exit
                foreach (var e in outs) stack.Push(e.ToId);
            }
            return false;
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

        // Structures a natural loop whose header is `header` — a back-edge target where BOTH branches return
        // (e.g. a back-edge from far below via a connector pair). Works for a single-entry / single-exit loop:
        // it cuts the back-edge(s) so the body parses acyclically (re-using the normal segmentation), then emits
        // a DoWhile, or — when the exit sits in the MIDDLE of the body ("loop-and-a-half") — a While with the
        // pre-exit part emitted once before and once inside (a single, bounded duplication). Returns false when
        // the loop isn't cleanly single-exit (caller falls back), so an irreducible tangle still gets flagged.
        private bool TryStructureLoop(string header, FlowNode node, List<NsBlock> blocks, out string? exit, int depth)
        {
            exit = null;
            var L = _nodes.Keys.Where(n => n == header || (Reaches(header, n) && Reaches(n, header))).ToHashSet();
            var exitEdges = new List<(string from, string to)>();
            foreach (var n in L) foreach (var e in Succ(n)) if (!L.Contains(e.ToId)) exitEdges.Add((n, e.ToId));
            if (exitEdges.Select(x => x.to).Distinct().Count() != 1) return false;   // not single exit-target
            if (exitEdges.Select(x => x.from).Distinct().Count() != 1) return false; // not single exit-test
            var exitTo = exitEdges[0].to;
            exit = exitTo;
            var xt = exitEdges[0].from;

            WithLoopCut(header, () =>   // cut back-edge(s) into the header so the body parses acyclically
            {
                var A = ParseRegion(header, xt, depth + 1);                         // header … exit test
                var contEdge = Succ(xt).FirstOrDefault(e => e.ToId != exitTo);      // branch that stays in the loop
                var B = contEdge.ToId is null ? new List<NsBlock>() : ParseRegion(contEdge.ToId, header, depth + 1);
                var cond = LoopCond(_nodes[xt].Text, contEdge.Label);
                if (B.Count == 0)
                    blocks.Add(new NsBlock { Kind = NsBlockKind.DoWhile, Text = cond, Note = Ann(node.Id), Body = A });
                else
                {
                    blocks.AddRange(A);                                             // pre-exit part, run once
                    var body = new List<NsBlock>(B);
                    body.AddRange(ParseRegion(header, xt, depth + 1));              // … then repeated in the loop
                    blocks.Add(new NsBlock { Kind = NsBlockKind.While, Text = cond, Note = Ann(node.Id), Body = body });
                }
                return true;
            });
            return true;
        }

        // Runs <paramref name="body"/> with every back-edge into <paramref name="header"/> (an edge from a node
        // the header can reach) temporarily removed, so the loop body parses as an acyclic region. Restores the
        // edges afterwards.
        private T WithLoopCut<T>(string header, Func<T> body)
        {
            var backSrcs = _succ.Keys.Where(p => Reaches(header, p) && _succ[p].Any(e => e.ToId == header)).ToList();
            var removed = new List<(string p, Edge e)>();
            foreach (var p in backSrcs)
                for (int i = _succ[p].Count - 1; i >= 0; i--)
                    if (_succ[p][i].ToId == header) { removed.Add((p, _succ[p][i])); _succ[p].RemoveAt(i); }
            try { return body(); }
            finally { foreach (var (p, e) in removed) _succ[p].Add(e); }
        }

        // The loop condition text from a decision + the looping branch's label. A plain yes/no caption gets
        // combined with the decision wording ("getroffen? = nein"); a label that is ALREADY a boolean
        // expression (e.g. "!chicken") is used on its own — combining would be redundant.
        private static readonly string[] _yesNo =
            { "yes", "true", "ja", "y", "1", "wahr", "no", "false", "nein", "n", "0", "falsch" };
        private static string LoopCond(string decisionText, string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return decisionText;
            if (string.IsNullOrWhiteSpace(decisionText)) return label;
            return _yesNo.Contains(label.Trim().ToLowerInvariant()) ? $"{decisionText} = {label}" : label;
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
            Text = string.IsNullOrWhiteSpace(n.Text) ? "…" : n.Text,
            Note = Ann(n.Id)
        };

        private static NsBlock Flag(string note) => new()
        {
            Kind    = NsBlockKind.Statement,
            Flagged = true,
            Text    = note
        };
    }
}
