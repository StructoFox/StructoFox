using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// Selection-based rich-text plumbing for a <see cref="TextItem"/>'s <c>List&lt;TextRun&gt;</c>. Avalonia has no
/// rich-text editor, so text is typed as plain text and formatting is applied to CHARACTER RANGES; the runs are kept
/// aligned to the characters as the text is edited (Remap). Also turns runs into Avalonia inlines for the live
/// preview and the printed box. Font FAMILY stays per-box (Avalonia can't mix families inline via TextBlock inlines).
/// </summary>
public static class RichText
{
    public static string Plain(IEnumerable<TextRun> runs) => string.Concat(runs.Select(r => r.Text));

    // Ensures a run boundary at char index i; returns the run index that STARTS at i.
    static int SplitAt(List<TextRun> runs, int i)
    {
        if (i <= 0) return 0;
        int pos = 0;
        for (int k = 0; k < runs.Count; k++)
        {
            int len = runs[k].Text.Length;
            if (i == pos) return k;
            if (i < pos + len)
            {
                var left = runs[k].Clone();  left.Text  = runs[k].Text[..(i - pos)];
                var right = runs[k].Clone(); right.Text = runs[k].Text[(i - pos)..];
                runs[k] = left; runs.Insert(k + 1, right);
                return k + 1;
            }
            pos += len;
        }
        return runs.Count;
    }

    static void Merge(List<TextRun> runs)
    {
        for (int k = runs.Count - 1; k > 0; k--)
            if (runs[k].SameFormat(runs[k - 1])) { runs[k - 1].Text += runs[k].Text; runs.RemoveAt(k); }
        runs.RemoveAll(r => r.Text.Length == 0);
        if (runs.Count == 0) runs.Add(new TextRun { Text = "" });
    }

    /// <summary>Applies a formatting mutation to every run in the character range [start,end).</summary>
    public static void Apply(List<TextRun> runs, int start, int end, Action<TextRun> mutate)
    {
        if (end <= start) return;
        int a = SplitAt(runs, start), b = SplitAt(runs, end);
        for (int k = a; k < b; k++) mutate(runs[k]);
        Merge(runs);
    }

    /// <summary>True if every run overlapping [start,end) satisfies <paramref name="pred"/> — used so a toggle
    /// (bold/italic/…) turns OFF when the whole selection already has it.</summary>
    public static bool All(List<TextRun> runs, int start, int end, Func<TextRun, bool> pred)
    {
        if (end <= start) return false;
        int pos = 0; bool any = false;
        foreach (var r in runs)
        {
            int s = Math.Max(start, pos), e = Math.Min(end, pos + r.Text.Length);
            if (e > s) { any = true; if (!pred(r)) return false; }
            pos += r.Text.Length;
        }
        return any;
    }

    /// <summary>Re-aligns runs after the plain text changed old→new (typing/deleting): keep the common prefix and
    /// suffix, replace the middle; inserted text inherits the format at the edit point.</summary>
    public static void Remap(List<TextRun> runs, string oldText, string newText)
    {
        if (oldText == newText) return;
        int max = Math.Min(oldText.Length, newText.Length);
        int pre = 0; while (pre < max && oldText[pre] == newText[pre]) pre++;
        int suf = 0; while (suf < max - pre && oldText[oldText.Length - 1 - suf] == newText[newText.Length - 1 - suf]) suf++;
        int delFrom = pre, delTo = oldText.Length - suf;
        string ins = newText[pre..(newText.Length - suf)];

        var fmt = FormatAt(runs, delFrom - 1) ?? (runs.Count > 0 ? runs[0].Clone() : new TextRun());
        if (delTo > delFrom) { int a = SplitAt(runs, delFrom), b = SplitAt(runs, delTo); runs.RemoveRange(a, b - a); }
        if (ins.Length > 0) { int a = SplitAt(runs, delFrom); var nr = fmt.Clone(); nr.Text = ins; runs.Insert(a, nr); }
        Merge(runs);
    }

    static TextRun? FormatAt(List<TextRun> runs, int i)
    {
        if (i < 0) return runs.Count > 0 ? runs[0].Clone() : null;
        int pos = 0;
        foreach (var r in runs) { if (i < pos + r.Text.Length) return r.Clone(); pos += r.Text.Length; }
        return runs.Count > 0 ? runs[^1].Clone() : null;
    }

    /// <summary>Copies of the runs (or their parts) covering [start,end) — for rendering one line's slice of runs.</summary>
    public static List<TextRun> Slice(IEnumerable<TextRun> runs, int start, int end)
    {
        var outp = new List<TextRun>();
        int pos = 0;
        foreach (var r in runs)
        {
            int s = Math.Max(start, pos), e = Math.Min(end, pos + r.Text.Length);
            if (e > s) { var c = r.Clone(); c.Text = r.Text.Substring(s - pos, e - s); outp.Add(c); }
            pos += r.Text.Length;
        }
        if (outp.Count == 0) outp.Add(new TextRun { Text = "" });
        return outp;
    }

    /// <summary>Runs → Avalonia inlines. <paramref name="boxSize"/> is the box font size (unscaled),
    /// <paramref name="scale"/> the display scale; a run without its own Size inherits the host TextBlock's size.</summary>
    public static IEnumerable<Inline> ToInlines(IEnumerable<TextRun> runs, double boxSize, double scale, IBrush defaultFg)
    {
        foreach (var r in runs)
        {
            var run = new Run(r.Text)
            {
                FontWeight = r.Bold ? FontWeight.Bold : FontWeight.Normal,
                FontStyle  = r.Italic ? FontStyle.Italic : FontStyle.Normal,
                Foreground = r.Fg is { } fg ? Parse(fg, defaultFg) : defaultFg,
            };
            var deco = new TextDecorationCollection();
            if (r.Underline) deco.Add(new TextDecoration { Location = TextDecorationLocation.Underline });
            if (r.Strike)    deco.Add(new TextDecoration { Location = TextDecorationLocation.Strikethrough });
            if (deco.Count > 0) run.TextDecorations = deco;
            if (r.Marker is { } mk) run.Background = Parse(mk, Brushes.Transparent);

            if (r.Super || r.Sub)
            {
                run.BaselineAlignment = r.Super ? BaselineAlignment.Superscript : BaselineAlignment.Subscript;
                run.FontSize = (r.Size is { } ss && ss > 0 ? ss : boxSize) * scale * 0.72;
            }
            else if (r.Size is { } rs && rs > 0)
                run.FontSize = rs * scale;
            yield return run;
        }
    }

    static IBrush Parse(string hex, IBrush fallback)
    { try { return string.IsNullOrWhiteSpace(hex) ? fallback : new SolidColorBrush(Color.Parse(hex)); } catch { return fallback; } }
}
