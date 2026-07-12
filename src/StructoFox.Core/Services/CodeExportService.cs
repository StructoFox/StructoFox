using System.Text;
using StructoFox.Core.Models;

namespace StructoFox.Core;

public enum ExportLanguage { CSharp, Cpp, Java, TypeScript, Python, Kotlin, Swift, Php, Go, Rust, Verse, JavaScript, C }

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
        ExportLanguage.C          => "c",
        ExportLanguage.Java       => "java",
        ExportLanguage.TypeScript => "ts",
        ExportLanguage.JavaScript => "js",
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

    /// <summary>Ids of Object entities instantiated inside a flow (locals) — their file-level "// instance:" note is skipped.</summary>
    private static HashSet<string> _localObjectIds = new();

    // Shared prelude for single- and multi-file generation: sets the body source + local-object set, resolves
    // namespace ids → dotted names, and returns the emittable entities with id→name / entity→namespace lookups.
    private static (List<CodeEntity> all, Func<string, string> name, Func<CodeEntity, string> nsName)
        Prepare(IEnumerable<CodeEntity> entities, string? projFolder)
    {
        _projForBodies = projFolder;
        var list = entities.ToList();
        var byId = list.ToDictionary(e => e.Id);
        string Name(string id) => byId.TryGetValue(id, out var e) ? e.Name : "";

        var nsEntities = list.Where(e => e.EntityType == CodeEntityType.Namespace).ToList();
        if (projFolder is not null)
            nsEntities = nsEntities.Concat(CodeEntityService.LoadAll(projFolder, "Namespace"))
                                   .GroupBy(n => n.Id).Select(g => g.Last()).ToList();
        var nsById = NamespaceService.FullNames(nsEntities);
        string NsName(CodeEntity e) => string.IsNullOrEmpty(e.Namespace) ? "" : (nsById.TryGetValue(e.Namespace, out var nm) ? nm : e.Namespace.Trim());

        var all = list.Where(e => e.EntityType != CodeEntityType.Namespace).ToList();

        _localObjectIds = new HashSet<string>();
        if (projFolder is not null)
            foreach (var o in all.Where(e => e.EntityType == CodeEntityType.Object))
                if (ObjectUsageScanner.Scan(projFolder, o).Any(u => u.Kind == ObjectUsageScanner.UseKind.Create))
                    _localObjectIds.Add(o.Id);

        return (all, Name, NsName);
    }

    public static string Generate(IEnumerable<CodeEntity> entities, ExportLanguage lang, string? projFolder = null)
    {
        var (all, Name, NsName) = Prepare(entities, projFolder);
        SetFlatten(all, NsName, lang);

        var sb = new StringBuilder();
        sb.AppendLine($"{Cmt(lang)} Auto-generated skeleton from StructoFox. Fill in the logic.");
        if (lang == ExportLanguage.Php) sb.AppendLine("<?php");
        if (lang == ExportLanguage.Python) sb.AppendLine(PythonImports(all));
        sb.AppendLine();

        // C++: emit the entry point LAST so it can call functions and instantiate classes defined above it
        // (a class can't be forward-declared for by-value use). Free functions still get forward declarations.
        CodeEntity? cppEntry = lang == ExportLanguage.Cpp
            ? all.FirstOrDefault(e => e.EntityType == CodeEntityType.Function && e.IsEntryPoint) : null;
        var emit = cppEntry is null ? all : all.Where(e => e != cppEntry).ToList();

        // Neither C nor C++ looks ahead: things must be declared before use. C emits full type typedefs +
        // all prototypes; C++ needs only free-function prototypes (types are ordered before the entry point).
        if (lang == ExportLanguage.C) EmitCForward(sb, all, Name);
        else if (lang == ExportLanguage.Cpp) EmitCppForward(sb, emit, NsName);

        var groups = emit.GroupBy(NsName).OrderBy(g => g.Key);

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

            var ordered = grp.OrderBy(SortRank).ThenBy(x => x.Name).ToList();

            // C# and Java have no free functions — every function must live inside a class. Gather all
            // functions of this namespace (entry point + helpers) into one `Program` holder class.
            if (lang is ExportLanguage.CSharp or ExportLanguage.Java)
            {
                var funcs = ordered.Where(x => x.EntityType == CodeEntityType.Function).ToList();
                if (funcs.Count > 0)
                {
                    EmitFunctionHolder(sb, funcs, lang, ind, Name);
                    sb.AppendLine();
                }
                ordered = ordered.Where(x => x.EntityType != CodeEntityType.Function).ToList();
            }

            foreach (var e in ordered)
            {
                EmitEntity(sb, e, lang, ind, Name);
                sb.AppendLine();
            }

            if (hasNs && braceNs) sb.AppendLine("}");
        }

        // C++ entry point, emitted last so everything it uses is already declared/defined above.
        if (cppEntry is not null) { EmitEntity(sb, cppEntry, lang, "", Name); sb.AppendLine(); }

        return sb.ToString().TrimEnd() + "\n";
    }

    // ── Multi-file generation (one file per type + a functions file; C/C++ = shared header + units) ──

    private const string FileHeader = "Auto-generated skeleton from StructoFox. Fill in the logic.";

    /// <summary>Splits the project into multiple source files. Returns (relative path, content) pairs, or null
    /// if the language isn't multi-file capable yet (caller falls back to single-file <see cref="Generate"/>).</summary>
    public static List<(string path, string content)>? GenerateFiles(
        IEnumerable<CodeEntity> entities, ExportLanguage lang, string projectName, string? projFolder = null)
    {
        var (all, name, ns) = Prepare(entities, projFolder);
        SetFlatten(all, ns, lang);
        return lang switch
        {
            ExportLanguage.CSharp => CSharpFiles(all, name, ns),
            ExportLanguage.C      => CFiles(all, name, projectName),
            ExportLanguage.Cpp    => CppFiles(all, name, ns),
            ExportLanguage.Java   => JavaFiles(all, name, ns),
            ExportLanguage.Go     => GoFiles(all, name),
            ExportLanguage.Php    => PhpFiles(all, name, ns),
            ExportLanguage.TypeScript => TypeScriptFiles(all, name),
            ExportLanguage.Python => PythonFiles(all, name),
            ExportLanguage.Rust   => RustFiles(all, name),
            ExportLanguage.JavaScript => JavaScriptFiles(all, name),
            ExportLanguage.Kotlin => KotlinFiles(all, name),
            ExportLanguage.Swift  => SwiftFiles(all, name),
            _                     => null,
        };
    }

    static bool IsType(CodeEntity e) =>
        e.EntityType is CodeEntityType.Enum or CodeEntityType.Interface or CodeEntityType.Struct or CodeEntityType.Class;

    // ── C#: one .cs per type + Functions.cs (holds the entry point + free functions + object notes) ──
    static List<(string, string)> CSharpFiles(List<CodeEntity> all, Func<string, string> name, Func<CodeEntity, string> ns)
    {
        var files = new List<(string, string)>();

        // Wraps some emitted content in a file header + (optional) namespace block.
        string File(string nsName, Action<StringBuilder, string> body)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// {FileHeader}");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(nsName)) { sb.AppendLine($"namespace {nsName}"); sb.AppendLine("{"); body(sb, "    "); sb.AppendLine("}"); }
            else body(sb, "");
            return sb.ToString().TrimEnd() + "\n";
        }

        foreach (var e in all.Where(IsType))
            files.Add(($"{e.Name}.cs", File(ns(e), (sb, ind) => EmitEntity(sb, e, ExportLanguage.CSharp, ind, name))));

        var funcs = all.Where(e => e.EntityType == CodeEntityType.Function).ToList();
        var objs  = all.Where(e => e.EntityType == CodeEntityType.Object).ToList();
        if (funcs.Count > 0 || objs.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// {FileHeader}");
            sb.AppendLine();
            foreach (var grp in funcs.Concat(objs).GroupBy(ns).OrderBy(g => g.Key))
            {
                bool hasNs = !string.IsNullOrWhiteSpace(grp.Key);
                var ind = hasNs ? "    " : "";
                if (hasNs) { sb.AppendLine($"namespace {grp.Key}"); sb.AppendLine("{"); }
                var gf = grp.Where(e => e.EntityType == CodeEntityType.Function).ToList();
                if (gf.Count > 0) { EmitFunctionHolder(sb, gf, ExportLanguage.CSharp, ind, name); sb.AppendLine(); }
                foreach (var o in grp.Where(e => e.EntityType == CodeEntityType.Object)) { EmitEntity(sb, o, ExportLanguage.CSharp, ind, name); }
                if (hasNs) sb.AppendLine("}");
            }
            files.Add(("Functions.cs", sb.ToString().TrimEnd() + "\n"));
        }
        return files;
    }

    // ── C: a shared <Project>.h (types + prototypes) + main.c (free funcs + entry) + one .c per class ──
    static List<(string, string)> CFiles(List<CodeEntity> all, Func<string, string> name, string projectName)
    {
        var files   = new List<(string, string)>();
        var hdr      = Sanitize(projectName);
        var guard    = hdr.ToUpperInvariant() + "_H";
        var classes  = all.Where(e => e.EntityType is CodeEntityType.Class or CodeEntityType.Struct).ToList();
        var funcs    = all.Where(e => e.EntityType == CodeEntityType.Function).ToList();

        var h = new StringBuilder();
        h.AppendLine($"#ifndef {guard}");
        h.AppendLine($"#define {guard}");
        h.AppendLine();
        h.AppendLine("#include <stdio.h>\n#include <stdlib.h>\n#include <stdbool.h>\n#include <string.h>");
        h.AppendLine();
        EmitCForward(h, all, name);                       // enum typedefs, struct fwd typedefs, prototypes
        foreach (var c in classes) EmitCStruct(h, c, "", name);   // full struct definitions
        h.AppendLine();
        h.AppendLine("#endif");
        files.Add(($"{hdr}.h", h.ToString()));

        string inc = $"#include \"{hdr}.h\"\n\n";

        var m = new StringBuilder(inc);
        var entry = funcs.FirstOrDefault(f => f.IsEntryPoint);
        foreach (var f in funcs.Where(f => f != entry)) { EmitC(m, f, "", name); m.AppendLine(); }
        if (entry is not null) { EmitC(m, entry, "", name); m.AppendLine(); }
        files.Add(("main.c", m.ToString().TrimEnd() + "\n"));

        foreach (var c in classes)
        {
            var cb = new StringBuilder(inc);
            EmitCMethods(cb, c, "", name);
            files.Add(($"{c.Name}.c", cb.ToString().TrimEnd() + "\n"));
        }
        return files;
    }

    // ── C++: one <Class>.hpp per class (header-only, inline methods) + main.cpp (free funcs + entry) ──
    static List<(string, string)> CppFiles(List<CodeEntity> all, Func<string, string> name, Func<CodeEntity, string> ns)
    {
        var files   = new List<(string, string)>();
        var classes = all.Where(IsType).ToList();
        var funcs   = all.Where(e => e.EntityType == CodeEntityType.Function).ToList();

        foreach (var c in classes)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#pragma once");
            sb.AppendLine("#include <iostream>\n#include <string>\n#include <vector>\n#include <map>");
            sb.AppendLine();
            var n = ns(c);
            if (!string.IsNullOrWhiteSpace(n)) { sb.AppendLine($"namespace {n} {{"); EmitEntity(sb, c, ExportLanguage.Cpp, "    ", name); sb.AppendLine("}"); }
            else EmitEntity(sb, c, ExportLanguage.Cpp, "", name);
            files.Add(($"{c.Name}.hpp", sb.ToString().TrimEnd() + "\n"));
        }

        var m = new StringBuilder();
        m.AppendLine("#include <iostream>\n#include <string>\n#include <vector>\n#include <map>");
        foreach (var c in classes) m.AppendLine($"#include \"{c.Name}.hpp\"");
        m.AppendLine();
        var entry = funcs.FirstOrDefault(f => f.IsEntryPoint);
        // Forward-declare the free functions EXCEPT the entry point (main is defined below; declaring it as
        // "void main()" here would clash with its "int main()" definition).
        EmitCppForward(m, all.Where(e => e != entry).ToList(), ns);
        foreach (var f in funcs.Where(f => f != entry)) { EmitEntity(m, f, ExportLanguage.Cpp, "", name); m.AppendLine(); }
        if (entry is not null) { EmitEntity(m, entry, ExportLanguage.Cpp, "", name); m.AppendLine(); }
        files.Add(("main.cpp", m.ToString().TrimEnd() + "\n"));
        return files;
    }

    // ── Java: one .java per public class (in package dirs) + Program.java (entry + static helper methods) ──
    static List<(string, string)> JavaFiles(List<CodeEntity> all, Func<string, string> name, Func<CodeEntity, string> ns)
    {
        var files = new List<(string, string)>();

        string Dir(string nsName) => string.IsNullOrWhiteSpace(nsName) ? "" : nsName.Replace('.', '/') + "/";
        string Wrap(string nsName, Action<StringBuilder> body)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// {FileHeader}");
            if (!string.IsNullOrWhiteSpace(nsName)) { sb.AppendLine($"package {nsName};"); sb.AppendLine(); }
            body(sb);
            return sb.ToString().TrimEnd() + "\n";
        }

        // One public class per file, placed in its package directory (Java requires this).
        foreach (var e in all.Where(IsType))
        {
            var nsName = ns(e);
            files.Add(($"{Dir(nsName)}{e.Name}.java", Wrap(nsName, sb => EmitEntity(sb, e, ExportLanguage.Java, "", name))));
        }

        // Free functions have no place in Java → gather them (per namespace) into a Program holder class.
        foreach (var grp in all.Where(e => e.EntityType == CodeEntityType.Function).GroupBy(ns))
            files.Add(($"{Dir(grp.Key)}Program.java", Wrap(grp.Key, sb => EmitFunctionHolder(sb, grp.ToList(), ExportLanguage.Java, "", name))));

        return files;
    }

    // ── Go: one .go per type + main.go (entry + free funcs), ALL in one package ──
    // Go's package == folder: every .go file in the same directory shares the package and sees the others
    // WITHOUT any import, so a multi-file split needs no cross-file wiring at all (the easiest case). Namespaces
    // are ignored here (flat package), matching the single-file behaviour.
    static List<(string, string)> GoFiles(List<CodeEntity> all, Func<string, string> name)
    {
        var files = new List<(string, string)>();
        // A runnable command is `package main`; a library gets a lowercase package name.
        bool exe = all.Any(e => e.IsEntryPoint && e.EntityType == CodeEntityType.Function);
        string pkg = exe ? "main" : "lib";

        string File(Action<StringBuilder> body)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// {FileHeader}");
            sb.AppendLine($"package {pkg}");
            sb.AppendLine();
            body(sb);
            return sb.ToString().TrimEnd() + "\n";
        }

        foreach (var e in all.Where(IsType))
            files.Add(($"{e.Name}.go", File(sb => EmitEntity(sb, e, ExportLanguage.Go, "", name))));

        var funcs = all.Where(e => e.EntityType == CodeEntityType.Function).ToList();
        var objs  = all.Where(e => e.EntityType == CodeEntityType.Object).ToList();
        if (funcs.Count > 0 || objs.Count > 0)
            files.Add(("main.go", File(sb =>
            {
                var entry = funcs.FirstOrDefault(f => f.IsEntryPoint);
                foreach (var f in funcs.Where(f => f != entry)) { EmitEntity(sb, f, ExportLanguage.Go, "", name); sb.AppendLine(); }
                foreach (var o in objs) EmitEntity(sb, o, ExportLanguage.Go, "", name);
                if (entry is not null) { EmitEntity(sb, entry, ExportLanguage.Go, "", name); sb.AppendLine(); }
            })));

        return files;
    }

    // ── PHP: one <Class>.php per type (PSR-4) + functions.php + index.php bootstrap with a class autoloader ──
    // Cross-file class references are resolved by spl_autoload_register (order-independent, so inheritance just
    // works); free functions aren't autoloadable, so index.php require_once's functions.php explicitly and then
    // calls the entry point.
    static List<(string, string)> PhpFiles(List<CodeEntity> all, Func<string, string> name, Func<CodeEntity, string> ns)
    {
        var files = new List<(string, string)>();
        static string NsSep(string dotted) => dotted.Replace('.', '\\');   // PHP namespace separator is '\'

        // One file per type, each with its own (single) namespace declaration.
        foreach (var e in all.Where(IsType))
        {
            var nsName = ns(e);
            var sb = new StringBuilder();
            sb.AppendLine("<?php");
            sb.AppendLine($"// {FileHeader}");
            if (!string.IsNullOrWhiteSpace(nsName)) sb.AppendLine($"namespace {NsSep(nsName)};");
            sb.AppendLine();
            EmitEntity(sb, e, ExportLanguage.Php, "", name);
            files.Add(($"{e.Name}.php", sb.ToString().TrimEnd() + "\n"));
        }

        // Free functions (+ file-level object notes) → functions.php. Bracketed `namespace X { … }` blocks let
        // several namespaces coexist in one file (functions, unlike classes, can't be autoloaded).
        var funcs = all.Where(e => e.EntityType == CodeEntityType.Function).ToList();
        var objs  = all.Where(e => e.EntityType == CodeEntityType.Object).ToList();
        if (funcs.Count > 0 || objs.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?php");
            sb.AppendLine($"// {FileHeader}");
            sb.AppendLine();
            foreach (var grp in funcs.Concat(objs).GroupBy(ns).OrderBy(g => g.Key))
            {
                bool hasNs = !string.IsNullOrWhiteSpace(grp.Key);
                var ind = hasNs ? "    " : "";
                if (hasNs) sb.AppendLine($"namespace {NsSep(grp.Key)} {{");
                foreach (var f in grp.Where(x => x.EntityType == CodeEntityType.Function)) { EmitEntity(sb, f, ExportLanguage.Php, ind, name); sb.AppendLine(); }
                foreach (var o in grp.Where(x => x.EntityType == CodeEntityType.Object)) EmitEntity(sb, o, ExportLanguage.Php, ind, name);
                if (hasNs) sb.AppendLine("}");
            }
            files.Add(("functions.php", sb.ToString().TrimEnd() + "\n"));
        }

        // index.php: autoload classes on demand (maps Ns\Class → Class.php, flat), pull in the functions file,
        // and run the entry point if there is one.
        var boot = new StringBuilder();
        boot.AppendLine("<?php");
        boot.AppendLine($"// {FileHeader}");
        boot.AppendLine();
        boot.AppendLine("spl_autoload_register(function ($class) {");
        boot.AppendLine("    $pos   = strrpos($class, '\\\\');");
        boot.AppendLine("    $short = $pos === false ? $class : substr($class, $pos + 1);");
        boot.AppendLine("    $file  = __DIR__ . '/' . $short . '.php';");
        boot.AppendLine("    if (file_exists($file)) require_once $file;");
        boot.AppendLine("});");
        if (funcs.Count > 0 || objs.Count > 0) { boot.AppendLine(); boot.AppendLine("require_once __DIR__ . '/functions.php';"); }
        var entry = funcs.FirstOrDefault(f => f.IsEntryPoint);
        if (entry is not null)
        {
            var en   = ns(entry);
            var call = string.IsNullOrWhiteSpace(en) ? entry.Name : "\\" + NsSep(en) + "\\" + entry.Name;
            boot.AppendLine();
            boot.AppendLine($"{call}();");
        }
        files.Add(("index.php", boot.ToString().TrimEnd() + "\n"));

        return files;
    }

    // ── TypeScript: src/<Type>.ts per type + functions.ts + index.ts bootstrap (all under src/) ──
    // ES modules need explicit relative imports; we generate them DETERMINISTICALLY (each file imports the other
    // project types) so the model never has to invent a path. Namespaces are flattened (one module folder).
    // Circular imports are safe here: every cross-type reference happens inside a body, not at module top level.
    static List<(string, string)> TypeScriptFiles(List<CodeEntity> all, Func<string, string> name)
    {
        var files     = new List<(string, string)>();
        var types      = all.Where(IsType).ToList();
        var typeNames  = types.Select(t => t.Name).ToList();

        // Imports of every project type except `self` (a type doesn't import itself).
        string Imports(string? self) =>
            string.Concat(typeNames.Where(n => n != self).Select(n => $"import {{ {n} }} from './{n}';\n"));

        foreach (var e in types)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// {FileHeader}");
            var imp = Imports(e.Name);
            if (imp.Length > 0) { sb.Append(imp); sb.AppendLine(); }
            EmitEntity(sb, e, ExportLanguage.TypeScript, "", name);
            files.Add(($"src/{e.Name}.ts", sb.ToString().TrimEnd() + "\n"));
        }

        var funcs = all.Where(e => e.EntityType == CodeEntityType.Function).ToList();
        var objs  = all.Where(e => e.EntityType == CodeEntityType.Object).ToList();
        if (funcs.Count > 0 || objs.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// {FileHeader}");
            var imp = Imports(null);
            if (imp.Length > 0) { sb.Append(imp); sb.AppendLine(); }
            var entry = funcs.FirstOrDefault(f => f.IsEntryPoint);
            foreach (var f in funcs.Where(f => f != entry)) { EmitEntity(sb, f, ExportLanguage.TypeScript, "", name); sb.AppendLine(); }
            foreach (var o in objs) EmitEntity(sb, o, ExportLanguage.TypeScript, "", name);
            if (entry is not null) { EmitEntity(sb, entry, ExportLanguage.TypeScript, "", name); sb.AppendLine(); }
            files.Add(("src/functions.ts", sb.ToString().TrimEnd() + "\n"));
        }

        // index.ts bootstrap: import the entry point and run it. Deterministic — excluded from AI filling.
        var entryFn = funcs.FirstOrDefault(f => f.IsEntryPoint);
        if (entryFn is not null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// {FileHeader}");
            sb.AppendLine($"import {{ {entryFn.Name} }} from './functions';");
            sb.AppendLine();
            sb.AppendLine($"{entryFn.Name}();");
            files.Add(("src/index.ts", sb.ToString().TrimEnd() + "\n"));
        }

        return files;
    }

    // ── Kotlin: src/main/kotlin/<Type>.kt per type + Main.kt (top-level funcs + entry) ──
    // Kotlin files in the same package see each other WITHOUT imports (default package here), so no cross-file
    // wiring is needed. The entry `fun main` lives in Main.kt (→ class MainKt, matching the Gradle mainClass).
    static List<(string, string)> KotlinFiles(List<CodeEntity> all, Func<string, string> name)
    {
        var files = new List<(string, string)>();
        const string dir = "src/main/kotlin/";
        string File(Action<StringBuilder> body)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// {FileHeader}");
            sb.AppendLine();
            body(sb);
            return sb.ToString().TrimEnd() + "\n";
        }

        foreach (var e in all.Where(IsType))
            files.Add(($"{dir}{e.Name}.kt", File(sb => EmitEntity(sb, e, ExportLanguage.Kotlin, "", name))));

        files.Add(($"{dir}Main.kt", File(sb =>
        {
            var funcs = all.Where(e => e.EntityType == CodeEntityType.Function).ToList();
            var objs  = all.Where(e => e.EntityType == CodeEntityType.Object).ToList();
            var entry = funcs.FirstOrDefault(f => f.IsEntryPoint);
            foreach (var f in funcs.Where(f => f != entry)) { EmitEntity(sb, f, ExportLanguage.Kotlin, "", name); sb.AppendLine(); }
            foreach (var o in objs) EmitEntity(sb, o, ExportLanguage.Kotlin, "", name);
            if (entry is not null) { EmitEntity(sb, entry, ExportLanguage.Kotlin, "", name); sb.AppendLine(); }
        })));

        return files;
    }

    // ── Swift: <Type>.swift per type + Functions.swift (funcs + entry) + main.swift (top-level entry call) ──
    // Swift files in the same module see each other WITHOUT imports. Top-level code is allowed ONLY in main.swift,
    // so the entry is called there; its body lives in Functions.swift. main.swift is a deterministic bootstrap
    // (excluded from AI filling).
    static List<(string, string)> SwiftFiles(List<CodeEntity> all, Func<string, string> name)
    {
        var files = new List<(string, string)>();
        string File(Action<StringBuilder> body)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// {FileHeader}");
            sb.AppendLine();
            body(sb);
            return sb.ToString().TrimEnd() + "\n";
        }

        foreach (var e in all.Where(IsType))
            files.Add(($"{e.Name}.swift", File(sb => EmitEntity(sb, e, ExportLanguage.Swift, "", name))));

        var funcs = all.Where(e => e.EntityType == CodeEntityType.Function).ToList();
        var objs  = all.Where(e => e.EntityType == CodeEntityType.Object).ToList();
        if (funcs.Count > 0 || objs.Count > 0)
            files.Add(("Functions.swift", File(sb =>
            {
                foreach (var f in funcs) { EmitEntity(sb, f, ExportLanguage.Swift, "", name); sb.AppendLine(); }
                foreach (var o in objs) EmitEntity(sb, o, ExportLanguage.Swift, "", name);
            })));

        // main.swift: the sole file allowed to hold top-level code → run the entry here. Excluded from AI filling.
        var entry = funcs.FirstOrDefault(f => f.IsEntryPoint);
        var mn = new StringBuilder();
        mn.AppendLine($"// {FileHeader}");
        if (entry is not null) { mn.AppendLine(); mn.AppendLine($"{entry.Name}()"); }
        files.Add(("main.swift", mn.ToString().TrimEnd() + "\n"));

        return files;
    }

    // ── JavaScript: src/<Type>.js per type + functions.js + index.js bootstrap (ES modules, like TypeScript) ──
    // Same as the TS split, but Node's ESM loader requires the `.js` extension in relative imports. Imports are
    // generated deterministically; cross-type references sit inside bodies, so circular imports are safe.
    static List<(string, string)> JavaScriptFiles(List<CodeEntity> all, Func<string, string> name)
    {
        var files     = new List<(string, string)>();
        var types      = all.Where(IsType).ToList();
        var typeNames  = types.Select(t => t.Name).ToList();

        string Imports(string? self) =>
            string.Concat(typeNames.Where(n => n != self).Select(n => $"import {{ {n} }} from './{n}.js';\n"));

        foreach (var e in types)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// {FileHeader}");
            var imp = Imports(e.Name);
            if (imp.Length > 0) { sb.Append(imp); sb.AppendLine(); }
            EmitEntity(sb, e, ExportLanguage.JavaScript, "", name);
            files.Add(($"src/{e.Name}.js", sb.ToString().TrimEnd() + "\n"));
        }

        var funcs = all.Where(e => e.EntityType == CodeEntityType.Function).ToList();
        var objs  = all.Where(e => e.EntityType == CodeEntityType.Object).ToList();
        if (funcs.Count > 0 || objs.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// {FileHeader}");
            var imp = Imports(null);
            if (imp.Length > 0) { sb.Append(imp); sb.AppendLine(); }
            var entry = funcs.FirstOrDefault(f => f.IsEntryPoint);
            foreach (var f in funcs.Where(f => f != entry)) { EmitEntity(sb, f, ExportLanguage.JavaScript, "", name); sb.AppendLine(); }
            foreach (var o in objs) EmitEntity(sb, o, ExportLanguage.JavaScript, "", name);
            if (entry is not null) { EmitEntity(sb, entry, ExportLanguage.JavaScript, "", name); sb.AppendLine(); }
            files.Add(("src/functions.js", sb.ToString().TrimEnd() + "\n"));
        }

        var entryFn = funcs.FirstOrDefault(f => f.IsEntryPoint);
        if (entryFn is not null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// {FileHeader}");
            sb.AppendLine($"import {{ {entryFn.Name} }} from './functions.js';");
            sb.AppendLine();
            sb.AppendLine($"{entryFn.Name}();");
            files.Add(("src/index.js", sb.ToString().TrimEnd() + "\n"));
        }

        return files;
    }

    // ── Python: <Type>.py per type + functions.py + __main__.py bootstrap ──
    // Python runs a module's top-level `from X import Y` at IMPORT time, so importing every sibling everywhere
    // deadlocks mutually-referencing modules (ImportError). Instead each file imports ONLY the project types it
    // actually references (from its declaration graph + body text). Type modules then usually import nothing from
    // each other, and functions.py imports the types it uses one-directionally (nothing imports functions.py),
    // so no spurious cycles arise. (A genuine mutual reference between two classes is rare and a real design cycle.)
    static List<(string, string)> PythonFiles(List<CodeEntity> all, Func<string, string> name)
    {
        var files     = new List<(string, string)>();
        var types      = all.Where(IsType).ToList();
        var typeNames  = types.Select(t => t.Name).ToList();

        // File header + standard-library imports + `from <T> import <T>` lines for the referenced project types.
        string Header(string stdImports, IEnumerable<string> typeRefs)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {FileHeader}");
            if (!string.IsNullOrWhiteSpace(stdImports)) sb.AppendLine(stdImports);
            var ti = typeRefs.Distinct().OrderBy(x => x).Select(t => $"from {t} import {t}").ToList();
            if (ti.Count > 0) sb.AppendLine(string.Join("\n", ti));
            sb.AppendLine();
            return sb.ToString();
        }

        foreach (var e in types)
        {
            var sb = new StringBuilder();
            sb.Append(Header(PythonImports(new List<CodeEntity> { e }), ReferencedTypes(e, typeNames, name)));
            EmitEntity(sb, e, ExportLanguage.Python, "", name);
            files.Add(($"{e.Name}.py", sb.ToString().TrimEnd() + "\n"));
        }

        var funcs = all.Where(e => e.EntityType == CodeEntityType.Function).ToList();
        var objs  = all.Where(e => e.EntityType == CodeEntityType.Object).ToList();
        if (funcs.Count > 0 || objs.Count > 0)
        {
            var refs = new HashSet<string>();
            foreach (var e in funcs.Concat(objs)) refs.UnionWith(ReferencedTypes(e, typeNames, name));
            var sb = new StringBuilder();
            sb.Append(Header("", refs));
            var entry = funcs.FirstOrDefault(f => f.IsEntryPoint);
            foreach (var f in funcs.Where(f => f != entry)) { EmitEntity(sb, f, ExportLanguage.Python, "", name); sb.AppendLine(); }
            foreach (var o in objs) EmitEntity(sb, o, ExportLanguage.Python, "", name);
            if (entry is not null) { EmitEntity(sb, entry, ExportLanguage.Python, "", name); sb.AppendLine(); }
            files.Add(("functions.py", sb.ToString().TrimEnd() + "\n"));
        }

        // __main__.py bootstrap: import the entry point and run it under the standard guard. Excluded from AI fill.
        var entryFn = funcs.FirstOrDefault(f => f.IsEntryPoint);
        if (entryFn is not null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {FileHeader}");
            sb.AppendLine($"from functions import {entryFn.Name}");
            sb.AppendLine();
            sb.AppendLine("if __name__ == \"__main__\":");
            sb.AppendLine($"    {entryFn.Name}()");
            files.Add(("__main__.py", sb.ToString().TrimEnd() + "\n"));
        }

        return files;
    }

    // ── Rust: src/<mod>.rs per type + functions.rs (helpers + entry) + main.rs crate root (mod decls + main) ──
    // Rust's module system needs the `mod` declarations in the crate root (main.rs) and cross-module items pulled
    // in with `use crate::<mod>::<Type>;` (types are already emitted `pub`). No import-cycle hazard. main.rs is a
    // deterministic bootstrap (excluded from AI filling) so the module wiring can never be broken by the model.
    static List<(string, string)> RustFiles(List<CodeEntity> all, Func<string, string> name)
    {
        var files     = new List<(string, string)>();
        var types      = all.Where(IsType).ToList();
        var typeNames  = types.Select(t => t.Name).ToList();
        static string Mod(string typeName) => typeName.ToLowerInvariant();   // module/file stem (Rust wants lowercase)

        string UseLines(IEnumerable<string> refs) =>
            string.Concat(refs.Distinct().OrderBy(x => x).Select(t => $"use crate::{Mod(t)}::{t};\n"));

        foreach (var e in types)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// {FileHeader}");
            var uses = UseLines(ReferencedTypes(e, typeNames, name));
            if (uses.Length > 0) { sb.Append(uses); sb.AppendLine(); }
            EmitEntity(sb, e, ExportLanguage.Rust, "", name);
            files.Add(($"src/{Mod(e.Name)}.rs", sb.ToString().TrimEnd() + "\n"));
        }

        var funcs = all.Where(e => e.EntityType == CodeEntityType.Function).ToList();
        var objs  = all.Where(e => e.EntityType == CodeEntityType.Object).ToList();
        bool hasFunctions = funcs.Count > 0 || objs.Count > 0;
        if (hasFunctions)
        {
            var refs = new HashSet<string>();
            foreach (var e in funcs.Concat(objs)) refs.UnionWith(ReferencedTypes(e, typeNames, name));
            var sb = new StringBuilder();
            sb.AppendLine($"// {FileHeader}");
            var uses = UseLines(refs);
            if (uses.Length > 0) { sb.Append(uses); sb.AppendLine(); }
            // EmitRust names the entry `main`; here it lives in the functions module (crate main calls it).
            foreach (var f in funcs) { EmitEntity(sb, f, ExportLanguage.Rust, "", name); sb.AppendLine(); }
            foreach (var o in objs) EmitEntity(sb, o, ExportLanguage.Rust, "", name);
            files.Add(("src/functions.rs", sb.ToString().TrimEnd() + "\n"));
        }

        // main.rs crate root: declare every module, then run the entry. Deterministic — excluded from AI filling.
        var mn = new StringBuilder();
        mn.AppendLine($"// {FileHeader}");
        foreach (var t in types) mn.AppendLine($"mod {Mod(t.Name)};");
        if (hasFunctions) mn.AppendLine("mod functions;");
        mn.AppendLine();
        if (funcs.Any(f => f.IsEntryPoint))
        {
            mn.AppendLine("fn main() {");
            mn.AppendLine("    functions::main();");
            mn.AppendLine("}");
        }
        files.Add(("src/main.rs", mn.ToString().TrimEnd() + "\n"));

        return files;
    }

    // Every project type an entity references — via its declaration graph (base/interfaces, instance-of, field
    // and method/parameter/return types) AND textually inside its bodies (so a `new Other()` buried in a method
    // is imported too). Whole-word matched, excluding the entity's own name.
    static HashSet<string> ReferencedTypes(CodeEntity e, List<string> typeNames, Func<string, string> name)
    {
        var text = new StringBuilder();
        void Id(string id) { if (!string.IsNullOrEmpty(id)) text.Append(' ').Append(name(id)); }

        Id(e.BaseClassId);
        foreach (var i in e.ImplementsIds) Id(i);
        Id(e.InstanceOfId);
        foreach (var f in e.Fields) text.Append(' ').Append(f.DataType);
        foreach (var m in e.Methods)
        {
            text.Append(' ').Append(m.ReturnType);
            foreach (var p in m.Parameters) text.Append(' ').Append(p.DataType);
            AppendBodyText($"{e.Id}#{m.Id}", text);
        }
        if (e.EntityType == CodeEntityType.Function)
        {
            var (ins, ret) = FuncSig(e);
            text.Append(' ').Append(ret);
            foreach (var p in ins) text.Append(' ').Append(p.DataType);
            AppendBodyText(e.Id, text);
        }

        var body = text.ToString();
        var refs = new HashSet<string>();
        foreach (var t in typeNames)
            if (t != e.Name && System.Text.RegularExpressions.Regex.IsMatch(body, $@"\b{System.Text.RegularExpressions.Regex.Escape(t)}\b"))
                refs.Add(t);
        return refs;
    }

    // Heuristic for Rust: does this method's body assign to one of the type's fields? If so it needs `&mut self`;
    // a pure reader keeps `&self`. Matches `field =` or `self.field =` (not `==`) anywhere in the body text.
    static bool MutatesSelf(string key, List<CodeField> fields)
    {
        if (fields.Count == 0) return false;
        var text = new StringBuilder();
        AppendBodyText(key, text);
        var body = text.ToString();
        foreach (var f in fields)
            if (System.Text.RegularExpressions.Regex.IsMatch(body, $@"\b{System.Text.RegularExpressions.Regex.Escape(f.Name)}\b\s*=(?![=])"))
                return true;
        return false;
    }

    // Appends all statement/condition text of a body's structogram (recursively) into <paramref name="into"/>.
    static void AppendBodyText(string key, StringBuilder into)
    {
        if (BodyFor(key) is not { Root.Count: > 0 } sd) return;
        void Walk(List<Models.NsBlock> bs)
        {
            foreach (var b in bs)
            {
                into.Append(' ').Append(b.Text);
                Walk(b.Body); Walk(b.Else);
                foreach (var arm in b.Arms) Walk(arm.Body);
            }
        }
        Walk(sd.Root);
    }

    // Sanitizes a name for use as a file/identifier stem.
    static string Sanitize(string s)
    {
        var cleaned = new string((s ?? "").Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        return cleaned.Length == 0 ? "Project" : cleaned;
    }

    private static int SortRank(CodeEntity e) => e.IsEntryPoint ? -1 : e.EntityType switch
    {
        CodeEntityType.Enum => 0, CodeEntityType.Interface => 1, CodeEntityType.Struct => 2,
        CodeEntityType.Class => 3, CodeEntityType.Function => 4, CodeEntityType.Object => 5, _ => 6
    };

    private static void EmitEntity(StringBuilder sb, CodeEntity e, ExportLanguage lang, string ind, Func<string, string> name)
    {
        // A locally-instantiated object is created in a flow (real `new` in a body) — don't also emit a
        // file-level "// instance:" note for it.
        if (e.EntityType == CodeEntityType.Object && _localObjectIds.Contains(e.Id)) return;

        Doc(sb, e, ind, Cmt(lang));

        // In non-class languages the entry point stays a free function; just flag it.
        // (C#/Java route their functions through EmitFunctionHolder instead.)
        if (e.EntityType == CodeEntityType.Function && e.IsEntryPoint)
            sb.AppendLine($"{ind}{Cmt(lang)} ▶ Entry point (main)");

        EmitByLang(sb, e, lang, ind, name);
    }

    // C#/Java holder: wraps every free function of a namespace in one class (entry point first). C# uses a
    // `static class Functions` (all members are static anyway, and bare intra-file calls resolve inside it);
    // Java keeps a plain `Program` class (Java has no top-level static classes).
    private static void EmitFunctionHolder(StringBuilder sb, List<CodeEntity> funcs, ExportLanguage lang, string ind, Func<string, string> name)
    {
        var inner = ind + "    ";
        sb.AppendLine(lang == ExportLanguage.CSharp ? $"{ind}public static class Functions" : $"{ind}public class Program");
        sb.AppendLine($"{ind}{{");
        bool first = true;
        foreach (var f in funcs)
        {
            if (!first) sb.AppendLine();
            first = false;
            Doc(sb, f, inner, Cmt(lang));
            if (f.IsEntryPoint) sb.AppendLine($"{inner}{Cmt(lang)} ▶ Entry point (main)");
            EmitByLang(sb, f, lang, inner, name);
        }
        sb.AppendLine($"{ind}}}");
    }

    private static void EmitByLang(StringBuilder sb, CodeEntity e, ExportLanguage lang, string ind, Func<string, string> name)
    {
        switch (lang)
        {
            case ExportLanguage.CSharp:     EmitCSharp(sb, e, ind, name); break;
            case ExportLanguage.Cpp:        EmitCpp(sb, e, ind, name); break;
            case ExportLanguage.C:          EmitC(sb, e, ind, name); break;
            case ExportLanguage.Java:       EmitJava(sb, e, ind, name); break;
            case ExportLanguage.TypeScript: EmitTypeScript(sb, e, ind, name); break;
            case ExportLanguage.JavaScript: EmitJavaScript(sb, e, ind, name); break;
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
    /// <summary>
    /// The structogram to use as the body for <paramref name="key"/>. The PAP (flowchart) is the authored
    /// source, so if one exists it is converted LIVE — edits/relinks flow into codegen with no manual
    /// "→ structogram" step and no stale saved structogram. A saved structogram is only a fallback: when there
    /// is no flowchart, or when the flow can't be fully structured (arbitrary jumps) AND a hand-made structogram
    /// exists to fall back on. Null → no body (caller emits a stub). Works without the AI plugin.
    /// </summary>
    private static Models.StructogramData? BodyFor(string key)
    {
        var sd = BodyForRaw(key);
        // For flatten-namespace languages, strip project `Namespace.` prefixes from the pseudocode BEFORE the
        // model sees it (`TestSpace.TestKlasse` → `TestKlasse`), so it can't reintroduce a prefix that won't
        // resolve in a flat package/module. (Each BodyForRaw returns a fresh instance, so mutating is safe.)
        if (sd is not null && _flattenNs.Count > 0) StripNamespacePrefixes(sd.Root);
        return sd;
    }

    private static Models.StructogramData? BodyForRaw(string key)
    {
        if (_projForBodies is null) return null;

        bool hasStruct = StructogramService.Exists(_projForBodies, key);
        if (FlowChartService.Exists(_projForBodies, key))
        {
            var fc = FlowChartService.Load(_projForBodies, key);
            if (fc.Nodes.Count > 0)
            {
                var sd = StructogramConverter.Convert(fc, "", out var unstructured);
                // Clean conversion → the PAP wins. Partial → prefer a hand-made structogram if one exists,
                // else use the partial result rather than nothing.
                if ((unstructured.Count == 0 || !hasStruct) && sd.Root.Count > 0) return sd;
            }
        }
        return hasStruct ? StructogramService.Load(_projForBodies, key) : null;
    }

    // Project namespace full-names to strip from body pseudocode (non-empty only for flatten-namespace languages).
    private static HashSet<string> _flattenNs = new();

    // Languages that emit every type at top level (no namespace qualifier on a type reference), so a
    // `Namespace.Type` in the pseudocode must collapse to `Type`. (C#/C++/Java/PHP keep namespaces; Rust uses
    // module paths; Verse keeps its own scoping — none of them strip.)
    private static bool FlattensNamespaces(ExportLanguage l) =>
        l is ExportLanguage.Go or ExportLanguage.JavaScript or ExportLanguage.TypeScript
          or ExportLanguage.Kotlin or ExportLanguage.Swift or ExportLanguage.Python;

    private static void SetFlatten(IEnumerable<CodeEntity> all, Func<CodeEntity, string> ns, ExportLanguage lang) =>
        _flattenNs = FlattensNamespaces(lang)
            ? all.Select(ns).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToHashSet()
            : new HashSet<string>();

    private static void StripNamespacePrefixes(List<Models.NsBlock> blocks)
    {
        foreach (var b in blocks)
        {
            b.Text = StripNsText(b.Text);
            StripNamespacePrefixes(b.Body);
            StripNamespacePrefixes(b.Else);
            foreach (var arm in b.Arms) StripNamespacePrefixes(arm.Body);
        }
    }

    private static string StripNsText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var ns in _flattenNs)
            text = System.Text.RegularExpressions.Regex.Replace(text, $@"\b{System.Text.RegularExpressions.Regex.Escape(ns)}\.", "");
        return text;
    }

    private static bool TryBracedBody(string key, StringBuilder sb, string ind)
    {
        if (BodyFor(key) is not { Root.Count: > 0 } sd) return false;
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
            case Models.NsBlockKind.Subroutine:
            case Models.NsBlockKind.Jump:
                var s = StTerm(b.Text);
                AppendStmtLines(sb, ind, s);
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

    /// <summary>Emits a (possibly multi-line) statement with EVERY line indented. When the user splits an instruction
    /// block with Enter, all its lines must sit at the block's indent — not just the first (the rest used to land at
    /// column 0). Blank lines stay blank.</summary>
    private static void AppendStmtLines(StringBuilder sb, string ind, string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        foreach (var line in s.Replace("\r\n", "\n").Split('\n'))
            sb.AppendLine(line.Length == 0 ? "" : ind + line);
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
        if (BodyFor(key) is not { Root.Count: > 0 } sd) return false;
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
            case Models.NsBlockKind.Subroutine:
            case Models.NsBlockKind.Jump:
                var s = (b.Text ?? "").Trim();
                AppendStmtLines(sb, ind, s);
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
        if (BodyFor(key) is not { Root.Count: > 0 } sd) return false;
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
            case Models.NsBlockKind.Subroutine:
            case Models.NsBlockKind.Jump:
                var s = (b.Text ?? "").Trim();
                AppendStmtLines(sb, ind, s);
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
        if (BodyFor(key) is not { Root.Count: > 0 } sd) return false;
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
            case Models.NsBlockKind.Subroutine:
            case Models.NsBlockKind.Jump:
                var s = StTerm(b.Text);
                AppendStmtLines(sb, ind, s);
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
                // C#'s entry point must be named `Main`, whatever the author called the diagram.
                var fn = e.IsEntryPoint ? "Main" : e.Name;
                sb.AppendLine($"{ind}public static {ret} {fn}({ps})");
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

    // Forward-declares the free functions (namespace-wrapped) so any function can call any other regardless
    // of emission order — the entry point, emitted last, then sees them all.
    private static void EmitCppForward(StringBuilder sb, List<CodeEntity> all, Func<CodeEntity, string> nsFull)
    {
        string Conv(PassingConvention c) => c switch { PassingConvention.Reference => "&", PassingConvention.Pointer => "*", _ => "" };
        var funcs = all.Where(e => e.EntityType == CodeEntityType.Function).ToList();
        if (funcs.Count == 0) return;

        sb.AppendLine("// ── Forward declarations ──");
        foreach (var grp in funcs.GroupBy(nsFull).OrderBy(g => g.Key))
        {
            bool hasNs = !string.IsNullOrWhiteSpace(grp.Key);
            string ind = hasNs ? "    " : "";
            if (hasNs) sb.AppendLine($"namespace {grp.Key} {{");
            foreach (var e in grp)
            {
                var (ins, ret) = FuncSig(e);
                var ps = string.Join(", ", ins.Select(p => $"{p.DataType}{Conv(p.Convention)} {p.Name}"));
                sb.AppendLine($"{ind}{ret} {e.Name}({ps});");
            }
            if (hasNs) sb.AppendLine("}");
        }
        sb.AppendLine();
    }

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
                // C++'s entry point must be `int main(...)`; other functions keep their signature.
                var fn = e.IsEntryPoint ? "main" : e.Name;
                var rt = e.IsEntryPoint ? "int" : ret;
                sb.AppendLine($"{ind}{rt} {fn}({ps}) {{");
                TryBracedBody(e.Id, sb, inner);
                if (e.IsEntryPoint) sb.AppendLine($"{inner}return 0;");
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
                    var minner = inner + "    ";
                    foreach (var m in methods)
                    {
                        var ps  = string.Join(", ", m.Parameters.Select(p => $"{p.DataType}{Conv(p.Convention)} {p.Name}"));
                        var key = $"{e.Id}#{m.Id}";
                        // Interface = pure virtual declaration; everything else is defined INLINE in the class
                        // (single definition site — an in-class body plus an out-of-line one would redefine it).
                        if (iface) { sb.AppendLine($"{inner}virtual {m.ReturnType} {m.Name}({ps}) = 0;"); continue; }
                        string mhead = m.Kind switch
                        {
                            MethodKind.Constructor => $"{inner}{e.Name}({ps}) {{",
                            MethodKind.Destructor  => $"{inner}{(bases.Count > 0 ? "virtual " : "")}~{e.Name}() {{",
                            _                      => $"{inner}{(m.IsStatic ? "static " : "")}{m.ReturnType} {m.Name}({ps}) {{",
                        };
                        sb.AppendLine(mhead);
                        TryBracedBody(key, sb, minner);
                        sb.AppendLine($"{inner}}}");
                    }
                }
                sb.AppendLine($"{ind}}};");
                break;
            }
        }
    }

    // ── C (structs + free functions; no classes/interfaces) ───────────────────

    // Maps a few common type names to C; anything else passes through as-is (user's responsibility).
    private static string CType(string t) => (t ?? "").Trim() switch
    {
        "string" or "String" => "const char*",
        "" => "void",
        var s => s,
    };

    // C forward-declaration header: enum typedefs, opaque struct typedefs, and every function/method prototype,
    // so code emitted later (or earlier, like main) can reference anything regardless of order.
    private static void EmitCForward(StringBuilder sb, List<CodeEntity> all, Func<string, string> name)
    {
        var funcs   = all.Where(e => e.EntityType == CodeEntityType.Function).ToList();
        var enums   = all.Where(e => e.EntityType == CodeEntityType.Enum).ToList();
        var classes = all.Where(e => e.EntityType is CodeEntityType.Class or CodeEntityType.Struct).ToList();
        if (funcs.Count + enums.Count + classes.Count == 0) return;

        sb.AppendLine("// ── Forward declarations ──");
        // Enums can't be forward-declared in C, so define them fully here (and skip them in the body).
        foreach (var e in enums)
            sb.AppendLine($"typedef enum {{ {string.Join(", ", e.EnumValues)} }} {e.Name};");
        // An opaque typedef names the struct type everywhere; the full body comes later.
        foreach (var e in classes)
            sb.AppendLine($"typedef struct {e.Name} {e.Name};");
        foreach (var e in funcs)
        {
            var (ins, ret) = FuncSig(e);
            var ps = ins.Count == 0 ? "void" : string.Join(", ", ins.Select(p => $"{CType(p.DataType)} {p.Name}"));
            var fn = e.IsEntryPoint ? "main" : e.Name;
            var rt = e.IsEntryPoint ? "int" : CType(ret);
            sb.AppendLine($"{rt} {fn}({(e.IsEntryPoint ? "void" : ps)});");
        }
        foreach (var e in classes)
            foreach (var m in e.Methods)
            {
                var extra = string.Join(", ", m.Parameters.Select(p => $"{CType(p.DataType)} {p.Name}"));
                if (m.Kind == MethodKind.Constructor)     sb.AppendLine($"{e.Name}* {e.Name}_new({(extra.Length == 0 ? "void" : extra)});");
                else if (m.Kind == MethodKind.Destructor) sb.AppendLine($"void {e.Name}_free({e.Name}* self);");
                else                                      sb.AppendLine($"{CType(m.ReturnType)} {e.Name}_{m.Name}({e.Name}* self{(extra.Length == 0 ? "" : ", " + extra)});");
            }
        sb.AppendLine();
    }

    private static void EmitC(StringBuilder sb, CodeEntity e, string ind, Func<string, string> name)
    {
        var inner = ind + "    ";
        switch (e.EntityType)
        {
            case CodeEntityType.Enum:
                break;   // already emitted as a full typedef in the forward-declaration header
            case CodeEntityType.Function:
            {
                var (ins, ret) = FuncSig(e);
                var ps = ins.Count == 0 ? "void" : string.Join(", ", ins.Select(p => $"{CType(p.DataType)} {p.Name}"));
                // C's entry point is `int main(void)`; other functions keep their signature.
                var fn = e.IsEntryPoint ? "main" : e.Name;
                var rt = e.IsEntryPoint ? "int" : CType(ret);
                sb.AppendLine($"{ind}{rt} {fn}({(e.IsEntryPoint ? "void" : ps)}) {{");
                if (!TryBracedBody(e.Id, sb, inner))
                {
                    if (e.IsEntryPoint) sb.AppendLine($"{inner}return 0;");
                    else if (rt != "void") sb.AppendLine($"{inner}/* TODO: implement */");
                }
                else if (e.IsEntryPoint) sb.AppendLine($"{inner}return 0;");
                sb.AppendLine($"{ind}}}");
                break;
            }
            case CodeEntityType.Object:
                sb.AppendLine($"{ind}// instance: {(string.IsNullOrEmpty(e.InstanceOfId) ? "/* type */" : name(e.InstanceOfId))} {e.Name};");
                break;
            case CodeEntityType.Interface:
                sb.AppendLine($"{ind}// interface {e.Name} — C has no interfaces (use a struct of function pointers).");
                break;
            default:   // Class / Struct → a struct of fields + methods as free functions taking the struct.
                EmitCStruct(sb, e, ind, name);
                EmitCMethods(sb, e, ind, name);
                break;
        }
    }

    // The struct body only (the typedef lives in the forward header). Reused by the multi-file C header.
    private static void EmitCStruct(StringBuilder sb, CodeEntity e, string ind, Func<string, string> name)
    {
        var inner = ind + "    ";
        sb.AppendLine($"{ind}struct {e.Name} {{");
        if (!string.IsNullOrEmpty(e.BaseClassId))
            sb.AppendLine($"{inner}{name(e.BaseClassId)} base;   /* inheritance → embedded base struct */");
        foreach (var f in e.Fields)
            sb.AppendLine($"{inner}{CType(f.DataType)} {f.Name};");
        sb.AppendLine($"{ind}}};");
    }

    // The method definitions (free functions taking the struct). Reused by the multi-file per-class .c file.
    private static void EmitCMethods(StringBuilder sb, CodeEntity e, string ind, Func<string, string> name)
    {
        var inner = ind + "    ";
        foreach (var m in e.Methods)
        {
            var extra = string.Join(", ", m.Parameters.Select(p => $"{CType(p.DataType)} {p.Name}"));
            if (m.Kind == MethodKind.Constructor)
            {
                sb.AppendLine($"{ind}{e.Name}* {e.Name}_new({(extra.Length == 0 ? "void" : extra)}) {{");
                sb.AppendLine($"{inner}{e.Name}* self = ({e.Name}*)malloc(sizeof({e.Name}));");
                TryBracedBody($"{e.Id}#{m.Id}", sb, inner);
                sb.AppendLine($"{inner}return self;");
                sb.AppendLine($"{ind}}}");
                continue;
            }
            if (m.Kind == MethodKind.Destructor)
            {
                sb.AppendLine($"{ind}void {e.Name}_free({e.Name}* self) {{");
                if (!TryBracedBody($"{e.Id}#{m.Id}", sb, inner)) sb.AppendLine($"{inner}free(self);");
                sb.AppendLine($"{ind}}}");
                continue;
            }
            var self = $"{e.Name}* self" + (extra.Length == 0 ? "" : ", " + extra);
            var rt = CType(m.ReturnType);
            sb.AppendLine($"{ind}{rt} {e.Name}_{m.Name}({self}) {{");
            if (!TryBracedBody($"{e.Id}#{m.Id}", sb, inner) && rt != "void") sb.AppendLine($"{inner}/* TODO: implement */");
            sb.AppendLine($"{ind}}}");
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
                // Java's entry point must be exactly `public static void main(String[] args)`; helpers keep theirs.
                var fn = e.IsEntryPoint ? "main" : e.Name;
                sb.AppendLine($"{ind}public static {(e.IsEntryPoint ? "void" : ret)} {fn}({(e.IsEntryPoint ? "String[] args" : ps)}) {{");
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

    // ── JavaScript (TypeScript without the types) ─────────────────────────────

    private static void EmitJavaScript(StringBuilder sb, CodeEntity e, string ind, Func<string, string> name)
    {
        var inner = ind + "    ";
        switch (e.EntityType)
        {
            case CodeEntityType.Enum:
                // JS has no enum → a frozen object of incrementing values.
                var vals = string.Join(", ", e.EnumValues.Select((v, i) => $"{v}: {i}"));
                sb.AppendLine($"{ind}export const {e.Name} = Object.freeze({{ {vals} }});");
                break;
            case CodeEntityType.Function:
            {
                var (ins, ret) = FuncSig(e);
                var ps = string.Join(", ", ins.Select(p => p.Name));
                sb.AppendLine($"{ind}export function {e.Name}({ps}) {{");
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
                // JS has no interfaces → a class shell (duck-typed) with a note.
                if (iface) sb.AppendLine($"{ind}// interface {e.Name} — JS has no interfaces; implement by duck typing.");
                var ext = string.IsNullOrEmpty(e.BaseClassId) ? "" : " extends " + name(e.BaseClassId);
                sb.AppendLine($"{ind}export class {e.Name}{ext} {{");
                if (!iface)
                    foreach (var f in e.Fields)
                        sb.AppendLine($"{inner}{(f.IsStatic ? "static " : "")}{f.Name};");   // bare field (bodies reference the plain name)
                foreach (var m in e.Methods)
                {
                    var ps = string.Join(", ", m.Parameters.Select(p => p.Name));
                    if (!iface && m.Kind == MethodKind.Constructor)
                    {
                        sb.AppendLine($"{inner}constructor({ps}) {{");
                        TryBracedBody($"{e.Id}#{m.Id}", sb, inner + "    ");
                        sb.AppendLine($"{inner}}}");
                        continue;
                    }
                    if (!iface && m.Kind == MethodKind.Destructor) { sb.AppendLine($"{inner}// destructor — no JS equivalent (consider dispose())"); continue; }
                    sb.AppendLine($"{inner}{(m.IsStatic ? "static " : "")}{m.Name}({ps}) {{");
                    if (!TryBracedBody($"{e.Id}#{m.Id}", sb, inner + "    ") && m.ReturnType.Trim() is not ("void" or ""))
                        sb.AppendLine($"{inner}    throw new Error(\"Not implemented\");");
                    sb.AppendLine($"{inner}}}");
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
                sb.AppendLine($"{ind}fun {(e.IsEntryPoint ? "main" : e.Name)}({ps}){Ret(ret)} {{");
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
                // Params use the `_` (no argument label) style so calls match the label-free pseudocode: f(a, b).
                var ps = string.Join(", ", ins.Select(p => $"_ {p.Name}: {p.DataType}"));
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
                    // `_` = no argument label, so callers write obj.method(a, b) matching the label-free pseudocode.
                    var ps = string.Join(", ", m.Parameters.Select(p => $"_ {p.Name}: {p.DataType}"));
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
                sb.AppendLine($"{ind}func {(e.IsEntryPoint ? "main" : e.Name)}({ps}){r} {{");
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
                sb.AppendLine($"{ind}pub fn {(e.IsEntryPoint ? "main" : e.Name)}({ps}){Ret(ret)} {{");
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
                // Methods that mutate a field need `&mut self`; a pure reader keeps `&self`. We don't know which,
                // so a setter-shaped method (assigns a field in its body) gets `&mut self`, else `&self`.
                var nonDtor = e.Methods.Where(m => m.Kind != MethodKind.Destructor).ToList();
                if (nonDtor.Count > 0)
                {
                    sb.AppendLine($"{ind}impl {e.Name} {{");
                    foreach (var m in nonDtor)
                    {
                        if (m.Kind == MethodKind.Constructor)
                        {
                            var cps = string.Join(", ", m.Parameters.Select(p => $"{p.Name}: {p.DataType}"));
                            sb.AppendLine($"{inner}pub fn new({cps}) -> Self {{");
                            if (!TryRustBody($"{e.Id}#{m.Id}", sb, inner + "    ")) sb.AppendLine($"{inner}    unimplemented!()");
                            sb.AppendLine($"{inner}}}");
                            continue;
                        }
                        var self = MutatesSelf($"{e.Id}#{m.Id}", e.Fields) ? "&mut self" : "&self";
                        var ps = string.Join(", ", new[] { self }.Concat(m.Parameters.Select(p => $"{p.Name}: {p.DataType}")));
                        sb.AppendLine($"{inner}{(m.Visibility == CodeVisibility.Public ? "pub " : "")}fn {m.Name}({ps}){Ret(m.ReturnType)} {{");
                        if (!TryRustBody($"{e.Id}#{m.Id}", sb, inner + "    ")) sb.AppendLine($"{inner}    unimplemented!()");
                        sb.AppendLine($"{inner}}}");
                    }
                    sb.AppendLine($"{ind}}}");
                }
                // Rust destructors ARE the Drop trait — a SEPARATE impl block, never nested inside `impl {e.Name}`.
                if (e.Methods.FirstOrDefault(m => m.Kind == MethodKind.Destructor) is { } dtor)
                {
                    sb.AppendLine($"{ind}impl Drop for {e.Name} {{");
                    sb.AppendLine($"{inner}fn drop(&mut self) {{");
                    TryRustBody($"{e.Id}#{dtor.Id}", sb, inner + "    ");
                    sb.AppendLine($"{inner}}}");
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
            case Models.NsBlockKind.Subroutine:
            case Models.NsBlockKind.Jump:
                var s = (b.Text ?? "").Trim();
                AppendStmtLines(sb, ind, s);
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
