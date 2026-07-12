using System.Collections.Generic;
using StructoFox.Core.Models;

namespace StructoFox.Core;

/// <summary>
/// Cascades a readable-key rename across a project. When an entity (or one of its methods) changes key, its diagram
/// files are moved and every cross-document reference is rewritten: base/implements/instance/namespace in other
/// entities, board card positions + relations, board target keys, and structogram subroutine LinkKeys. Keeps files
/// and references human-readable and consistent. The entity's OWN entity file is written by the caller.
/// </summary>
public static class ProjectKeyRenameService
{
    /// <summary>Renames an entity key everywhere except its own entity file. <paramref name="methodKeys"/> are the
    /// entity's current method sub-keys, so its per-method diagrams move with it. When <paramref name="updateReferrers"/>
    /// is false, only the entity's OWN diagram files move — every reference keeps pointing at the old key (the
    /// "leave links" choice); the caller may then clear them via <see cref="ProjectUsageService.RemoveReferences"/>.</summary>
    public static void RenameEntity(string projFolder, string oldKey, string newKey, IEnumerable<string> methodKeys, bool updateReferrers = true)
    {
        if (oldKey == newKey) return;

        // 1. The entity's own diagrams — the entity-level one plus one per method.
        FlowChartService.Rename(projFolder, oldKey, newKey);
        StructogramService.Rename(projFolder, oldKey, newKey);
        foreach (var m in methodKeys)
        {
            FlowChartService.Rename(projFolder, NameKeys.JoinMethodKey(oldKey, m), NameKeys.JoinMethodKey(newKey, m));
            StructogramService.Rename(projFolder, NameKeys.JoinMethodKey(oldKey, m), NameKeys.JoinMethodKey(newKey, m));
        }

        if (!updateReferrers) return;

        // 2. References held by every other entity.
        foreach (var type in CodeEntityService.EntityTypes)
            foreach (var e in CodeEntityService.LoadAll(projFolder, type))
            {
                bool changed = false;
                if (e.BaseClassId  == oldKey) { e.BaseClassId  = newKey; changed = true; }
                if (e.InstanceOfId == oldKey) { e.InstanceOfId = newKey; changed = true; }
                if (e.Namespace    == oldKey) { e.Namespace    = newKey; changed = true; }
                for (int i = 0; i < e.ImplementsIds.Count; i++)
                    if (e.ImplementsIds[i] == oldKey) { e.ImplementsIds[i] = newKey; changed = true; }

                // Type names used as free text: field/port/parameter/return types that NAME this entity.
                foreach (var f in e.Fields)
                    { var n = NameKeys.RemapType(f.DataType, oldKey, newKey); if (n != f.DataType) { f.DataType = n; changed = true; } }
                foreach (var port in e.Ports)
                    { var n = NameKeys.RemapType(port.DataType, oldKey, newKey); if (n != port.DataType) { port.DataType = n; changed = true; } }
                foreach (var m in e.Methods)
                {
                    var rn = NameKeys.RemapType(m.ReturnType, oldKey, newKey); if (rn != m.ReturnType) { m.ReturnType = rn; changed = true; }
                    foreach (var p in m.Parameters)
                        { var pn = NameKeys.RemapType(p.DataType, oldKey, newKey); if (pn != p.DataType) { p.DataType = pn; changed = true; } }
                }

                if (changed) CodeEntityService.Save(projFolder, type, e);
            }

        // 3. Board card positions (keyed by entity key) + relations.
        foreach (var boardId in CodeBoardDataService.AllBoardIds(projFolder))
        {
            var d = CodeBoardDataService.Load(projFolder, boardId);
            bool changed = false;
            if (d.Positions.TryGetValue(oldKey, out var pos)) { d.Positions.Remove(oldKey); d.Positions[newKey] = pos; changed = true; }
            foreach (var r in d.Relations)
            {
                if (r.FromId == oldKey) { r.FromId = newKey; changed = true; }
                if (r.ToId   == oldKey) { r.ToId   = newKey; changed = true; }
            }
            if (changed) CodeBoardDataService.Save(projFolder, boardId, d);
        }

        // 4. Board target keys (entity, or entity#method).
        var boards = CodeBoardRegistryService.Load(projFolder);
        bool regChanged = false;
        foreach (var b in boards)
        {
            if (b.TargetKey == oldKey) { b.TargetKey = newKey; regChanged = true; }
            else if (b.TargetKey.StartsWith(oldKey + NameKeys.MethodSep)) { b.TargetKey = newKey + b.TargetKey[oldKey.Length..]; regChanged = true; }
        }
        if (regChanged) CodeBoardRegistryService.Save(projFolder, boards);

        // 5. Subroutine LinkKeys in every structogram (they reference a Function entity key).
        foreach (var fileKey in StructogramService.AllFileKeys(projFolder))
        {
            var sd = StructogramService.Load(projFolder, fileKey);
            if (ReplaceLinkKeys(sd.Root, oldKey, newKey))
                StructogramService.Save(projFolder, fileKey, sd);
        }
    }

    /// <summary>Renames one method sub-key of an entity: moves its diagrams and fixes any board target keys.</summary>
    public static void RenameMethod(string projFolder, string entityKey, string oldMethodKey, string newMethodKey)
    {
        if (oldMethodKey == newMethodKey) return;
        var oldFull = NameKeys.JoinMethodKey(entityKey, oldMethodKey);
        var newFull = NameKeys.JoinMethodKey(entityKey, newMethodKey);
        FlowChartService.Rename(projFolder, oldFull, newFull);
        StructogramService.Rename(projFolder, oldFull, newFull);

        var boards = CodeBoardRegistryService.Load(projFolder);
        bool changed = false;
        foreach (var b in boards)
            if (b.TargetKey == oldFull) { b.TargetKey = newFull; changed = true; }
        if (changed) CodeBoardRegistryService.Save(projFolder, boards);
    }

    static bool ReplaceLinkKeys(List<NsBlock> blocks, string oldKey, string newKey)
    {
        bool changed = false;
        foreach (var b in blocks)
        {
            if (b.LinkKey == oldKey) { b.LinkKey = newKey; changed = true; }
            if (ReplaceLinkKeys(b.Body, oldKey, newKey)) changed = true;
            if (ReplaceLinkKeys(b.Else, oldKey, newKey)) changed = true;
            foreach (var arm in b.Arms) if (ReplaceLinkKeys(arm.Body, oldKey, newKey)) changed = true;
        }
        return changed;
    }
}
