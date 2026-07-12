using System.Collections.Generic;
using System.Linq;
using StructoFox.Core.Models;

namespace StructoFox.Core;

/// <summary>
/// Finds — and can clear — every reference to an entity key across a project (base class, interface, instance-of,
/// namespace parent, board cards/relations, board target, structogram subroutine links). Used to warn before a
/// rename/delete ("still used by …") and to optionally drop the links. UI-free: returns structured data the App
/// localizes for display.
/// </summary>
public static class ProjectUsageService
{
    public enum UsageKind { BaseClass, Interface, InstanceOf, Namespace, Board, Subroutine, FieldType }

    public readonly record struct Usage(string Referrer, UsageKind Kind);

    /// <summary>Everything that references <paramref name="key"/>, for a "still used by …" hint.</summary>
    public static List<Usage> FindReferrers(string projFolder, string key)
    {
        var hits = new List<Usage>();

        foreach (var type in CodeEntityService.EntityTypes)
            foreach (var e in CodeEntityService.LoadAll(projFolder, type))
            {
                if (e.BaseClassId  == key) hits.Add(new(e.Name, UsageKind.BaseClass));
                if (e.InstanceOfId == key) hits.Add(new(e.Name, UsageKind.InstanceOf));
                if (e.Namespace    == key) hits.Add(new(e.Name, UsageKind.Namespace));
                if (e.ImplementsIds.Contains(key)) hits.Add(new(e.Name, UsageKind.Interface));

                // A field/port/parameter/return type that NAMES this entity as its type.
                bool asType = e.Fields.Any(f => NameKeys.TypeMentions(f.DataType, key))
                           || e.Ports.Any(p => NameKeys.TypeMentions(p.DataType, key))
                           || e.Methods.Any(m => NameKeys.TypeMentions(m.ReturnType, key)
                                              || m.Parameters.Any(p => NameKeys.TypeMentions(p.DataType, key)));
                if (asType) hits.Add(new(e.Name, UsageKind.FieldType));
            }

        var boards = CodeBoardRegistryService.Load(projFolder);
        foreach (var b in boards)
            if (b.TargetKey == key || b.TargetKey.StartsWith(key + NameKeys.MethodSep))
                hits.Add(new(b.Name, UsageKind.Board));
        foreach (var boardId in CodeBoardDataService.AllBoardIds(projFolder))
        {
            var d = CodeBoardDataService.Load(projFolder, boardId);
            if (d.Positions.ContainsKey(key) || d.Relations.Any(r => r.FromId == key || r.ToId == key))
            {
                var name = boards.FirstOrDefault(b => b.Id == boardId)?.Name ?? boardId;
                hits.Add(new(name, UsageKind.Board));
            }
        }

        foreach (var fileKey in StructogramService.AllFileKeys(projFolder))
        {
            var sd = StructogramService.Load(projFolder, fileKey);
            if (HasLink(sd.Root, key)) hits.Add(new(fileKey, UsageKind.Subroutine));
        }

        return hits.Distinct().ToList();
    }

    /// <summary>Removes every reference to <paramref name="key"/> (used for the "delete the links" choice and to
    /// keep the model clean when the entity itself is deleted).</summary>
    public static void RemoveReferences(string projFolder, string key)
    {
        foreach (var type in CodeEntityService.EntityTypes)
            foreach (var e in CodeEntityService.LoadAll(projFolder, type))
            {
                bool ch = false;
                if (e.BaseClassId  == key) { e.BaseClassId  = ""; ch = true; }
                if (e.InstanceOfId == key) { e.InstanceOfId = ""; ch = true; }
                if (e.Namespace    == key) { e.Namespace    = ""; ch = true; }
                if (e.ImplementsIds.RemoveAll(x => x == key) > 0) ch = true;
                if (ch) CodeEntityService.Save(projFolder, type, e);
            }

        foreach (var boardId in CodeBoardDataService.AllBoardIds(projFolder))
        {
            var d = CodeBoardDataService.Load(projFolder, boardId);
            bool ch = d.Positions.Remove(key);
            if (d.Relations.RemoveAll(r => r.FromId == key || r.ToId == key) > 0) ch = true;
            if (ch) CodeBoardDataService.Save(projFolder, boardId, d);
        }

        var boards = CodeBoardRegistryService.Load(projFolder);
        bool bch = false;
        foreach (var b in boards)
            if (b.TargetKey == key || b.TargetKey.StartsWith(key + NameKeys.MethodSep)) { b.TargetKey = ""; bch = true; }
        if (bch) CodeBoardRegistryService.Save(projFolder, boards);

        foreach (var fileKey in StructogramService.AllFileKeys(projFolder))
        {
            var sd = StructogramService.Load(projFolder, fileKey);
            if (ClearLinks(sd.Root, key)) StructogramService.Save(projFolder, fileKey, sd);
        }
    }

    static bool HasLink(List<NsBlock> blocks, string key) =>
        blocks.Any(b => b.LinkKey == key || HasLink(b.Body, key) || HasLink(b.Else, key) || b.Arms.Any(a => HasLink(a.Body, key)));

    static bool ClearLinks(List<NsBlock> blocks, string key)
    {
        bool changed = false;
        foreach (var b in blocks)
        {
            if (b.LinkKey == key) { b.LinkKey = ""; changed = true; }
            if (ClearLinks(b.Body, key)) changed = true;
            if (ClearLinks(b.Else, key)) changed = true;
            foreach (var a in b.Arms) if (ClearLinks(a.Body, key)) changed = true;
        }
        return changed;
    }
}
