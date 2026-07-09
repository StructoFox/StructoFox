using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
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
    /// <summary>What the picker returns: the linked target id plus an optional call form (assign-to variable,
    /// arguments, and — for a non-static method — the object instance to call it on), so the caller can label
    /// the node as "variable = instance.Name(args)".</summary>
    public sealed record SubResult(string Id, string AssignTo, string Args, string Instance = "");

    /// <summary>Builds the node label for a linked call. Functions and static methods are called by their
    /// qualified name (Class.method); a non-static method is called on the chosen instance (obj.method).
    /// Plain qualified name when there's no call form at all (backward-compatible).</summary>
    public static string CallText(string projFolder, SubResult r)
    {
        var (isMethod, isStatic, methodName, qualified) = Resolve(projFolder, r.Id);
        var assign = r.AssignTo.Trim();
        var args   = r.Args.Trim();

        // Instance method with a chosen object → call on the object, not on the class.
        var callee = qualified;
        if (isMethod && !isStatic && r.Instance.Trim() is { Length: > 0 } inst)
            callee = $"{inst}.{methodName}";

        if (assign.Length == 0 && args.Length == 0 && callee == qualified) return callee;
        var call = $"{callee}({args})";
        return assign.Length == 0 ? call : $"{assign} = {call}";
    }

    /// <summary>Resolves a RefId to (isMethod, isStatic, bare method name, fully-qualified display name).</summary>
    static (bool isMethod, bool isStatic, string methodName, string qualified) Resolve(string projFolder, string refId)
    {
        var qualified = RefName(projFolder, refId);
        var hash = refId.IndexOf('#');
        if (hash < 0) return (false, false, "", qualified);
        var cls = CodeEntityService.LoadAll(projFolder, "Class").FirstOrDefault(c => c.Id == refId[..hash]);
        var m   = cls?.Methods.FirstOrDefault(x => x.Id == refId[(hash + 1)..]);
        return (true, m?.IsStatic ?? false, m?.Name ?? "", qualified);
    }

    /// <summary>Resolves a subroutine RefId to a display name: a function's name, or "Class.method" for a
    /// method key ("classId#methodId"). Falls back to the raw id if it can't be resolved.</summary>
    public static string RefName(string projFolder, string refId)
    {
        if (string.IsNullOrEmpty(refId)) return "";
        var nsFull = NamespaceService.FullNames(projFolder);
        string Qualify(string nsId, string tail)
        {
            var ns = nsFull.GetValueOrDefault(nsId, "");
            return string.IsNullOrEmpty(ns) ? tail : $"{ns}.{tail}";
        }
        var hash = refId.IndexOf('#');
        if (hash >= 0)
        {
            var cls = CodeEntityService.LoadAll(projFolder, "Class").FirstOrDefault(c => c.Id == refId[..hash]);
            if (cls is null) return refId;
            var m = cls.Methods.FirstOrDefault(x => x.Id == refId[(hash + 1)..]);
            return Qualify(cls.Namespace, $"{cls.Name}.{m?.Name ?? "?"}");
        }
        var fn = CodeEntityService.LoadAll(projFolder, "Function").FirstOrDefault(f => f.Id == refId);
        return fn is null ? refId : Qualify(fn.Namespace, fn.Name);
    }

    public static Task<SubResult?> Show(Window owner, string projFolder, string suggestedName, string? excludeKey = null)
    {
        // Callable targets exclude the entry point (you don't call main) and the current diagram itself
        // (a subroutine can't be its own body — that would recurse forever).
        var funcs   = CodeEntityService.LoadAll(projFolder, "Function")
            .Where(f => !f.IsEntryPoint && f.Id != excludeKey).ToList();
        var classes = CodeEntityService.LoadAll(projFolder, "Class").ToList();
        var hasExisting = funcs.Count > 0 || classes.Any(c => c.Methods.Count > 0);

        var dlg = new Window
        {
            Title = Loc.S("Sub_LinkTitle"), Width = 460,
            // Grow with the content: the instance row appears only for non-static methods, so a fixed
            // height would clip it. Min height keeps the window from collapsing when it's hidden.
            SizeToContent = SizeToContent.Height, MinHeight = 540,
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
        // Optional call form applied to whatever target is chosen/created: "assignTo = instance.Name(args)".
        var assignBox    = new TextBox { MinWidth = 150, PlaceholderText = Loc.S("Sub_AssignHint") };
        var argsBox      = new TextBox { MinWidth = 150, PlaceholderText = Loc.S("Sub_ArgsHint") };
        var newObjectBox = new TextBox { PlaceholderText = Loc.S("Sub_InstanceHint"), IsVisible = false };
        foreach (var b in new[] { nameBox, assignBox, argsBox, newObjectBox })
        {
            Ui.Theme(b, TextBox.BackgroundProperty,  "InputBgBrush");
            Ui.Theme(b, TextBox.ForegroundProperty,  "SidebarTextBrush");
            Ui.Theme(b, TextBox.BorderBrushProperty, "ControlBorderBrush");
        }

        // Instances are namespace-neutral (only their TYPE has a namespace), so we list every Object of the
        // method's class, whatever namespace it sits in — plus a "＋ new object" sentinel that creates one.
        const string NewObj = "new";
        var objects = CodeEntityService.LoadAll(projFolder, "Object");
        var instanceCombo = Ui.Combo();
        string _instClassId = "";
        void RefillInstances(string classId)
        {
            _instClassId = classId;
            instanceCombo.Items.Clear();
            foreach (var o in objects.Where(o => o.InstanceOfId == classId).OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
                instanceCombo.Items.Add(new ComboItem(o.Name, o.Id));
            instanceCombo.Items.Add(new ComboItem(Loc.S("Sub_NewObject"), NewObj));
            instanceCombo.SelectedIndex = 0;
        }
        instanceCombo.SelectionChanged += (_, _) =>
            newObjectBox.IsVisible = (instanceCombo.SelectedItem as ComboItem)?.Id == NewObj;

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
                tree.Items.Add(Item($"ƒ  {f.Name}{SigOf(f.Id)}", f.Id));
            foreach (var c in classes.Where(c => c.Namespace == nsId).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                var ci = Item("▸  " + c.Name, null); ci.IsExpanded = true;   // class node: not a target
                // Only NORMAL methods are callable subprograms — constructors/destructors (default name
                // "Method") aren't call targets and would just show up as "Class.Method".
                foreach (var m in c.Methods.Where(m => m.Kind == MethodKind.Normal && $"{c.Id}#{m.Id}" != excludeKey)
                                           .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                    ci.Items.Add(Item($"{c.Name}.{m.Name}{SigOf($"{c.Id}#{m.Id}")}", $"{c.Id}#{m.Id}"));
                tree.Items.Add(ci);
            }
        }
        // The input parameters (name, type) of a leaf — a function's input ports, or a method's parameters.
        List<(string name, string type)> LeafParams(string refId)
        {
            var hash = refId.IndexOf('#');
            if (hash >= 0)
            {
                var cls = classes.FirstOrDefault(c => c.Id == refId[..hash]);
                var m   = cls?.Methods.FirstOrDefault(x => x.Id == refId[(hash + 1)..]);
                return m?.Parameters.Select(p => (p.Name, p.DataType)).ToList() ?? new();
            }
            var f = funcs.FirstOrDefault(x => x.Id == refId);
            return f?.Ports.Where(p => p.Direction == PortDirection.Input).Select(p => (p.Name, p.DataType)).ToList() ?? new();
        }

        // "(int value, ...)" for the tree label — "()" when there are no parameters.
        string SigOf(string refId) => "(" + string.Join(", ", LeafParams(refId).Select(p => $"{p.type} {p.name}")) + ")";

        // True for a picked leaf that is a non-static method (needs an object to call it on).
        bool IsInstanceMethod(string refId)
        {
            var hash = refId.IndexOf('#');
            if (hash < 0) return false;
            var cls = classes.FirstOrDefault(c => c.Id == refId[..hash]);
            var m   = cls?.Methods.FirstOrDefault(x => x.Id == refId[(hash + 1)..]);
            return m is { IsStatic: false };
        }

        // The instance field only matters when an instance method is the picked target.
        var instanceRow = new StackPanel { Spacing = 4, IsVisible = false, Children =
            { new TextBlock { Text = Loc.S("Sub_Instance"), TextWrapping = TextWrapping.Wrap }, instanceCombo, newObjectBox } };
        void SyncInstance()
        {
            bool instMethod = pickExisting.IsChecked == true
                && tree.SelectedItem is TreeViewItem { Tag: string id } && IsInstanceMethod(id);
            instanceRow.IsVisible = instMethod;
            if (instMethod && tree.SelectedItem is TreeViewItem { Tag: string tid })
                RefillInstances(tid[..tid.IndexOf('#')]);
        }

        // Resolves the chosen instance name for a picked method leaf; creates a new Object entity when the
        // "＋ new object" item is selected. Returns null when a new object needs a name (keep the dialog open).
        string? InstanceName(string refId)
        {
            if (!IsInstanceMethod(refId)) return "";
            if (instanceCombo.SelectedItem is not ComboItem sel) return "";
            if (sel.Id != NewObj) return sel.Name;   // an existing object → its variable name

            var nm = newObjectBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(nm)) return null;
            var obj = new CodeEntity { Name = nm, EntityType = CodeEntityType.Object, InstanceOfId = _instClassId, Namespace = "" };
            CodeEntityService.Save(projFolder, "Object", obj);
            objects.Add(obj);
            return nm;
        }

        // Greys out whichever mode isn't active, so it's obvious which input matters.
        void SyncEnabled()
        {
            tree.IsEnabled    = pickExisting.IsChecked == true;
            nameBox.IsEnabled = createNew.IsChecked == true;
            SyncInstance();
        }
        nsCombo.SelectionChanged += (_, _) => RefillTree();
        tree.SelectionChanged += (_, _) =>
        {
            SyncInstance();
            // Pre-fill the arguments field with the target's parameter TYPES as a hint of what to pass
            // (e.g. "int, int"), ready for the user to replace each with an actual value/variable.
            if (tree.SelectedItem is TreeViewItem { Tag: string id })
                argsBox.Text = string.Join(", ", LeafParams(id).Select(p => p.type));
        };
        pickExisting.IsCheckedChanged += (_, _) => SyncEnabled();
        createNew.IsCheckedChanged    += (_, _) => SyncEnabled();
        RefillTree();
        SyncEnabled();

        var ok     = Ui.Btn(Loc.S("Common_OK"));     ok.IsDefault = true;
        var cancel = Ui.Btn(Loc.S("Common_Cancel")); cancel.IsCancel = true;
        cancel.Click += (_, _) => dlg.Close(null);

        SubResult Result(string id, string instance) => new(id, assignBox.Text ?? "", argsBox.Text ?? "", instance);

        // Closes with the picked leaf, resolving (and possibly creating) its instance first. No-op when the
        // selection isn't a target or a new object still needs a name.
        void PickLeaf()
        {
            if (tree.SelectedItem is not TreeViewItem { Tag: string id }) return;
            var inst = InstanceName(id);
            if (inst is null) return;   // "new object" chosen but unnamed → stay open
            dlg.Close(Result(id, inst));
        }

        ok.Click += async (_, _) =>
        {
            if (pickExisting.IsChecked == true)
            {
                PickLeaf();   // function or method leaf (class node → no-op, stay open)
                return;
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
                if (res == DialogResult.Yes) { dlg.Close(Result(dup.Id, "")); return; }   // use the existing one
                // No → fall through and create another anyway
            }

            var fn = new CodeEntity { Name = name, EntityType = CodeEntityType.Function, Namespace = nsId };
            CodeEntityService.Save(projFolder, "Function", fn);
            dlg.Close(Result(fn.Id, ""));
        };
        tree.DoubleTapped += (_, _) => { if (pickExisting.IsChecked == true) PickLeaf(); };

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } };

        // A wrapping caption above a control, so longer translations flow to a second line instead of clipping.
        static StackPanel Labeled(string key, Control c) => new()
        {
            Spacing = 4, Children = { new TextBlock { Text = Loc.S(key), TextWrapping = TextWrapping.Wrap }, c }
        };

        // Stacked (not side-by-side): the arguments field gets the FULL dialog width, so several arguments
        // still fit comfortably.
        var callForm = new StackPanel { Spacing = 8, Children = { Labeled("Sub_ReturnLabel", assignBox), Labeled("Sub_ArgsLabel", argsBox) } };

        dlg.Content = new StackPanel
        {
            Margin = new(20), Spacing = 10,
            Children =
            {
                new TextBlock { Text = Loc.S("Sub_Namespace"), TextWrapping = TextWrapping.Wrap },
                nsCombo,
                pickExisting,
                tree,
                createNew,
                nameBox,
                new TextBlock { Text = Loc.S("Sub_CallForm"), TextWrapping = TextWrapping.Wrap },
                callForm,
                instanceRow,
                btnRow,
            },
        };

        return dlg.ShowDialog<SubResult?>(owner);
    }
}
