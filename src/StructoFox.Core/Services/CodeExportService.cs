using System.Text;
using StructoFox.Core.Models;

namespace StructoFox.Core;

public enum ExportLanguage { CSharp, Cpp, Java, TypeScript, Python, Kotlin, Swift, Php, Go, Rust, Verse }

/// <summary>
/// Generates code skeletons from CodeEntity definitions for several languages.
/// OOP languages map cleanly; Go and Rust have no class inheritance, so a base class
/// is approximated by composition/embedding (with a note).
/// </summary>
public static class CodeExportService
{
    public static string FileExtension(ExportLanguage lang) => lang switch
    {
        ExportLanguage.CSharp     => "cs",
        ExportLanguage.Cpp        => "h",
        ExportLanguage.Java       => "java",
        ExportLanguage.TypeScript => "ts",
        ExportLanguage.Python     => "py",
        ExportLanguage.Kotlin     => "kt",
        ExportLanguage.Swift      => "swift",
        ExportLanguage.Php        => "php",
        ExportLanguage.Go         => "go",
        ExportLanguage.Rust       => "rs",
        ExportLanguage.Verse      => "verse",
        _                         => "txt"
    };

    /// <summary>The line-comment token for a language (Python and Verse use '#', the rest '//').</summary>
    private static string Cmt(ExportLanguage lang) => lang is ExportLanguage.Python or ExportLanguage.Verse ? "#" : "//";

    /// <summary>Project folder used to load per-method structograms for body generation. Null = skeleton bodies only.</summary>
    private static string? _projForBodies;

    public static string Generate(IEnumerable<CodeEntity> entities, ExportLanguage lang, string? projFolder = null)
    {
        _projForBodies = projFolder;
        var all  = entities.ToList();
        var byId = all.ToDictionary(e => e.Id);
        string Name(string id) => byId.TryGetValue(id, out var e) ? e.Name : "";

        var sb = new StringBuilder();
        sb.AppendLine($"{Cmt(lang)} Auto-generated skeleton from StructoFox. Fill in the logic.");
        if (lang == ExportLanguage.Php) sb.AppendLine("<?php");
        if (lang == ExportLanguage.Python) sb.AppendLine(PythonImports(all));
        sb.AppendLine();

        var groups = all.GroupBy(e => e.Namespace?.Trim() ?? "").OrderBy(g => g.Key);

        foreach (var grp in groups)
        {
            bool hasNs = !string.IsNullOrWhiteSpace(grp.Key);
            string ind = "";
            bool braceNs = lang is ExportLanguage.CSharp or ExportLanguage.Cpp or ExportLanguage.TypeScript or ExportLanguage.Php or ExportLanguage.Rust;

            if (hasNs)
            {
                if (braceNs)
                {
                    string nsKw = lang == ExportLanguage.Rust ? $"mod {grp.Key.ToLowerInvariant()}" : $"namespace {grp.Key}";
                    sb.AppendLine($"{nsKw} {{");
                    ind = "    ";
                }
                else
                {
                    // package/module style can't repeat per group in one file → comment marker
                    sb.AppendLine($"{Cmt(lang)} namespace / package: {grp.Key}");
                }
            }

            foreach (var e in grp.OrderBy(SortRank).ThenBy(x => x.Name))
            {
                EmitEntity(sb, e, lang, ind, Name);
                sb.AppendLine();
            }

            if (hasNs && braceNs) sb.AppendLine("}");
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    private static int SortRank(CodeEntity e) => e.IsEntryPoint ? -1 : e.EntityType switch
    {
        CodeEntityType.Enum => 0, CodeEntityType.Interface => 1, CodeEntityType.Struct => 2,
        CodeEntityType.Class => 3, CodeEntityType.Function => 4, CodeEntityType.Object => 5, _ => 6
    };

    private static void EmitEntity(StringBuilder sb, CodeEntity e, ExportLanguage lang, string ind, Func<string, string> name)
    {
        Doc(sb, e, ind, Cmt(lang));

        // The entry point is flagged and, in languages where main must live inside a class
        // (Java; classic C#), wrapped in a holder class. Elsewhere it stays a free function.
        if (e.EntityType == CodeEntityType.Function && e.IsEntryPoint)
        {
            sb.AppendLine($"{ind}{Cmt(lang)} ▶ Entry point (main)");
            if (lang is ExportLanguage.Java or ExportLanguage.CSharp)
            {
                sb.AppendLine($"{ind}public class Program");
                sb.AppendLine($"{ind}{{");
                EmitByLang(sb, e, lang, ind + "    ", name);
                sb.AppendLine($"{ind}}}");
                return;
            }
        }
        EmitByLang(sb, e, lang, ind, name);
    }

    private static void EmitByLang(StringBuilder sb, CodeEntity e, ExportLanguage lang, string ind, Func<string, string> name)
    {
        switch (lang)
        {
            case ExportLanguage.CSharp:     EmitCSharp(sb, e, ind, name); break;
            case ExportLanguage.Cpp:        EmitCpp(sb, e, ind, name); break;
            case ExportLanguage.Java:       EmitJava(sb, e, ind, name); break;
            case ExportLanguage.TypeScript: EmitTypeScript(sb, e, ind, name); break;
            case ExportLanguage.Python:     EmitPython(sb, e, ind, name); break;
            case ExportLanguage.Kotlin:     EmitKotlin(sb, e, ind, name); break;
            case ExportLanguage.Swift:      EmitSwift(sb, e, ind, name); break;
            case ExportLanguage.Php:        EmitPhp(sb, e, ind, name); break;
            case ExportLanguage.Go:         EmitGo(sb, e, ind, name); break;
            case ExportLanguage.Rust:       EmitRust(sb, e, ind, name); break;
            case ExportLanguage.Verse:      EmitVerse(sb, e, ind, name); break;
        }
    }

    // ── Shared helpers ──────────────────────────────────────────────────────

    private static void Doc(StringBuilder sb, CodeEntity e, string ind, string marker = "//")
    {
        if (string.IsNullOrWhiteSpace(e.Description)) return;
        foreach (var line in e.Description.Split('\n'))
            sb.AppendLine($"{ind}{marker} {line.TrimEnd()}");
    }

    private static List<string> Bases(CodeEntity e, Func<string, string> name)
    {
        var b = new List<string>();
        if (!string.IsNullOrEmpty(e.BaseClassId)) b.Add(name(e.BaseClassId));
        b.AddRange(e.ImplementsIds.Select(name));
        return b.Where(s => !string.IsNullOrEmpty(s)).ToList();
    }

    private static (List<CodePort> ins, string ret) FuncSig(CodeEntity e)
    {
        var ins = e.Ports.Where(p => p.Direction == PortDirection.Input).ToList();
        var outp = e.Ports.FirstOrDefault(p => p.Direction == PortDirection.Output);
        var ret = outp?.DataType is { Length: > 0 } rt ? rt : "void";
        return (ins, ret);
    }

    // ── Deterministic structogram → method body (braced C-family: C#, Java, TS, PHP) ──

    /// <summary>
    /// If a non-empty structogram exists for <paramref name="key"/>, renders it as a braced
    /// method body directly into <paramref name="sb"/> and returns true. Otherwise returns false
    /// (caller emits its default placeholder body).
    /// </summary>
    private static bool TryBracedBody(string key, StringBuilder sb, string ind)
    {
        if (_projForBodies is null) return false;
        if (!StructogramService.Exists(_projForBodies, key)) return false;
        var sd = StructogramService.Load(_projForBodies, key);
        if (sd.Root.Count == 0) return false;
        RenderBracedSeq(sb, sd.Root, ind);
        return true;
    }

    private static void RenderBracedSeq(StringBuilder sb, List<Models.NsBlock> blocks, string ind)
    {
        foreach (var b in blocks) RenderBracedBlock(sb, b, ind);
    }

    private static void RenderBracedBlock(StringBuilder sb, Models.NsBlock b, string ind)
    {
        var inner = ind + "    ";
        switch (b.Kind)
        {
            case Models.NsBlockKind.Statement:
                var s = StTerm(b.Text);
                if (s.Length > 0) sb.AppendLine($"{ind}{s}");
                break;

            case Models.NsBlockKind.If:
                sb.AppendLine($"{ind}if ({CondText(b.Text)})");
                sb.AppendLine($"{ind}{{");
                RenderBracedSeq(sb, b.Body, inner);
                sb.AppendLine($"{ind}}}");
                if (b.Else.Count > 0)
                {
                    sb.AppendLine($"{ind}else");
                    sb.AppendLine($"{ind}{{");
                    RenderBracedSeq(sb, b.Else, inner);
                    sb.AppendLine($"{ind}}}");
                }
                break;

            case Models.NsBlockKind.While:
                sb.AppendLine($"{ind}while ({CondText(b.Text)})");
                sb.AppendLine($"{ind}{{");
                RenderBracedSeq(sb, b.Body, inner);
                sb.AppendLine($"{ind}}}");
                break;

            case Models.NsBlockKind.DoWhile:
                sb.AppendLine($"{ind}do");
                sb.AppendLine($"{ind}{{");
                RenderBracedSeq(sb, b.Body, inner);
                sb.AppendLine($"{ind}}} while ({CondText(b.Text)});");
                break;

            case Models.NsBlockKind.Case:
                sb.AppendLine($"{ind}switch ({CondText(b.Text)})");
                sb.AppendLine($"{ind}{{");
                foreach (var arm in b.Arms)
                {
                    sb.AppendLine($"{inner}case {arm.Label}:");
                    RenderBracedSeq(sb, arm.Body, inner + "    ");
                    sb.AppendLine($"{inner}    break;");
                }
                sb.AppendLine($"{ind}}}");
                break;
        }
    }

    /// <summary>Appends a statement terminator unless the text already ends in ; { or }.</summary>
    private static string StTerm(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return "";
        return (t.EndsWith(";") || t.EndsWith("{") || t.EndsWith("}")) ? t : t + ";";
    }

    /// <summary>Cleans a condition/selector text (drops a trailing '?', defaults to true).</summary>
    private static string CondText(string t)
    {
        t = (t ?? "").Trim();
        if (t.EndsWith("?")) t = t[..^1].Trim();
        return t.Length == 0 ? "true" : t;
    }

    // ── Braceless C-like body (Kotlin / Swift / Go): braces, no statement semicolons ──

    private static bool TryBracelessBody(string key, StringBuilder sb, string ind, ExportLanguage lang)
    {
        if (_projForBodies is null || !StructogramService.Exists(_projForBodies, key)) return false;
        var sd = StructogramService.Load(_projForBodies, key);
        if (sd.Root.Count == 0) return false;
        RenderBracelessSeq(sb, sd.Root, ind, lang);
        return true;
    }

    private static void RenderBracelessSeq(StringBuilder sb, List<Models.NsBlock> blocks, string ind, ExportLanguage lang)
    {
        foreach (var b in blocks) RenderBracelessBlock(sb, b, ind, lang);
    }

    private static void RenderBracelessBlock(StringBuilder sb, Models.NsBlock b, string ind, ExportLanguage lang)
    {
        var inner = ind + "    ";
        // Go uses `for` for every loop; Swift uses `repeat … while`; switch needs no break in any of the three.
        switch (b.Kind)
        {
            case Models.NsBlockKind.Statement:
                var s = (b.Text ?? "").Trim();
                if (s.Length > 0) sb.AppendLine($"{ind}{s}");
                break;

            case Models.NsBlockKind.If:
                sb.AppendLine($"{ind}if ({CondText(b.Text)}) {{");
                RenderBracelessSeq(sb, b.Body, inner, lang);
                if (b.Else.Count > 0)
                {
                    sb.AppendLine($"{ind}}} else {{");
                    RenderBracelessSeq(sb, b.Else, inner, lang);
                }
                sb.AppendLine($"{ind}}}");
                break;

            case Models.NsBlockKind.While:
                sb.AppendLine(lang == ExportLanguage.Go
                    ? $"{ind}for {CondText(b.Text)} {{"
                    : $"{ind}while {CondText(b.Text)} {{");
                RenderBracelessSeq(sb, b.Body, inner, lang);
                sb.AppendLine($"{ind}}}");
                break;

            case Models.NsBlockKind.DoWhile:
                if (lang == ExportLanguage.Swift)
                {
                    sb.AppendLine($"{ind}repeat {{");
                    RenderBracelessSeq(sb, b.Body, inner, lang);
                    sb.AppendLine($"{ind}}} while {CondText(b.Text)}");
                }
                else // Kotlin: do { } while(...);  Go: for { ...; if !cond { break } }
                {
                    sb.AppendLine($"{ind}do {{");
                    RenderBracelessSeq(sb, b.Body, inner, lang);
                    sb.AppendLine($"{ind}}} while ({CondText(b.Text)})");
                }
                break;

            case Models.NsBlockKind.Case:
                // Kotlin uses `when`; Go and Swift use `switch` (no break needed).
                sb.AppendLine(lang == ExportLanguage.Kotlin
                    ? $"{ind}when ({CondText(b.Text)}) {{"
                    : $"{ind}switch {CondText(b.Text)} {{");
                foreach (var arm in b.Arms)
                {
                    if (lang == ExportLanguage.Kotlin)
                    {
                        sb.AppendLine($"{inner}{arm.Label} -> {{");
                        RenderBracelessSeq(sb, arm.Body, inner + "    ", lang);
                        sb.AppendLine($"{inner}}}");
                    }
                    else
                    {
                        sb.AppendLine($"{inner}case {arm.Label}:");
                        RenderBracelessSeq(sb, arm.Body, inner + "    ", lang);
                    }
                }
                sb.AppendLine($"{ind}}}");
                break;
        }
    }

    // ── Python body (indentation, colon blocks) ──

    private static bool TryPythonBody(string key, StringBuilder sb, string ind)
    {
        if (_projForBodies is null || !StructogramService.Exists(_projForBodies, key)) return false;
        var sd = StructogramService.Load(_projForBodies, key);
        if (sd.Root.Count == 0) return false;
        RenderPythonSeq(sb, sd.Root, ind);
        return true;
    }

    private static void RenderPythonSeq(StringBuilder sb, List<Models.NsBlock> blocks, string ind)
    {
        foreach (var b in blocks) RenderPythonBlock(sb, b, ind);
    }

    private static void RenderPythonBlock(StringBuilder sb, Models.NsBlock b, string ind)
    {
        var inner = ind + "    ";
        switch (b.Kind)
        {
            case Models.NsBlockKind.Statement:
                var s = (b.Text ?? "").Trim();
                if (s.Length > 0) sb.AppendLine($"{ind}{s}");
                break;

            case Models.NsBlockKind.If:
                sb.AppendLine($"{ind}if {CondText(b.Text)}:");
                RenderPythonSeqOrPass(sb, b.Body, inner);
                if (b.Else.Count > 0)
                {
                    sb.AppendLine($"{ind}else:");
                    RenderPythonSeqOrPass(sb, b.Else, inner);
                }
                break;

            case Models.NsBlockKind.While:
                sb.AppendLine($"{ind}while {CondText(b.Text)}:");
                RenderPythonSeqOrPass(sb, b.Body, inner);
                break;

            case Models.NsBlockKind.DoWhile:   // Python has no do-while → emulate
                sb.AppendLine($"{ind}while True:");
                RenderPythonSeqOrPass(sb, b.Body, inner);
                sb.AppendLine($"{inner}if not ({CondText(b.Text)}):");
                sb.AppendLine($"{inner}    break");
                break;

            case Models.NsBlockKind.Case:
                sb.AppendLine($"{ind}match {CondText(b.Text)}:");
                foreach (var arm in b.Arms)
                {
                    sb.AppendLine($"{inner}case {arm.Label}:");
                    RenderPythonSeqOrPass(sb, arm.Body, inner + "    ");
                }
                break;
        }
    }

    private static void RenderPythonSeqOrPass(StringBuilder sb, List<Models.NsBlock> blocks, string ind)
    {
        if (blocks.Count == 0) { sb.AppendLine($"{ind}pass"); return; }
        RenderPythonSeq(sb, blocks, ind);
    }

    // ── Rust body (match, no forced semicolons) ──

    private static bool TryRustBody(string key, StringBuilder sb, string ind)
    {
        if (_projForBodies is null || !StructogramService.Exists(_projForBodies, key)) return false;
        var sd = StructogramService.Load(_projForBodies, key);
        if (sd.Root.Count == 0) return false;
        RenderRustSeq(sb, sd.Root, ind);
        return true;
    }

    private static void RenderRustSeq(StringBuilder sb, List<Models.NsBlock> blocks, string ind)
    {
        foreach (var b in blocks) RenderRustBlock(sb, b, ind);
    }

    private static void RenderRustBlock(StringBuilder sb, Models.NsBlock b, string ind)
    {
        var inner = ind + "    ";
        switch (b.Kind)
        {
            case Models.NsBlockKind.Statement:
                var s = StTerm(b.Text);
                if (s.Length > 0) sb.AppendLine($"{ind}{s}");
                break;

            case Models.NsBlockKind.If:
                sb.AppendLine($"{ind}if {CondText(b.Text)} {{");
                RenderRustSeq(sb, b.Body, inner);
                if (b.Else.Count > 0)
                {
                    sb.AppendLine($"{ind}}} else {{");
                    RenderRustSeq(sb, b.Else, inner);
                }
                sb.AppendLine($"{ind}}}");
                break;

            case Models.NsBlockKind.While:
                sb.AppendLine($"{ind}while {CondText(b.Text)} {{");
                RenderRustSeq(sb, b.Body, inner);
                sb.AppendLine($"{ind}}}");
                break;

            case Models.NsBlockKind.DoWhile:   // Rust has no do-while → loop + break
                sb.AppendLine($"{ind}loop {{");
                RenderRustSeq(sb, b.Body, inner);
                sb.AppendLine($"{inner}if !({CondText(b.Text)}) {{ break; }}");
                sb.AppendLine($"{ind}}}");
                break;

            case Models.NsBlockKind.Case:
                sb.AppendLine($"{ind}match {CondText(b.Text)} {{");
                foreach (var arm in b.Arms)
                {
                    sb.AppendLine($"{inner}{arm.Label} => {{");
                    RenderRustSeq(sb, arm.Body, inner + "    ");
                    sb.AppendLine($"{inner}}}");
                }
                sb.AppendLine($"{ind}}}");
                break;
        }
    }

    // ── C# ──────────────────────────────────────────────────────────────────

    private static void EmitCSharp(StringBuilder sb, CodeEntity e, string ind, Func<string, string> name)
    {
        string V(CodeVisibility v) => v.ToString().ToLowerInvariant();
        string Conv(PassingConvention c) => c is PassingConvention.Reference or PassingConvention.Pointer ? "ref " : "";
        var inner = ind + "    ";

        switch (e.EntityType)
        {
            case CodeEntityType.Enum:
                sb.AppendLine($"{ind}public enum {e.Name} {{ {string.Join(", ", e.EnumValues)} }}");
                break;
            case CodeEntityType.Function:
            {
                var (ins, ret) = FuncSig(e);
                var ps = string.Join(", ", ins.Select(p => $"{Conv(p.Convention)}{p.DataType} {p.Name}"));
                sb.AppendLine($"{ind}public static {ret} {e.Name}({ps})");
                sb.AppendLine($"{ind}{{");
                if (!TryBracedBody(e.Id, sb, ind + "    ") && ret.Trim() is not ("void" or ""))
                    sb.AppendLine($"{ind}    throw new System.NotImplementedException();");
                sb.AppendLine($"{ind}}}");
                break;
            }
            case CodeEntityType.Object:
                sb.AppendLine($"{ind}// instance: {(string.IsNullOrEmpty(e.InstanceOfId) ? "var" : name(e.InstanceOfId))} {e.Name} = new {(string.IsNullOrEmpty(e.InstanceOfId) ? "/* type */" : name(e.InstanceOfId))}();");
                break;
            default:
            {
                bool iface = e.EntityType == CodeEntityType.Interface;
                string kw = iface ? "interface" : e.EntityType == CodeEntityType.Struct ? "struct" : "class";
                var bases = Bases(e, name);
                var head = $"{ind}public {kw} {e.Name}" + (bases.Count > 0 ? " : " + string.Join(", ", bases) : "");
                sb.AppendLine(head);
                sb.AppendLine($"{ind}{{");
                if (!iface)
                    foreach (var f in e.Fields)
                        sb.AppendLine($"{inner}{V(f.Visibility)} {(f.IsStatic ? "static " : "")}{f.DataType} {f.Name}{(string.IsNullOrWhiteSpace(f.DefaultValue) ? "" : $" = {f.DefaultValue}")};");
                foreach (var m in e.Methods)
                {
                    var ps = string.Join(", ", m.Parameters.Select(p => $"{Conv(p.Convention)}{p.DataType} {p.Name}"));
                    if (!iface && m.Kind == MethodKind.Constructor)
                    {
                        sb.AppendLine($"{inner}{V(m.Visibility)} {e.Name}({ps})");
                        sb.AppendLine($"{inner}{{");
                        TryBracedBody($"{e.Id}#{m.Id}", sb, inner + "    ");
                        sb.AppendLine($"{inner}}}");
                        continue;
                    }
                    if (!iface && m.Kind == MethodKind.Destructor)
                    {
                        sb.AppendLine($"{inner}~{e.Name}()");
                        sb.AppendLine($"{inner}{{");
                        TryBracedBody($"{e.Id}#{m.Id}", sb, inner + "    ");
                        sb.AppendLine($"{inner}}}");
                        continue;
                    }
                    if (iface) sb.AppendLine($"{inner}{m.ReturnType} {m.Name}({ps});");
                    else
                    {
                        sb.AppendLine($"{inner}{V(m.Visibility)} {(m.IsStatic ? "static " : "")}{m.ReturnType} {m.Name}({ps})");
                        sb.AppendLine($"{inner}{{");
                        if (!TryBracedBody($"{e.Id}#{m.Id}", sb, inner + "    ") && m.ReturnType.Trim() is not ("void" or ""))
                            sb.AppendLine($"{inner}    throw new System.NotImplementedException();");
                        sb.AppendLine($"{inner}}}");
                    }
                }
                sb.AppendLine($"{ind}}}");
                break;
            }
        }
    }

    // ── C++ ─────────────────────────────────────────────────────────────────

    private static void EmitCpp(StringBuilder sb, CodeEntity e, string ind, Func<string, string> name)
    {
        string Conv(PassingConvention c) => c switch { PassingConvention.Reference => "&", PassingConvention.Pointer => "*", _ => "" };
        var inner = ind + "    ";
        switch (e.EntityType)
        {
            case CodeEntityType.Enum:
                sb.AppendLine($"{ind}enum class {e.Name} {{ {string.Join(", ", e.EnumValues)} }};");
                break;
            case CodeEntityType.Function:
            {
                var (ins, ret) = FuncSig(e);
                var ps = string.Join(", ", ins.Select(p => $"{p.DataType}{Conv(p.Convention)} {p.Name}"));
                sb.AppendLine($"{ind}{ret} {e.Name}({ps}) {{");
                sb.AppendLine($"{ind}}}");
                break;
            }
            case CodeEntityType.Object:
                sb.AppendLine($"{ind}// instance: {(string.IsNullOrEmpty(e.InstanceOfId) ? "/* type */" : name(e.InstanceOfId))} {e.Name};");
                break;
            default:
            {
                bool iface = e.EntityType == CodeEntityType.Interface;
                string kw = e.EntityType == CodeEntityType.Struct ? "struct" : "class";
                var bases = Bases(e, name);
                var head = $"{ind}{kw} {e.Name}" + (bases.Count > 0 ? " : " + string.Join(", ", bases.Select(b => "public " + b)) : "");
                sb.AppendLine(head + " {");
                foreach (var vis in new[] { CodeVisibility.Public, CodeVisibility.Protected, CodeVisibility.Private })
                {
                    var fields  = iface ? new List<CodeField>() : e.Fields.Where(f => f.Visibility == vis).ToList();
                    var methods = e.Methods.Where(m => m.Visibility == vis).ToList();
                    if (fields.Count == 0 && methods.Count == 0) continue;
                    sb.AppendLine($"{ind}{vis.ToString().ToLowerInvariant()}:");
                    foreach (var f in fields)
                        sb.AppendLine($"{inner}{(f.IsStatic ? "static " : "")}{f.DataType} {f.Name};");
                    foreach (var m in methods)
                    {
                        var ps = string.Join(", ", m.Parameters.Select(p => $"{p.DataType}{Conv(p.Convention)} {p.Name}"));
                        if (m.Kind == MethodKind.Constructor) { sb.AppendLine($"{inner}{e.Name}({ps});"); continue; }
                        if (m.Kind == MethodKind.Destructor)  { sb.AppendLine($"{inner}{(bases.Count > 0 ? "virtual " : "")}~{e.Name}();"); continue; }
                        sb.AppendLine($"{inner}{(iface ? "virtual " : "")}{(m.IsStatic ? "static " : "")}{m.ReturnType} {m.Name}({ps}){(iface ? " = 0;" : ";")}");
                    }
                }
                sb.AppendLine($"{ind}}};");

                // Out-of-line inline definitions for members that have a structogram body.
                if (!iface && _projForBodies is not null)
                {
                    foreach (var m in e.Methods)
                    {
                        var key = $"{e.Id}#{m.Id}";
                        if (!StructogramService.Exists(_projForBodies, key)) continue;
                        var ps = string.Join(", ", m.Parameters.Select(p => $"{p.DataType}{Conv(p.Convention)} {p.Name}"));
                        string sig = m.Kind switch
                        {
                            MethodKind.Constructor => $"{ind}inline {e.Name}::{e.Name}({ps}) {{",
                            MethodKind.Destructor  => $"{ind}inline {e.Name}::~{e.Name}() {{",
                            _                      => $"{ind}inline {m.ReturnType} {e.Name}::{m.Name}({ps}) {{"
                        };
                        sb.AppendLine();
                        sb.AppendLine(sig);
                        TryBracedBody(key, sb, ind + "    ");
                        sb.AppendLine($"{ind}}}");
                    }
                }
                break;
            }
        }
    }

    // ── Java ────────────────────────────────────────────────────────────────

    private static void EmitJava(StringBuilder sb, CodeEntity e, string ind, Func<string, string> name)
    {
        string V(CodeVisibility v) => v == CodeVisibility.Internal ? "" : v.ToString().ToLowerInvariant() + " ";
        var inner = ind + "    ";
        switch (e.EntityType)
        {
            case CodeEntityType.Enum:
                sb.AppendLine($"{ind}public enum {e.Name} {{ {string.Join(", ", e.EnumValues)} }}");
                break;
            case CodeEntityType.Function:
            {
                var (ins, ret) = FuncSig(e);
                var ps = string.Join(", ", ins.Select(p => $"{p.DataType} {p.Name}"));
                sb.AppendLine($"{ind}public static {ret} {e.Name}({ps}) {{");
                if (!TryBracedBody(e.Id, sb, ind + "    ") && ret.Trim() is not ("void" or ""))
                    sb.AppendLine($"{ind}    throw new UnsupportedOperationException();");
                sb.AppendLine($"{ind}}}");
                break;
            }
            case CodeEntityType.Object:
                sb.AppendLine($"{ind}// instance: {(string.IsNullOrEmpty(e.InstanceOfId) ? "var" : name(e.InstanceOfId))} {e.Name} = new {(string.IsNullOrEmpty(e.InstanceOfId) ? "/* type */" : name(e.InstanceOfId))}();");
                break;
            default:
            {
                bool iface = e.EntityType == CodeEntityType.Interface;
                string kw = iface ? "interface" : "class";
                var ext  = string.IsNullOrEmpty(e.BaseClassId) ? "" : " extends " + name(e.BaseClassId);
                var impl = e.ImplementsIds.Select(name).Where(s => !string.IsNullOrEmpty(s)).ToList();
                var implS = impl.Count > 0 ? " implements " + string.Join(", ", impl) : "";
                sb.AppendLine($"{ind}public {kw} {e.Name}{ext}{implS} {{");
                if (!iface)
                    foreach (var f in e.Fields)
                        sb.AppendLine($"{inner}{V(f.Visibility)}{(f.IsStatic ? "static " : "")}{f.DataType} {f.Name};");
                foreach (var m in e.Methods)
                {
                    var ps = string.Join(", ", m.Parameters.Select(p => $"{p.DataType} {p.Name}"));
                    if (!iface && m.Kind == MethodKind.Constructor)
                    {
                        sb.AppendLine($"{inner}{V(m.Visibility)}{e.Name}({ps}) {{");
                        TryBracedBody($"{e.Id}#{m.Id}", sb, inner + "    ");
                        sb.AppendLine($"{inner}}}");
                        continue;
                    }
                    if (!iface && m.Kind == MethodKind.Destructor)  { sb.AppendLine($"{inner}// destructor — Java has none; consider AutoCloseable.close()"); continue; }
                    if (iface) sb.AppendLine($"{inner}{m.ReturnType} {m.Name}({ps});");
                    else
                    {
                        sb.AppendLine($"{inner}{V(m.Visibility)}{(m.IsStatic ? "static " : "")}{m.ReturnType} {m.Name}({ps}) {{");
                        if (!TryBracedBody($"{e.Id}#{m.Id}", sb, inner + "    ") && m.ReturnType.Trim() is not ("void" or ""))
                            sb.AppendLine($"{inner}    throw new UnsupportedOperationException();");
                        sb.AppendLine($"{inner}}}");
                    }
                }
                sb.AppendLine($"{ind}}}");
                break;
            }
        }
    }

    // ── TypeScript ────────────────────────────────────────────────────────────

    private static void EmitTypeScript(StringBuilder sb, CodeEntity e, string ind, Func<string, string> name)
    {
        string V(CodeVisibility v) => v switch { CodeVisibility.Private => "private ", CodeVisibility.Protected => "protected ", _ => "" };
        var inner = ind + "    ";
        switch (e.EntityType)
        {
            case CodeEntityType.Enum:
                sb.AppendLine($"{ind}export enum {e.Name} {{ {string.Join(", ", e.EnumValues)} }}");
                break;
            case CodeEntityType.Function:
            {
                var (ins, ret) = FuncSig(e);
                var ps = string.Join(", ", ins.Select(p => $"{p.Name}: {p.DataType}"));
                sb.AppendLine($"{ind}export function {e.Name}({ps}): {ret} {{");
                if (!TryBracedBody(e.Id, sb, ind + "    ") && ret.Trim() is not ("void" or ""))
                    sb.AppendLine($"{ind}    throw new Error(\"Not implemented\");");
                sb.AppendLine($"{ind}}}");
                break;
            }
            case CodeEntityType.Object:
                sb.AppendLine($"{ind}// instance: const {e.Name} = new {(string.IsNullOrEmpty(e.InstanceOfId) ? "/* type */" : name(e.InstanceOfId))}();");
                break;
            default:
            {
                bool iface = e.EntityType == CodeEntityType.Interface;
                string kw = iface ? "interface" : "class";
                var ext  = string.IsNullOrEmpty(e.BaseClassId) ? "" : " extends " + name(e.BaseClassId);
                var impl = e.ImplementsIds.Select(name).Where(s => !string.IsNullOrEmpty(s)).ToList();
                var implS = impl.Count > 0 ? " implements " + string.Join(", ", impl) : "";
                sb.AppendLine($"{ind}export {kw} {e.Name}{ext}{implS} {{");
                if (!iface)
                    foreach (var f in e.Fields)
                        sb.AppendLine($"{inner}{V(f.Visibility)}{(f.IsStatic ? "static " : "")}{f.Name}: {f.DataType};");
                foreach (var m in e.Methods)
                {
                    var ps = string.Join(", ", m.Parameters.Select(p => $"{p.Name}: {p.DataType}"));
                    if (!iface && m.Kind == MethodKind.Constructor)
                    {
                        sb.AppendLine($"{inner}constructor({ps}) {{");
                        TryBracedBody($"{e.Id}#{m.Id}", sb, inner + "    ");
                        sb.AppendLine($"{inner}}}");
                        continue;
                    }
                    if (!iface && m.Kind == MethodKind.Destructor)  { sb.AppendLine($"{inner}// destructor — no TypeScript equivalent (consider dispose())"); continue; }
                    if (iface) sb.AppendLine($"{inner}{m.Name}({ps}): {m.ReturnType};");
                    else
                    {
                        sb.AppendLine($"{inner}{V(m.Visibility)}{(m.IsStatic ? "static " : "")}{m.Name}({ps}): {m.ReturnType} {{");
                        if (!TryBracedBody($"{e.Id}#{m.Id}", sb, inner + "    ") && m.ReturnType.Trim() is not ("void" or ""))
                            sb.AppendLine($"{inner}    throw new Error(\"Not implemented\");");
                        sb.AppendLine($"{inner}}}");
                    }
                }
                sb.AppendLine($"{ind}}}");
                break;
            }
        }
    }

    // ── Python ────────────────────────────────────────────────────────────────

    private static string PythonImports(List<CodeEntity> all)
    {
        var lines = new List<string>();
        if (all.Any(e => e.EntityType == CodeEntityType.Enum)) lines.Add("from enum import Enum");
        if (all.Any(e => e.EntityType == CodeEntityType.Interface)) lines.Add("from abc import ABC, abstractmethod");
        if (all.Any(e => e.EntityType is CodeEntityType.Class or CodeEntityType.Struct && e.Fields.Count > 0))
            lines.Add("from dataclasses import dataclass");
        return string.Join("\n", lines);
    }

    private static void EmitPython(StringBuilder sb, CodeEntity e, string ind, Func<string, string> name)
    {
        string PyName(string n, CodeVisibility v) => v switch { CodeVisibility.Private => "__" + n, CodeVisibility.Protected => "_" + n, _ => n };
        var inner = ind + "    ";
        switch (e.EntityType)
        {
            case CodeEntityType.Enum:
                sb.AppendLine($"{ind}class {e.Name}(Enum):");
                if (e.EnumValues.Count == 0) sb.AppendLine($"{inner}pass");
                else { int i = 1; foreach (var v in e.EnumValues) sb.AppendLine($"{inner}{v} = {i++}"); }
                break;
            case CodeEntityType.Function:
            {
                var (ins, ret) = FuncSig(e);
                var ps = string.Join(", ", ins.Select(p => $"{p.Name}: {p.DataType}"));
                sb.AppendLine($"{ind}def {e.Name}({ps}) -> {(ret == "void" ? "None" : ret)}:");
                if (!TryPythonBody(e.Id, sb, inner)) sb.AppendLine($"{inner}raise NotImplementedError");
                break;
            }
            case CodeEntityType.Object:
                sb.AppendLine($"{ind}# instance: {e.Name} = {(string.IsNullOrEmpty(e.InstanceOfId) ? "...  # type" : name(e.InstanceOfId) + "()")}");
                break;
            default:
            {
                bool iface = e.EntityType == CodeEntityType.Interface;
                var parents = new List<string>();
                if (!string.IsNullOrEmpty(e.BaseClassId)) parents.Add(name(e.BaseClassId));
                parents.AddRange(e.ImplementsIds.Select(name).Where(s => !string.IsNullOrEmpty(s)));
                if (iface) parents.Insert(0, "ABC");
                var pStr = parents.Count > 0 ? $"({string.Join(", ", parents)})" : "";
                if (!iface && e.Fields.Count > 0) sb.AppendLine($"{ind}@dataclass");
                sb.AppendLine($"{ind}class {e.Name}{pStr}:");

                bool any = false;
                if (!iface)
                    foreach (var f in e.Fields)
                    {
                        sb.AppendLine($"{inner}{PyName(f.Name, f.Visibility)}: {f.DataType}{(string.IsNullOrWhiteSpace(f.DefaultValue) ? "" : $" = {f.DefaultValue}")}");
                        any = true;
                    }
                foreach (var m in e.Methods)
                {
                    var ps = string.Join(", ", new[] { "self" }.Concat(m.Parameters.Select(p => $"{p.Name}: {p.DataType}")));
                    if (!iface && m.Kind is MethodKind.Constructor or MethodKind.Destructor)
                    {
                        var dunder = m.Kind == MethodKind.Constructor ? "__init__" : "__del__";
                        sb.AppendLine($"{inner}def {dunder}({ps}) -> None:");
                        if (!TryPythonBody($"{e.Id}#{m.Id}", sb, inner + "    ")) sb.AppendLine($"{inner}    pass");
                        any = true;
                        continue;
                    }
                    if (iface) sb.AppendLine($"{inner}@abstractmethod");
                    sb.AppendLine($"{inner}def {PyName(m.Name, m.Visibility)}({ps}) -> {(m.ReturnType == "void" ? "None" : m.ReturnType)}:");
                    if (iface) sb.AppendLine($"{inner}    ...");
                    else if (!TryPythonBody($"{e.Id}#{m.Id}", sb, inner + "    ")) sb.AppendLine($"{inner}    raise NotImplementedError");
                    any = true;
                }
                if (!any) sb.AppendLine($"{inner}pass");
                break;
            }
        }
    }

    // ── Kotlin ──────────────────────────────────────────────────────────────

    private static void EmitKotlin(StringBuilder sb, CodeEntity e, string ind, Func<string, string> name)
    {
        string V(CodeVisibility v) => v switch { CodeVisibility.Private => "private ", CodeVisibility.Protected => "protected ", CodeVisibility.Internal => "internal ", _ => "" };
        string Ret(string r) => r.Trim() is "void" or "" ? "" : ": " + r;
        var inner = ind + "    ";
        switch (e.EntityType)
        {
            case CodeEntityType.Enum:
                sb.AppendLine($"{ind}enum class {e.Name} {{ {string.Join(", ", e.EnumValues)} }}");
                break;
            case CodeEntityType.Function:
            {
                var (ins, ret) = FuncSig(e);
                var ps = string.Join(", ", ins.Select(p => $"{p.Name}: {p.DataType}"));
                sb.AppendLine($"{ind}fun {e.Name}({ps}){Ret(ret)} {{");
                if (!TryBracelessBody(e.Id, sb, ind + "    ", ExportLanguage.Kotlin)) sb.AppendLine($"{ind}    TODO(\"Not implemented\")");
                sb.AppendLine($"{ind}}}");
                break;
            }
            case CodeEntityType.Object:
                sb.AppendLine($"{ind}// instance: val {e.Name} = {(string.IsNullOrEmpty(e.InstanceOfId) ? "/* type */()" : name(e.InstanceOfId) + "()")}");
                break;
            default:
            {
                bool iface = e.EntityType == CodeEntityType.Interface;
                string kw = iface ? "interface" : "open class";
                var bases = new List<string>();
                if (!string.IsNullOrEmpty(e.BaseClassId)) bases.Add(name(e.BaseClassId) + "()");
                bases.AddRange(e.ImplementsIds.Select(name).Where(s => !string.IsNullOrEmpty(s)));
                var bStr = bases.Count > 0 ? " : " + string.Join(", ", bases) : "";
                sb.AppendLine($"{ind}{kw} {e.Name}{bStr} {{");
                if (!iface)
                    foreach (var f in e.Fields)
                        sb.AppendLine($"{inner}{V(f.Visibility)}var {f.Name}: {f.DataType}{(string.IsNullOrWhiteSpace(f.DefaultValue) ? "" : $" = {f.DefaultValue}")}");
                foreach (var m in e.Methods)
                {
                    var ps = string.Join(", ", m.Parameters.Select(p => $"{p.Name}: {p.DataType}"));
                    if (!iface && m.Kind == MethodKind.Constructor)
                    {
                        sb.AppendLine($"{inner}constructor({ps}) {{");
                        TryBracelessBody($"{e.Id}#{m.Id}", sb, inner + "    ", ExportLanguage.Kotlin);
                        sb.AppendLine($"{inner}}}");
                        continue;
                    }
                    if (!iface && m.Kind == MethodKind.Destructor)  { sb.AppendLine($"{inner}// destructor — no Kotlin equivalent (consider AutoCloseable.close())"); continue; }
                    if (iface) sb.AppendLine($"{inner}fun {m.Name}({ps}){Ret(m.ReturnType)}");
                    else
                    {
                        sb.AppendLine($"{inner}{V(m.Visibility)}fun {m.Name}({ps}){Ret(m.ReturnType)} {{");
                        if (!TryBracelessBody($"{e.Id}#{m.Id}", sb, inner + "    ", ExportLanguage.Kotlin)) sb.AppendLine($"{inner}    TODO(\"Not implemented\")");
                        sb.AppendLine($"{inner}}}");
                    }
                }
                sb.AppendLine($"{ind}}}");
                break;
            }
        }
    }

    // ── Swift ───────────────────────────────────────────────────────────────

    private static void EmitSwift(StringBuilder sb, CodeEntity e, string ind, Func<string, string> name)
    {
        string V(CodeVisibility v) => v switch { CodeVisibility.Private => "private ", CodeVisibility.Protected => "fileprivate ", CodeVisibility.Public => "public ", _ => "" };
        string Ret(string r) => r.Trim() is "void" or "" ? "" : " -> " + r;
        var inner = ind + "    ";
        switch (e.EntityType)
        {
            case CodeEntityType.Enum:
                sb.AppendLine($"{ind}enum {e.Name} {{");
                foreach (var v in e.EnumValues) sb.AppendLine($"{inner}case {v}");
                sb.AppendLine($"{ind}}}");
                break;
            case CodeEntityType.Function:
            {
                var (ins, ret) = FuncSig(e);
                var ps = string.Join(", ", ins.Select(p => $"{p.Name}: {p.DataType}"));
                sb.AppendLine($"{ind}func {e.Name}({ps}){Ret(ret)} {{");
                if (!TryBracelessBody(e.Id, sb, ind + "    ", ExportLanguage.Swift)) sb.AppendLine($"{ind}    fatalError(\"Not implemented\")");
                sb.AppendLine($"{ind}}}");
                break;
            }
            case CodeEntityType.Object:
                sb.AppendLine($"{ind}// instance: let {e.Name} = {(string.IsNullOrEmpty(e.InstanceOfId) ? "/* type */()" : name(e.InstanceOfId) + "()")}");
                break;
            default:
            {
                bool iface = e.EntityType == CodeEntityType.Interface;
                string kw = iface ? "protocol" : e.EntityType == CodeEntityType.Struct ? "struct" : "class";
                var bases = new List<string>();
                if (!string.IsNullOrEmpty(e.BaseClassId)) bases.Add(name(e.BaseClassId));
                bases.AddRange(e.ImplementsIds.Select(name).Where(s => !string.IsNullOrEmpty(s)));
                var bStr = bases.Count > 0 ? ": " + string.Join(", ", bases) : "";
                sb.AppendLine($"{ind}{kw} {e.Name}{bStr} {{");
                if (!iface)
                    foreach (var f in e.Fields)
                        sb.AppendLine($"{inner}{V(f.Visibility)}{(f.IsStatic ? "static " : "")}var {f.Name}: {f.DataType}");
                foreach (var m in e.Methods)
                {
                    var ps = string.Join(", ", m.Parameters.Select(p => $"{p.Name}: {p.DataType}"));
                    if (!iface && m.Kind == MethodKind.Constructor)
                    {
                        sb.AppendLine($"{inner}init({ps}) {{");
                        TryBracelessBody($"{e.Id}#{m.Id}", sb, inner + "    ", ExportLanguage.Swift);
                        sb.AppendLine($"{inner}}}");
                        continue;
                    }
                    if (!iface && m.Kind == MethodKind.Destructor)
                    {
                        sb.AppendLine($"{inner}deinit {{");
                        TryBracelessBody($"{e.Id}#{m.Id}", sb, inner + "    ", ExportLanguage.Swift);
                        sb.AppendLine($"{inner}}}");
                        continue;
                    }
                    if (iface) sb.AppendLine($"{inner}func {m.Name}({ps}){Ret(m.ReturnType)}");
                    else
                    {
                        sb.AppendLine($"{inner}{V(m.Visibility)}{(m.IsStatic ? "static " : "")}func {m.Name}({ps}){Ret(m.ReturnType)} {{");
                        if (!TryBracelessBody($"{e.Id}#{m.Id}", sb, inner + "    ", ExportLanguage.Swift)) sb.AppendLine($"{inner}    fatalError(\"Not implemented\")");
                        sb.AppendLine($"{inner}}}");
                    }
                }
                sb.AppendLine($"{ind}}}");
                break;
            }
        }
    }

    // ── PHP ─────────────────────────────────────────────────────────────────

    private static void EmitPhp(StringBuilder sb, CodeEntity e, string ind, Func<string, string> name)
    {
        string V(CodeVisibility v) => v switch { CodeVisibility.Private => "private", CodeVisibility.Protected => "protected", _ => "public" };
        var inner = ind + "    ";
        switch (e.EntityType)
        {
            case CodeEntityType.Enum:
                sb.AppendLine($"{ind}enum {e.Name} {{");
                foreach (var v in e.EnumValues) sb.AppendLine($"{inner}case {v};");
                sb.AppendLine($"{ind}}}");
                break;
            case CodeEntityType.Function:
            {
                var (ins, ret) = FuncSig(e);
                var ps = string.Join(", ", ins.Select(p => $"{p.DataType} ${p.Name}"));
                sb.AppendLine($"{ind}function {e.Name}({ps}): {ret} {{");
                if (!TryBracedBody(e.Id, sb, ind + "    ") && ret.Trim() is not ("void" or ""))
                    sb.AppendLine($"{ind}    throw new \\Exception(\"Not implemented\");");
                sb.AppendLine($"{ind}}}");
                break;
            }
            case CodeEntityType.Object:
                sb.AppendLine($"{ind}// instance: ${e.Name} = new {(string.IsNullOrEmpty(e.InstanceOfId) ? "/* type */" : name(e.InstanceOfId))}();");
                break;
            default:
            {
                bool iface = e.EntityType == CodeEntityType.Interface;
                string kw = iface ? "interface" : "class";
                var ext  = string.IsNullOrEmpty(e.BaseClassId) ? "" : " extends " + name(e.BaseClassId);
                var impl = e.ImplementsIds.Select(name).Where(s => !string.IsNullOrEmpty(s)).ToList();
                var implS = impl.Count > 0 ? " implements " + string.Join(", ", impl) : "";
                sb.AppendLine($"{ind}{kw} {e.Name}{ext}{implS} {{");
                if (!iface)
                    foreach (var f in e.Fields)
                        sb.AppendLine($"{inner}{V(f.Visibility)} {(f.IsStatic ? "static " : "")}{f.DataType} ${f.Name};");
                foreach (var m in e.Methods)
                {
                    var ps = string.Join(", ", m.Parameters.Select(p => $"{p.DataType} ${p.Name}"));
                    if (!iface && m.Kind == MethodKind.Constructor)
                    {
                        sb.AppendLine($"{inner}{V(m.Visibility)} function __construct({ps}) {{");
                        TryBracedBody($"{e.Id}#{m.Id}", sb, inner + "    ");
                        sb.AppendLine($"{inner}}}");
                        continue;
                    }
                    if (!iface && m.Kind == MethodKind.Destructor)
                    {
                        sb.AppendLine($"{inner}{V(m.Visibility)} function __destruct() {{");
                        TryBracedBody($"{e.Id}#{m.Id}", sb, inner + "    ");
                        sb.AppendLine($"{inner}}}");
                        continue;
                    }
                    if (iface) sb.AppendLine($"{inner}public function {m.Name}({ps}): {m.ReturnType};");
                    else
                    {
                        sb.AppendLine($"{inner}{V(m.Visibility)} {(m.IsStatic ? "static " : "")}function {m.Name}({ps}): {m.ReturnType} {{");
                        if (!TryBracedBody($"{e.Id}#{m.Id}", sb, inner + "    ") && m.ReturnType.Trim() is not ("void" or ""))
                            sb.AppendLine($"{inner}    throw new \\Exception(\"Not implemented\");");
                        sb.AppendLine($"{inner}}}");
                    }
                }
                sb.AppendLine($"{ind}}}");
                break;
            }
        }
    }

    // ── Go (no inheritance → embedding) ──────────────────────────────────────

    private static void EmitGo(StringBuilder sb, CodeEntity e, string ind, Func<string, string> name)
    {
        string Conv(PassingConvention c) => c switch { PassingConvention.Reference or PassingConvention.Pointer => "*", _ => "" };
        var inner = ind + "    ";
        switch (e.EntityType)
        {
            case CodeEntityType.Enum:
                sb.AppendLine($"{ind}type {e.Name} int");
                sb.AppendLine($"{ind}const (");
                bool first = true;
                foreach (var v in e.EnumValues) { sb.AppendLine($"{inner}{v}{(first ? $" {e.Name} = iota" : "")}"); first = false; }
                sb.AppendLine($"{ind})");
                break;
            case CodeEntityType.Function:
            {
                var (ins, ret) = FuncSig(e);
                var ps = string.Join(", ", ins.Select(p => $"{p.Name} {Conv(p.Convention)}{p.DataType}"));
                var r  = ret.Trim() is "void" or "" ? "" : " " + ret;
                sb.AppendLine($"{ind}func {e.Name}({ps}){r} {{");
                if (!TryBracelessBody(e.Id, sb, ind + "    ", ExportLanguage.Go)) sb.AppendLine($"{ind}    panic(\"not implemented\")");
                sb.AppendLine($"{ind}}}");
                break;
            }
            case CodeEntityType.Object:
                sb.AppendLine($"{ind}// instance: {e.Name} := {(string.IsNullOrEmpty(e.InstanceOfId) ? "/* type */{}" : name(e.InstanceOfId) + "{}")}");
                break;
            case CodeEntityType.Interface:
                sb.AppendLine($"{ind}type {e.Name} interface {{");
                foreach (var m in e.Methods)
                {
                    var ps = string.Join(", ", m.Parameters.Select(p => $"{p.Name} {p.DataType}"));
                    var r  = m.ReturnType.Trim() is "void" or "" ? "" : " " + m.ReturnType;
                    sb.AppendLine($"{inner}{m.Name}({ps}){r}");
                }
                sb.AppendLine($"{ind}}}");
                break;
            default: // Class / Struct → struct + methods; base class → embedded field
            {
                sb.AppendLine($"{ind}type {e.Name} struct {{");
                if (!string.IsNullOrEmpty(e.BaseClassId))
                    sb.AppendLine($"{inner}{name(e.BaseClassId)} // embedded (inheritance → composition)");
                foreach (var f in e.Fields)
                    sb.AppendLine($"{inner}{f.Name} {f.DataType}");
                sb.AppendLine($"{ind}}}");
                foreach (var m in e.Methods)
                {
                    var ps = string.Join(", ", m.Parameters.Select(p => $"{p.Name} {p.DataType}"));
                    if (m.Kind == MethodKind.Constructor)
                    {
                        sb.AppendLine($"{ind}func New{e.Name}({ps}) *{e.Name} {{");
                        TryBracelessBody($"{e.Id}#{m.Id}", sb, ind + "    ", ExportLanguage.Go);
                        sb.AppendLine($"{ind}    return &{e.Name}{{}}");
                        sb.AppendLine($"{ind}}}");
                        continue;
                    }
                    if (m.Kind == MethodKind.Destructor)
                    {
                        sb.AppendLine($"{ind}// destructor — Go has none; use a Close() method or runtime.SetFinalizer");
                        continue;
                    }
                    var r  = m.ReturnType.Trim() is "void" or "" ? "" : " " + m.ReturnType;
                    sb.AppendLine($"{ind}func (recv *{e.Name}) {m.Name}({ps}){r} {{");
                    if (!TryBracelessBody($"{e.Id}#{m.Id}", sb, ind + "    ", ExportLanguage.Go)) sb.AppendLine($"{ind}    panic(\"not implemented\")");
                    sb.AppendLine($"{ind}}}");
                }
                break;
            }
        }
    }

    // ── Rust (no inheritance → composition / traits) ─────────────────────────

    private static void EmitRust(StringBuilder sb, CodeEntity e, string ind, Func<string, string> name)
    {
        string Ret(string r) => r.Trim() is "void" or "" ? "" : " -> " + r;
        var inner = ind + "    ";
        switch (e.EntityType)
        {
            case CodeEntityType.Enum:
                sb.AppendLine($"{ind}pub enum {e.Name} {{ {string.Join(", ", e.EnumValues)} }}");
                break;
            case CodeEntityType.Function:
            {
                var (ins, ret) = FuncSig(e);
                var ps = string.Join(", ", ins.Select(p => $"{p.Name}: {p.DataType}"));
                sb.AppendLine($"{ind}pub fn {e.Name}({ps}){Ret(ret)} {{");
                if (!TryRustBody(e.Id, sb, ind + "    ")) sb.AppendLine($"{ind}    unimplemented!()");
                sb.AppendLine($"{ind}}}");
                break;
            }
            case CodeEntityType.Object:
                sb.AppendLine($"{ind}// instance: let {e.Name} = {(string.IsNullOrEmpty(e.InstanceOfId) ? "/* type */" : name(e.InstanceOfId))} {{ }};");
                break;
            case CodeEntityType.Interface:
                sb.AppendLine($"{ind}pub trait {e.Name} {{");
                foreach (var m in e.Methods)
                {
                    var ps = string.Join(", ", new[] { "&self" }.Concat(m.Parameters.Select(p => $"{p.Name}: {p.DataType}")));
                    sb.AppendLine($"{inner}fn {m.Name}({ps}){Ret(m.ReturnType)};");
                }
                sb.AppendLine($"{ind}}}");
                break;
            default: // Class / Struct → struct + impl; base → composition note
            {
                if (!string.IsNullOrEmpty(e.BaseClassId))
                    sb.AppendLine($"{ind}// note: Rust has no inheritance — base '{name(e.BaseClassId)}' modelled as composition");
                sb.AppendLine($"{ind}pub struct {e.Name} {{");
                if (!string.IsNullOrEmpty(e.BaseClassId))
                    sb.AppendLine($"{inner}base: {name(e.BaseClassId)},");
                foreach (var f in e.Fields)
                    sb.AppendLine($"{inner}{(f.Visibility == CodeVisibility.Public ? "pub " : "")}{f.Name}: {f.DataType},");
                sb.AppendLine($"{ind}}}");
                if (e.Methods.Count > 0)
                {
                    sb.AppendLine($"{ind}impl {e.Name} {{");
                    foreach (var m in e.Methods)
                    {
                        if (m.Kind == MethodKind.Constructor)
                        {
                            var cps = string.Join(", ", m.Parameters.Select(p => $"{p.Name}: {p.DataType}"));
                            sb.AppendLine($"{inner}pub fn new({cps}) -> Self {{");
                            if (!TryRustBody($"{e.Id}#{m.Id}", sb, inner + "    ")) sb.AppendLine($"{inner}    unimplemented!()");
                            sb.AppendLine($"{inner}}}");
                            continue;
                        }
                        if (m.Kind == MethodKind.Destructor)
                        {
                            sb.AppendLine($"{inner}// destructor — implement the Drop trait: impl Drop for {e.Name} {{ fn drop(&mut self) {{ }} }}");
                            continue;
                        }
                        var ps = string.Join(", ", new[] { "&self" }.Concat(m.Parameters.Select(p => $"{p.Name}: {p.DataType}")));
                        sb.AppendLine($"{inner}{(m.Visibility == CodeVisibility.Public ? "pub " : "")}fn {m.Name}({ps}){Ret(m.ReturnType)} {{");
                        if (!TryRustBody($"{e.Id}#{m.Id}", sb, inner + "    ")) sb.AppendLine($"{inner}    unimplemented!()");
                        sb.AppendLine($"{inner}}}");
                    }
                    sb.AppendLine($"{ind}}}");
                }
                break;
            }
        }
    }

    // ── Verse (Epic's functional-logic language; for Unreal / UEFN) ──────────

    private static void EmitVerse(StringBuilder sb, CodeEntity e, string ind, Func<string, string> name)
    {
        // Access specifiers sit after the identifier: Name<public>:type. Internal is the default (omitted).
        string Acc(CodeVisibility v) => v switch
        {
            CodeVisibility.Public    => "<public>",
            CodeVisibility.Private   => "<private>",
            CodeVisibility.Protected => "<protected>",
            _                        => "",          // Internal = default
        };
        // Verse has a void type; a present return type is written ":T".
        string Ret(string r) => r.Trim() is "void" or "" ? ":void" : ":" + r.Trim();
        var inner = ind + "    ";

        switch (e.EntityType)
        {
            case CodeEntityType.Enum:
                sb.AppendLine($"{ind}{e.Name} := enum:");
                if (e.EnumValues.Count == 0) sb.AppendLine($"{inner}# (no values)");
                else foreach (var v in e.EnumValues) sb.AppendLine($"{inner}{v}");
                break;

            case CodeEntityType.Function:
            {
                var (ins, ret) = FuncSig(e);
                var ps = string.Join(", ", ins.Select(p => $"{p.Name}:{p.DataType}"));
                sb.AppendLine($"{ind}{e.Name}({ps}){Ret(ret)} =");
                if (!TryVerseBody(e.Id, sb, inner)) sb.AppendLine($"{inner}# TODO: implement");
                break;
            }

            case CodeEntityType.Object:
                if (string.IsNullOrEmpty(e.InstanceOfId))
                    sb.AppendLine($"{ind}# instance {e.Name}: set its class via 'instance of'");
                else
                {
                    var t = name(e.InstanceOfId);
                    sb.AppendLine($"{ind}{e.Name}:{t} = {t}{{}}");
                }
                break;

            case CodeEntityType.Interface:
                sb.AppendLine($"{ind}{e.Name} := interface:");
                if (e.Methods.Count == 0) sb.AppendLine($"{inner}# (no methods)");
                foreach (var m in e.Methods)
                {
                    var ps = string.Join(", ", m.Parameters.Select(p => $"{p.Name}:{p.DataType}"));
                    sb.AppendLine($"{inner}{m.Name}({ps}){Ret(m.ReturnType)}");
                }
                break;

            default: // Class / Struct
            {
                bool strct = e.EntityType == CodeEntityType.Struct;
                var bases   = Bases(e, name);   // base class + interfaces
                // Structs are value types and don't inherit; only classes take a (base, iface…) list.
                var head = (!strct && bases.Count > 0)
                    ? $"{ind}{e.Name} := class({string.Join(", ", bases)}):"
                    : $"{ind}{e.Name} := {(strct ? "struct" : "class")}:";
                sb.AppendLine(head);

                bool any = false;
                foreach (var f in e.Fields)
                {
                    // Struct fields are immutable by default; class fields commonly change → 'var'.
                    var mut = strct ? "" : "var ";
                    var def = string.IsNullOrWhiteSpace(f.DefaultValue) ? "" : $" = {f.DefaultValue}";
                    sb.AppendLine($"{inner}{mut}{f.Name}{Acc(f.Visibility)}:{f.DataType}{def}");
                    any = true;
                }
                foreach (var m in e.Methods)
                {
                    if (m.Kind == MethodKind.Destructor)
                    {
                        sb.AppendLine($"{inner}# destructor — Verse is garbage-collected; no equivalent");
                        any = true; continue;
                    }
                    if (m.Kind == MethodKind.Constructor)
                    {
                        // Constructors are module-scope <constructor> functions in Verse, not class members.
                        var cps = string.Join(", ", m.Parameters.Select(p => $"{p.Name}:{p.DataType}"));
                        sb.AppendLine($"{inner}# constructor → at module scope: Make{e.Name}<constructor>({cps}) := {e.Name}{{ … }}");
                        any = true; continue;
                    }
                    var ps = string.Join(", ", m.Parameters.Select(p => $"{p.Name}:{p.DataType}"));
                    sb.AppendLine($"{inner}{m.Name}{Acc(m.Visibility)}({ps}){Ret(m.ReturnType)} =");
                    if (!TryVerseBody($"{e.Id}#{m.Id}", sb, inner + "    ")) sb.AppendLine($"{inner}    # TODO: implement");
                    any = true;
                }
                if (!any) sb.AppendLine($"{inner}# (empty)");
                break;
            }
        }
    }

    // ── Verse body (indentation blocks; no while → loop; case with => arms) ──

    private static bool TryVerseBody(string key, StringBuilder sb, string ind)
    {
        if (_projForBodies is null || !StructogramService.Exists(_projForBodies, key)) return false;
        var sd = StructogramService.Load(_projForBodies, key);
        if (sd.Root.Count == 0) return false;
        RenderVerseSeq(sb, sd.Root, ind);
        return true;
    }

    private static void RenderVerseSeq(StringBuilder sb, List<Models.NsBlock> blocks, string ind)
    {
        foreach (var b in blocks) RenderVerseBlock(sb, b, ind);
    }

    private static void RenderVerseSeqOrComment(StringBuilder sb, List<Models.NsBlock> blocks, string ind)
    {
        if (blocks.Count == 0) { sb.AppendLine($"{ind}# (empty)"); return; }
        RenderVerseSeq(sb, blocks, ind);
    }

    private static void RenderVerseBlock(StringBuilder sb, Models.NsBlock b, string ind)
    {
        var inner = ind + "    ";
        switch (b.Kind)
        {
            case Models.NsBlockKind.Statement:
                var s = (b.Text ?? "").Trim();
                if (s.Length > 0) sb.AppendLine($"{ind}{s}");
                break;

            case Models.NsBlockKind.If:
                sb.AppendLine($"{ind}if ({CondText(b.Text)}):");
                RenderVerseSeqOrComment(sb, b.Body, inner);
                if (b.Else.Count > 0)
                {
                    sb.AppendLine($"{ind}else:");
                    RenderVerseSeqOrComment(sb, b.Else, inner);
                }
                break;

            case Models.NsBlockKind.While:   // Verse has no while → loop, breaking when the test fails
                sb.AppendLine($"{ind}loop:");
                sb.AppendLine($"{inner}if ({CondText(b.Text)}):");
                RenderVerseSeqOrComment(sb, b.Body, inner + "    ");
                sb.AppendLine($"{inner}else:");
                sb.AppendLine($"{inner}    break");
                break;

            case Models.NsBlockKind.DoWhile: // body first, then loop while the test holds
                sb.AppendLine($"{ind}loop:");
                RenderVerseSeqOrComment(sb, b.Body, inner);
                sb.AppendLine($"{inner}if ({CondText(b.Text)}):");
                sb.AppendLine($"{inner}    # keep looping");
                sb.AppendLine($"{inner}else:");
                sb.AppendLine($"{inner}    break");
                break;

            case Models.NsBlockKind.Case:
                sb.AppendLine($"{ind}case ({CondText(b.Text)}):");
                foreach (var arm in b.Arms)
                {
                    sb.AppendLine($"{inner}{arm.Label} => block:");
                    RenderVerseSeqOrComment(sb, arm.Body, inner + "    ");
                }
                break;
        }
    }
}
