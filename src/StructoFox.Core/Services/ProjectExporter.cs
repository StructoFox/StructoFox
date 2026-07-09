using System.IO;
using System.Text;
using StructoFox.Core.Models;

namespace StructoFox.Core;

/// <summary>
/// Turns a set of code entities into a READY-TO-BUILD project tree (source files + the project/build files an
/// IDE expects), so the output opens and compiles with minimal steps. Builds on the deterministic
/// <see cref="CodeExportService"/> for the source; adds per-language build scaffolding: C# .csproj, C/C++
/// CMakeLists.txt, package.json/tsconfig (TS/JS), pyproject.toml (Python), go.mod (Go), Cargo.toml (Rust),
/// Gradle (Kotlin). The remaining languages emit a single source file.
/// (The KI-Codegen plugin fills method bodies via the AI before calling this.)
/// </summary>
public static class ProjectExporter
{
    /// <summary>The files of a buildable project as (relative path, content) pairs — caller writes them.
    /// When <paramref name="outDir"/> already IS this project's folder (its name matches, or it already holds
    /// the project's build file), the files are rooted directly in it (overwrite in place) instead of nesting a
    /// fresh <c>&lt;name&gt;/</c> subfolder — so re-exporting into the project folder doesn't create name/name/.</summary>
    public static IReadOnlyList<(string path, string content)> Build(
        IReadOnlyList<CodeEntity> entities, ExportLanguage lang, string projectName, string? projFolder = null,
        string? outDir = null, bool multiFile = false)
    {
        var name = Sanitize(projectName);
        var files = new List<(string, string)>();

        // Root the output in a fresh "<name>/" unless we're writing into the project's own folder.
        string prefix = WriteInPlace(outDir, name, lang) ? "" : name + "/";
        string P(string rel) => prefix + rel;

        // A function flagged as the entry point → build an executable (else a library, which always compiles).
        bool exe = entities.Any(e => e.IsEntryPoint && e.EntityType == CodeEntityType.Function);

        // Multi-file (one file per type + a functions/header file) for the languages that support it.
        var multi = multiFile ? CodeExportService.GenerateFiles(entities, lang, name, projFolder) : null;
        if (multi is not null)
        {
            foreach (var (path, content) in multi) files.Add((P(path), content));
            switch (lang)
            {
                case ExportLanguage.CSharp: files.Add((P($"{name}.csproj"), CsProj(exe))); break;   // SDK csproj auto-globs *.cs
                case ExportLanguage.C:      files.Add((P("CMakeLists.txt"), CMakeC(name, exe,
                    string.Join(" ", multi.Where(f => f.path.EndsWith(".c")).Select(f => f.path))))); break;
                case ExportLanguage.Cpp:    files.Add((P("CMakeLists.txt"), CMakeCpp(name, exe))); break;   // only main.cpp compiles (classes are header-only)
                case ExportLanguage.Go:     files.Add((P("go.mod"), GoMod(name))); break;   // all .go files share one package (folder)
                case ExportLanguage.TypeScript:
                    files.Add((P("package.json"), NodePkg(name, ts: true, "index.js")));
                    files.Add((P("tsconfig.json"), TsConfig));
                    break;
                case ExportLanguage.Python: files.Add((P("pyproject.toml"), PyProject(name))); break;
                case ExportLanguage.Rust:   files.Add((P("Cargo.toml"), CargoToml(name))); break;   // crate root stays src/main.rs
                case ExportLanguage.JavaScript: files.Add((P("package.json"), NodePkg(name, ts: false, "index.js"))); break;
                case ExportLanguage.Kotlin:
                    files.Add((P("build.gradle.kts"), GradleKts()));
                    files.Add((P("settings.gradle.kts"), $"rootProject.name = \"{Lower(name)}\"\n"));
                    break;
                // Swift: no build file — compile the flat sources with `swiftc *.swift` (main.swift is the entry).
                // PHP: no build file — index.php's spl_autoload_register wires the class files at runtime.
            }
            return files;
        }

        var src = CodeExportService.Generate(entities, lang, projFolder);
        switch (lang)
        {
            case ExportLanguage.CSharp:
                files.Add((P("Program.cs"), src));
                files.Add((P($"{name}.csproj"), CsProj(exe)));
                break;

            case ExportLanguage.Cpp:
                files.Add((P("main.cpp"), CppSource(src)));
                files.Add((P("CMakeLists.txt"), CMakeCpp(name, exe)));
                break;

            case ExportLanguage.C:
                files.Add((P("main.c"), CSource(src)));
                files.Add((P("CMakeLists.txt"), CMakeC(name, exe, "main.c")));
                break;

            case ExportLanguage.TypeScript:
                files.Add((P("src/main.ts"), src));
                files.Add((P("package.json"), NodePkg(name, ts: true, "main.js")));
                files.Add((P("tsconfig.json"), TsConfig));
                break;

            case ExportLanguage.JavaScript:
                files.Add((P("src/main.js"), src));
                files.Add((P("package.json"), NodePkg(name, ts: false, "main.js")));
                break;

            case ExportLanguage.Python:
                files.Add((P("main.py"), src));
                files.Add((P("pyproject.toml"), PyProject(name)));
                break;

            case ExportLanguage.Go:
                // Go needs a package clause first; `main` package + `func main` makes a runnable command.
                files.Add((P("main.go"), $"package {(exe ? "main" : Lower(name))}\n\n" + src));
                files.Add((P("go.mod"), GoMod(name)));
                break;

            case ExportLanguage.Rust:
                files.Add((P("src/main.rs"), src));
                files.Add((P("Cargo.toml"), CargoToml(name)));
                break;

            case ExportLanguage.Kotlin:
                files.Add((P("src/main/kotlin/Main.kt"), src));
                files.Add((P("build.gradle.kts"), GradleKts()));
                files.Add((P("settings.gradle.kts"), $"rootProject.name = \"{Lower(name)}\"\n"));
                break;

            default:   // Java, Swift, PHP, Verse → single source file (build scaffolding not mapped yet)
                files.Add((P($"main.{CodeExportService.FileExtension(lang)}"), src));
                break;
        }
        return files;
    }

    // True when outDir should be treated as the project root itself (write in place / overwrite), rather than
    // getting a nested "<name>/" subfolder: either its folder name matches, or it already contains this
    // project's build file from a previous export.
    static bool WriteInPlace(string? outDir, string name, ExportLanguage lang)
    {
        if (string.IsNullOrEmpty(outDir)) return false;
        if (string.Equals(new DirectoryInfo(outDir.TrimEnd('/', '\\')).Name, name, StringComparison.OrdinalIgnoreCase))
            return true;
        string marker = lang switch
        {
            ExportLanguage.CSharp                          => $"{name}.csproj",
            ExportLanguage.Cpp or ExportLanguage.C         => "CMakeLists.txt",
            ExportLanguage.TypeScript or ExportLanguage.JavaScript => "package.json",
            ExportLanguage.Python                          => "pyproject.toml",
            ExportLanguage.Go                              => "go.mod",
            ExportLanguage.Rust                            => "Cargo.toml",
            ExportLanguage.Kotlin                          => "build.gradle.kts",
            _                                              => $"main.{CodeExportService.FileExtension(lang)}",
        };
        return File.Exists(Path.Combine(outDir, marker));
    }

    /// <summary>The subfolder that holds the previous versions of files this export replaced.</summary>
    public const string ReplacedFolder = "Replaced";

    /// <summary>Before writing, moves StructoFox's OWN existing generated files (build files + entry sources of
    /// every language) out of the way into a <c>Replaced/</c> subfolder — so re-exporting doesn't silently
    /// overwrite files the user may have edited, and re-exporting in a DIFFERENT language doesn't leave a stale
    /// mix. Only well-known generated filenames are moved (never arbitrary user files). <c>Replaced/</c> is
    /// cleared first, so it only ever holds the MOST RECENT replacement — overwritten on the next run. Returns
    /// the relative paths that were moved, so the caller can tell the user what got replaced.</summary>
    public static IReadOnlyList<string> BackupReplaced(string outDir, string projectName, ExportLanguage lang)
    {
        var moved = new List<string>();
        if (string.IsNullOrEmpty(outDir)) return moved;
        var name = Sanitize(projectName);
        var root = WriteInPlace(outDir, name, lang) ? outDir : Path.Combine(outDir, name);
        if (!Directory.Exists(root)) return moved;

        var replacedDir = Path.Combine(root, ReplacedFolder);
        try { if (Directory.Exists(replacedDir)) Directory.Delete(replacedDir, recursive: true); } catch { }

        // The fixed build/entry files (covers subpaths like src/main.rs), plus every generated source file
        // anywhere under the root (recursive — Java places classes in package subfolders). We sweep ALL
        // generated source extensions (not just the current language's) so switching language — e.g. C → Java —
        // also clears the previous language's per-type files instead of leaving a stale mix.
        var rels = KnownArtifacts(name).ToList();
        var exts = GeneratedSourceExts.Append("." + CodeExportService.FileExtension(lang));
        foreach (var ext in exts.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var f in Directory.EnumerateFiles(root, "*" + ext, SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, f);
                // Never touch our own Replaced/ backup, or dependency/build/VCS trees (a Node project may hold
                // thousands of .ts/.js under node_modules — those are NOT ours to move).
                if (rel.Split('/', '\\').Any(seg =>
                        seg.Equals(ReplacedFolder, StringComparison.OrdinalIgnoreCase) ||
                        seg.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                        seg.Equals("dist", StringComparison.OrdinalIgnoreCase) ||
                        seg.Equals(".git", StringComparison.OrdinalIgnoreCase)))
                    continue;
                rels.Add(rel);
            }

        foreach (var rel in rels.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var full = Path.Combine(root, rel);
            if (!File.Exists(full)) continue;
            try
            {
                var dest = Path.Combine(replacedDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(full, dest, overwrite: true);
                moved.Add(rel.Replace('\\', '/'));
            }
            catch { /* locked → leave it; the write below overwrites it in place */ }
        }
        return moved;
    }

    // Every generated source/header extension across the multi-file languages — swept on cleanup regardless of
    // the current language, so a language switch leaves no stale per-type files behind.
    static readonly string[] GeneratedSourceExts = { ".cs", ".c", ".h", ".cpp", ".hpp", ".java", ".go", ".php", ".ts", ".py", ".rs", ".js", ".kt", ".swift" };

    // Every relative path Build can emit, across all languages — the set we may safely delete when overwriting.
    static IEnumerable<string> KnownArtifacts(string name) => new[]
    {
        "Program.cs", $"{name}.csproj",              // C#
        "CMakeLists.txt", "main.cpp", "main.c",       // C / C++
        "main.py", "pyproject.toml",                  // Python
        "main.go", "go.mod",                          // Go
        "Cargo.toml", Path.Combine("src", "main.rs"), // Rust
        "package.json", "tsconfig.json",              // TS / JS
        Path.Combine("src", "main.ts"), Path.Combine("src", "main.js"),
        "build.gradle.kts", "settings.gradle.kts",    // Kotlin
        Path.Combine("src", "main", "kotlin", "Main.kt"),
        "main.java", "main.swift", "main.php", "main.verse",   // single-file languages
    };

    /// <summary>Writes the buildable project under <paramref name="outDir"/>.</summary>
    public static void Write(string outDir, IReadOnlyList<CodeEntity> entities, ExportLanguage lang,
                             string projectName, string? projFolder = null, bool multiFile = false)
    {
        BackupReplaced(outDir, projectName, lang);
        foreach (var (path, content) in Build(entities, lang, projectName, projFolder, outDir, multiFile))
        {
            var full = Path.Combine(outDir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }

    static string CsProj(bool exe) => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>{(exe ? "Exe" : "Library")}</OutputType>
            <TargetFramework>net8.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;

    // Generated C++ is a declarations skeleton; prepend common headers so it compiles as a library unit.
    static string CppSource(string generated) =>
        "#include <iostream>\n#include <string>\n#include <vector>\n#include <map>\n\n" + generated;

    // Executable when there's a real `int main`, otherwise a library (always compiles).
    static string CMakeCpp(string name, bool exe) => $"""
        cmake_minimum_required(VERSION 3.15)
        project({name} CXX)
        set(CMAKE_CXX_STANDARD 17)
        set(CMAKE_CXX_STANDARD_REQUIRED ON)
        {(exe ? "add_executable" : "add_library")}({name} main.cpp)
        """;

    // Generated C uses malloc/printf/bool/strings; prepend the standard headers so it compiles.
    static string CSource(string generated) =>
        "#include <stdio.h>\n#include <stdlib.h>\n#include <stdbool.h>\n#include <string.h>\n\n" + generated;

    // C: an executable when there's a real `main`, otherwise a library. <paramref name="sources"/> is the
    // space-separated list of .c files (just "main.c" for a single-file export; several for multi-file).
    static string CMakeC(string name, bool exe, string sources) => $"""
        cmake_minimum_required(VERSION 3.15)
        project({name} C)
        set(CMAKE_C_STANDARD 11)
        set(CMAKE_C_STANDARD_REQUIRED ON)
        {(exe ? "add_executable" : "add_library")}({name} {sources})
        """;

    // ── Manifests for the "single source file + project file" languages ───────

    // <paramref name="mainJs"/> is the compiled/runnable entry file the "main" field points at — main.js for a
    // single-file export, index.js for a multi-file one (which uses an index bootstrap).
    static string NodePkg(string name, bool ts, string mainJs) => ts ? $$"""
        {
          "name": "{{Lower(name)}}",
          "version": "0.1.0",
          "type": "module",
          "main": "dist/{{mainJs}}",
          "scripts": { "build": "tsc" },
          "devDependencies": { "typescript": "^5.4.0" }
        }
        """ : $$"""
        {
          "name": "{{Lower(name)}}",
          "version": "0.1.0",
          "type": "module",
          "main": "src/{{mainJs}}"
        }
        """;

    static string TsConfig => """
        {
          "compilerOptions": {
            "target": "ES2020",
            "module": "ES2020",
            "moduleResolution": "node",
            "rootDir": "src",
            "outDir": "dist",
            "strict": true,
            "esModuleInterop": true
          },
          "include": ["src"]
        }
        """;

    static string PyProject(string name) => $"""
        [project]
        name = "{Lower(name)}"
        version = "0.1.0"

        [build-system]
        requires = ["setuptools>=61"]
        build-backend = "setuptools.build_meta"
        """;

    static string GoMod(string name) => $"module {Lower(name)}\n\ngo 1.21\n";

    static string CargoToml(string name) => $"""
        [package]
        name = "{Lower(name)}"
        version = "0.1.0"
        edition = "2021"

        [[bin]]
        name = "{Lower(name)}"
        path = "src/main.rs"
        """;

    static string GradleKts() => """
        plugins {
            kotlin("jvm") version "1.9.24"
            application
        }
        repositories { mavenCentral() }
        application { mainClass.set("MainKt") }
        """;

    // Package/crate names must be lowercase (npm, cargo, go module conventions).
    static string Lower(string name) => name.ToLowerInvariant();

    static string Sanitize(string s)
    {
        var cleaned = new string((s ?? "").Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        return cleaned.Length == 0 ? "GeneratedProject" : cleaned;
    }
}
