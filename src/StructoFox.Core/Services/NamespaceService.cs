using StructoFox.Core.Models;

namespace StructoFox.Core;

/// <summary>
/// Resolves nested namespaces. A Namespace entity's <see cref="CodeEntity.Namespace"/> holds its
/// PARENT namespace's id, so namespaces form a tree whose full name is the dotted path from the root
/// (e.g. parent "App" + "Data" = "App.Data"). Cycles (a corrupted parent chain) are broken defensively.
/// Keeping this in one place stops every caller from re-deriving the same walk.
/// </summary>
public static class NamespaceService
{
    /// <summary>Maps each namespace id to its full dotted name, resolved through the parent chain.</summary>
    public static Dictionary<string, string> FullNames(IEnumerable<CodeEntity> namespaces)
    {
        var byId = namespaces.Where(n => !string.IsNullOrEmpty(n.Id))
                             .GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
        var cache = new Dictionary<string, string>();

        string Resolve(string id, HashSet<string> seen)
        {
            if (cache.TryGetValue(id, out var done)) return done;
            if (!byId.TryGetValue(id, out var n)) return "";
            var own = (n.Name ?? "").Trim();
            if (!seen.Add(id)) return own;                       // cycle guard: stop at the repeat
            var parent = n.Namespace;                            // parent namespace id (may be empty)
            var full = string.IsNullOrEmpty(parent) ? own : Combine(Resolve(parent, seen), own);
            cache[id] = full;
            return full;
        }

        foreach (var id in byId.Keys) Resolve(id, new());
        return cache;
    }

    /// <summary>Convenience overload that loads the project's namespaces first.</summary>
    public static Dictionary<string, string> FullNames(string projFolder) =>
        FullNames(CodeEntityService.LoadAll(projFolder, "Namespace"));

    // Joins a parent path and a leaf name with a dot, tolerating either side being empty.
    static string Combine(string parent, string name) =>
        string.IsNullOrEmpty(parent) ? name : string.IsNullOrEmpty(name) ? parent : parent + "." + name;

    /// <summary>The id itself plus every namespace that has it as an ancestor — i.e. the ids that must
    /// NOT be offered as its parent, since picking one would create a cycle.</summary>
    public static HashSet<string> SelfAndDescendants(IEnumerable<CodeEntity> namespaces, string id)
    {
        var list = namespaces.ToList();
        var result = new HashSet<string> { id };
        for (bool changed = true; changed; )
        {
            changed = false;
            foreach (var n in list)
                if (!result.Contains(n.Id) && !string.IsNullOrEmpty(n.Namespace) && result.Contains(n.Namespace))
                    changed |= result.Add(n.Id);
        }
        return result;
    }
}
