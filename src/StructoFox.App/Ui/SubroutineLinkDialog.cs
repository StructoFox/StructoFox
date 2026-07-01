using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using StructoFox.Core;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// The window shown when a subroutine block has nothing behind it yet: instead of just asking for a
/// name, it lets the user either pick an existing function or create a new one — in both cases within
/// a chosen namespace (used to filter the pick-list and as the create target). Returns the linked
/// function's id, or null on cancel. A new function whose name already lives in the same namespace
/// triggers a "there is already one" warning before anything is saved. Reuse beats re-inventing.
/// </summary>
public static class SubroutineLinkDialog
{
    /// <summary>Resolves a subroutine RefId to a display name: a function's name, or "Class.method" for a
    /// method key ("classId#methodId"). Falls back to the raw id if it can't be resolved.</summary>
    public static string RefName(string projFolder, string refId)
    {
        if (string.IsNullOrEmpty(refId)) return "";
        var hash = refId.IndexOf('#');
        if (hash >= 0)
        {
            var cls = CodeEntityService.LoadAll(projFolder, "Class").FirstOrDefault(c => c.Id == refId[..hash]);
            var m   = cls?.Methods.FirstOrDefault(x => x.Id == refId[(hash + 1)..]);
            return cls is null ? refId : $"{cls.Name}.{m?.Name ?? "?"}";
        }
        return CodeEntityService.LoadAll(projFolder, "Function").FirstOrDefault(f => f.Id == refId)?.Name ?? refId;
    }

    public static Task<string?> Show(Window owner, string projFolder, string suggestedName, string? excludeKey = null)
    {
        // Callable targets exclude the entry point (you don't call main) and the current diagram itself
        // (a subroutine can't be its own body — that would recurse forever).
        var funcs   = CodeEntityService.LoadAll(projFolder, "Function")
            .Where(f => !f.IsEntryPoint && f.Id != excludeKey).ToList();
        var classes = CodeEntityService.LoadAll(projFolder, "Class").ToList();
        var hasExisting = funcs.Count > 0 || classes.Any(c => c.Methods.Count > 0);

        var dlg = new Window
        {
            Title = Loc.S("Sub_LinkTitle"), Width = 440, Height = 540,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        Ui.ThemeWindow(dlg);

        // Namespace picker (id-based), shared by both modes. "(none)" carries the empty id.
        var nsCombo = Ui.Combo();
        nsCombo.Items.Add(new ComboItem(Loc.S("Sec_NsNone"), ""));
        var nsFull = NamespaceService.FullNames(projFolder);
        foreach (var n in CodeEntityService.LoadAll(projFolder, "Namespace")
                     .OrderBy(n => nsFull.GetValueOrDefault(n.Id, n.Name), StringComparer.OrdinalIgnoreCase))
            nsCombo.Items.Add(new ComboItem(nsFull.GetValueOrDefault(n.Id, n.Name), n.Id));
        nsCombo.SelectedIndex = 0;
        string NsId() => (nsCombo.SelectedItem as ComboItem)?.Id ?? "";

        var pickExisting = new RadioButton { Content = Loc.S("Sub_PickExisting"), IsChecked = hasExisting, IsEnabled = hasExisting };
        var createNew    = new RadioButton { Content = Loc.S("Sub_CreateNew"),    IsChecked = !hasExisting };

        // A tree: functions as leaves, classes expandable with their methods (leaf = classId#methodId).
        var tree = new TreeView { MinHeight = 200 };
        Ui.Theme(tree, TemplatedControl.BackgroundProperty,  "ControlBgBrush");
        Ui.Theme(tree, TemplatedControl.ForegroundProperty,  "SidebarTextBrush");
        Ui.Theme(tree, TemplatedControl.BorderBrushProperty, "ControlBorderBrush");

        var nameBox = new TextBox { Text = suggestedName, MinWidth = 320 };
        Ui.Theme(nameBox, TextBox.BackgroundProperty,  "InputBgBrush");
        Ui.Theme(nameBox, TextBox.ForegroundProperty,  "SidebarTextBrush");
        Ui.Theme(nameBox, TextBox.BorderBrushProperty, "ControlBorderBrush");

        // Fills the tree with the functions + classes (→ methods) of the selected namespace.
        void RefillTree()
        {
            var nsId = NsId();
            tree.Items.Clear();
            TreeViewItem Item(string header, string? tag)
            {
                var it = new TreeViewItem { Header = header, Tag = tag };
                Ui.Theme(it, TemplatedControl.ForegroundProperty, "SidebarTextBrush");   // Fluent items don't inherit
                return it;
            }
            foreach (var f in funcs.Where(f => f.Namespace == nsId).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                tree.Items.Add(Item("ƒ  " + f.Name, f.Id));
            foreach (var c in classes.Where(c => c.Namespace == nsId).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                var ci = Item("▸  " + c.Name, null); ci.IsExpanded = true;   // class node: not a target
                foreach (var m in c.Methods.Where(m => $"{c.Id}#{m.Id}" != excludeKey).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                    ci.Items.Add(Item(c.Name + "." + m.Name, $"{c.Id}#{m.Id}"));
                tree.Items.Add(ci);
            }
        }
        // Greys out whichever mode isn't active, so it's obvious which input matters.
        void SyncEnabled()
        {
            tree.IsEnabled    = pickExisting.IsChecked == true;
            nameBox.IsEnabled = createNew.IsChecked == true;
        }
        nsCombo.SelectionChanged += (_, _) => RefillTree();
        pickExisting.IsCheckedChanged += (_, _) => SyncEnabled();
        createNew.IsCheckedChanged    += (_, _) => SyncEnabled();
        RefillTree();
        SyncEnabled();

        var ok     = Ui.Btn(Loc.S("Common_OK"));     ok.IsDefault = true;
        var cancel = Ui.Btn(Loc.S("Common_Cancel")); cancel.IsCancel = true;
        cancel.Click += (_, _) => dlg.Close(null);

        ok.Click += async (_, _) =>
        {
            if (pickExisting.IsChecked == true)
            {
                if (tree.SelectedItem is TreeViewItem { Tag: string id }) dlg.Close(id);   // a function or a method leaf
                return;   // nothing selectable picked (e.g. a class node) → stay open
            }

            var name = nameBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name)) return;
            var nsId = NsId();

            // A same-named function already in this namespace: tell the user before creating a twin.
            var dup = funcs.FirstOrDefault(f => f.Namespace == nsId && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (dup is not null)
            {
                var res = await MessageDialog.Show(dlg, Loc.S("Sub_DuplicateMsg"), Loc.S("Sub_DuplicateTitle"), DialogButtons.YesNoCancel);
                if (res == DialogResult.Cancel) return;          // back to the dialog
                if (res == DialogResult.Yes) { dlg.Close(dup.Id); return; }   // use the existing one
                // No → fall through and create another anyway
            }

            var fn = new CodeEntity { Name = name, EntityType = CodeEntityType.Function, Namespace = nsId };
            CodeEntityService.Save(projFolder, "Function", fn);
            dlg.Close(fn.Id);
        };
        tree.DoubleTapped += (_, _) => { if (pickExisting.IsChecked == true && tree.SelectedItem is TreeViewItem { Tag: string id }) dlg.Close(id); };

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } };

        dlg.Content = new StackPanel
        {
            Margin = new(20), Spacing = 10,
            Children =
            {
                new TextBlock { Text = Loc.S("Sub_Namespace") },
                nsCombo,
                pickExisting,
                tree,
                createNew,
                nameBox,
                btnRow,
            },
        };

        return dlg.ShowDialog<string?>(owner);
    }
}
