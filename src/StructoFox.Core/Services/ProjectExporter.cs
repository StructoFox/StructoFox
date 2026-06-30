using System.IO;
using System.Text;
using StructoFox.Core.Models;

namespace StructoFox.Core;

/// <summary>
/// Turns a set of code entities into a READY-TO-BUILD project tree (source files + the project/build files an
/// IDE expects), so the output opens and compiles with minimal steps. Builds on the deterministic
/// <see cref="CodeExportService"/> for the source; adds the C# `.csproj` / C++ `CMakeLists.txt` scaffolding.
/// (The KI-Codegen plugin fills method bodies via the AI before calling this.)
/// </summary>
public static class ProjectExporter
{
    /// <summary>The files of a buildable project as (relative path, content) pairs — caller writes them.</summary>
    public static IReadOnlyList<(string path, string content)> Build(
        IReadOnlyList<CodeEntity> entities, ExportLanguage lang, string projectName, string? projFolder = null)
    {
        var name = Sanitize(projectName);
        var src  = CodeExportService.Generate(entities, lang, projFolder);
        var files = new List<(string, string)>();

        switch (lang)
        {
            case ExportLanguage.CSharp:
                // Exe only when there's a conventional static Main (else the build would have no entry point);
                // otherwise a library, which always compiles.
                bool exe = entities.Any(e => e.IsEntryPoint && e.EntityType == CodeEntityType.Function
                                             && string.Equals(e.Name, "Main", StringComparison.Ordinal));
                files.Add(($"{name}/Program.cs", src));
                files.Add(($"{name}/{name}.csproj", CsProj(exe)));
                break;

            case ExportLanguage.Cpp:
                files.Add(($"{name}/main.cpp", CppSource(src)));
                files.Add(($"{name}/CMakeLists.txt", CMake(name)));
                break;

            default:   // other languages: a single source file (no build scaffolding yet)
                files.Add(($"{name}/main.{CodeExportService.FileExtension(lang)}", src));
                break;
        }
        return files;
    }

    /// <summary>Writes the buildable project under <paramref name="outDir"/>.</summary>
    public static void Write(string outDir, IReadOnlyList<CodeEntity> entities, ExportLanguage lang,
                             string projectName, string? projFolder = null)
    {
        foreach (var (path, content) in Build(entities, lang, projectName, projFolder))
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

    // A library always compiles (no main needed). Switch to add_executable + a main() once the entry is real.
    static string CMake(string name) => $"""
        cmake_minimum_required(VERSION 3.15)
        project({name} CXX)
        set(CMAKE_CXX_STANDARD 17)
        set(CMAKE_CXX_STANDARD_REQUIRED ON)
        add_library({name} main.cpp)
        """;

    static string Sanitize(string s)
    {
        var cleaned = new string((s ?? "").Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        return cleaned.Length == 0 ? "GeneratedProject" : cleaned;
    }
}
