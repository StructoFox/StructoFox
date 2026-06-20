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
    public static Task<string?> Show(Window owner, string projFolder, string suggestedName)
    {
        var funcs = CodeEntityService.LoadAll(projFolder, "Function").ToList();
        var hasExisting = funcs.Count > 0;

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

        var list = new ListBox { SelectionMode = SelectionMode.Single, MinHeight = 180 };
        Ui.Theme(list, TemplatedControl.BackgroundProperty,  "ControlBgBrush");
        Ui.Theme(list, TemplatedControl.ForegroundProperty,  "SidebarTextBrush");
        Ui.Theme(list, TemplatedControl.BorderBrushProperty, "ControlBorderBrush");

        var nameBox = new TextBox { Text = suggestedName, MinWidth = 320 };
        Ui.Theme(nameBox, TextBox.BackgroundProperty,  "InputBgBrush");
        Ui.Theme(nameBox, TextBox.ForegroundProperty,  "SidebarTextBrush");
        Ui.Theme(nameBox, TextBox.BorderBrushProperty, "ControlBorderBrush");

        // Fills the existing-function list with the functions of the selected namespace.
        void RefillList()
        {
            var nsId = NsId();
            list.Items.Clear();
            foreach (var f in funcs.Where(f => f.Namespace == nsId).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                list.Items.Add(new ListBoxItem { Content = f.Name, Tag = f.Id });
        }
        // Greys out whichever mode isn't active, so it's obvious which input matters.
        void SyncEnabled()
        {
            list.IsEnabled    = pickExisting.IsChecked == true;
            nameBox.IsEnabled = createNew.IsChecked == true;
        }
        nsCombo.SelectionChanged += (_, _) => RefillList();
        pickExisting.IsCheckedChanged += (_, _) => SyncEnabled();
        createNew.IsCheckedChanged    += (_, _) => SyncEnabled();
        RefillList();
        SyncEnabled();

        var ok     = Ui.Btn(Loc.S("Common_OK"));     ok.IsDefault = true;
        var cancel = Ui.Btn(Loc.S("Common_Cancel")); cancel.IsCancel = true;
        cancel.Click += (_, _) => dlg.Close(null);

        ok.Click += async (_, _) =>
        {
            if (pickExisting.IsChecked == true)
            {
                if (list.SelectedItem is ListBoxItem { Tag: string id }) dlg.Close(id);
                return;   // nothing picked → stay open
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
        list.DoubleTapped += (_, _) => { if (pickExisting.IsChecked == true && list.SelectedItem is ListBoxItem { Tag: string id }) dlg.Close(id); };

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } };

        dlg.Content = new StackPanel
        {
            Margin = new(20), Spacing = 10,
            Children =
            {
                new TextBlock { Text = Loc.S("Sub_Namespace") },
                nsCombo,
                pickExisting,
                list,
                createNew,
                nameBox,
                btnRow,
            },
        };

        return dlg.ShowDialog<string?>(owner);
    }
}
