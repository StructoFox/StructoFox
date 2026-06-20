using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using StructoFox.Core;
using StructoFox.Core.Models;

namespace StructoFox.App;

/// <summary>
/// Standalone editor for a single CodeEntity — name, type, namespace, inheritance, fields, methods,
/// enum values and flow ports. Persists the entity on Save and reports it via <see cref="Saved"/>.
/// The big workbench where a class is hammered into its final shape.
/// </summary>
public class CodeEntityEditorDialog : Window
{
    readonly string _projFolder;
    readonly CodeEntity _entity;
    readonly IReadOnlyDictionary<string, CodeEntity> _known;
    readonly string? _themePath;

    /// <summary>True if the user saved (entity persisted to disk).</summary>
    public bool Saved { get; private set; }
    /// <summary>The entity type before editing (lets callers notice a type change).</summary>
    public CodeEntityType OldType { get; private set; }

    static readonly FontFamily Mono = new("Consolas, Cascadia Mono, monospace");

    // Opens the editor over an owner; resolves to true if the user saved.
    public static Task<bool> Edit(Window owner, string projFolder, CodeEntity entity,
        IReadOnlyDictionary<string, CodeEntity> known, string? themePath)
        => new CodeEntityEditorDialog(projFolder, entity, known, themePath).ShowDialog<bool>(owner);

    CodeEntityEditorDialog(string projFolder, CodeEntity entity,
        IReadOnlyDictionary<string, CodeEntity> known, string? themePath)
    {
        _projFolder = projFolder;
        _entity     = entity;
        _known      = known;
        _themePath  = themePath;
        OldType     = entity.EntityType;

        Title                 = string.Format(Loc.S("CodeEdit_Title"), entity.Name);
        Width                 = 560;
        Height                = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Ui.ThemeWindow(this);

        BuildForm();
    }

    void BuildForm()
    {
        var entity = _entity;

        var root = new Grid { Margin = new(16) };
        root.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Content = root;

        var form = new StackPanel();
        var formScroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(formScroll, 0); root.Children.Add(formScroll);

        // Name
        form.Children.Add(FieldLabel(Loc.S("Common_Name")));
        var nameBox = EditorBox(entity.Name);
        form.Children.Add(nameBox);

        // Type
        form.Children.Add(FieldLabel(Loc.S("CodeEdit_Type")));
        var typeCombo = Ui.Combo();
        foreach (var et in Enum.GetValues<CodeEntityType>()) typeCombo.Items.Add(et);
        typeCombo.SelectedItem = entity.EntityType;
        form.Children.Add(typeCombo);

        // Namespace
        // Namespace is chosen from the ones managed in the Namespaces tab (a combo, not free text).
        form.Children.Add(FieldLabel(Loc.S("CodeEdit_Namespace")));
        // Items carry the namespace entity's Id (so a later rename of the namespace stays linked).
        var nsCombo = Ui.Combo();
        nsCombo.Items.Add(new ComboItem(Loc.S("Sec_NsNone"), ""));
        foreach (var n in CodeEntityService.LoadAll(_projFolder, "Namespace").OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
            nsCombo.Items.Add(new ComboItem(n.Name, n.Id));
        // Preselect by id, or by a legacy name value, else "(none)".
        nsCombo.SelectedItem = nsCombo.Items.OfType<ComboItem>().FirstOrDefault(c => c.Id == entity.Namespace)
            ?? nsCombo.Items.OfType<ComboItem>().FirstOrDefault(c => c.Name == entity.Namespace)
            ?? nsCombo.Items[0];
        form.Children.Add(nsCombo);

        // Description
        form.Children.Add(FieldLabel(Loc.S("CodeEdit_Description")));
        var descBox = EditorBox(entity.Description, multiLine: true);
        form.Children.Add(descBox);

        // Inheritance (Class/Struct)
        var inheritSection = new StackPanel();
        inheritSection.Children.Add(FieldLabel(Loc.S("CodeEdit_Inherits")));
        var baseCombo = Ui.Combo();
        baseCombo.Items.Add(new ComboItem(Loc.S("Common_None"), ""));
        foreach (var e in AllEntitiesOfTypes(CodeEntityType.Class, CodeEntityType.Struct).Where(e => e.Id != entity.Id))
            baseCombo.Items.Add(new ComboItem(e.Name, e.Id));
        baseCombo.SelectedItem = baseCombo.Items.OfType<ComboItem>().FirstOrDefault(c => c.Id == entity.BaseClassId)
                                 ?? baseCombo.Items[0];
        inheritSection.Children.Add(baseCombo);

        inheritSection.Children.Add(FieldLabel(Loc.S("CodeEdit_Implements")));
        var implPanel = new StackPanel { Margin = new(0, 0, 0, 8) };
        var implChecks = new List<(string Id, CheckBox Cb)>();
        foreach (var iface in AllEntitiesOfTypes(CodeEntityType.Interface))
        {
            var cb = new CheckBox { Content = iface.Name, IsChecked = entity.ImplementsIds.Contains(iface.Id), Margin = new(0, 1) };
            Ui.Theme(cb, CheckBox.ForegroundProperty, "SidebarTextBrush");
            implPanel.Children.Add(cb);
            implChecks.Add((iface.Id, cb));
        }
        if (implChecks.Count == 0)
            implPanel.Children.Add(new TextBlock { Text = Loc.S("CodeEdit_NoInterfaces"), Opacity = 0.5, FontSize = 10, FontStyle = FontStyle.Italic });
        inheritSection.Children.Add(implPanel);
        form.Children.Add(inheritSection);

        // Object: instance-of
        var instanceSection = new StackPanel();
        instanceSection.Children.Add(FieldLabel(Loc.S("CodeEdit_InstanceOf")));
        var instCombo = Ui.Combo();
        instCombo.Items.Add(new ComboItem(Loc.S("Common_None"), ""));
        foreach (var e in AllEntitiesOfTypes(CodeEntityType.Class, CodeEntityType.Struct))
            instCombo.Items.Add(new ComboItem(e.Name, e.Id));
        instCombo.SelectedItem = instCombo.Items.OfType<ComboItem>().FirstOrDefault(c => c.Id == entity.InstanceOfId)
                                 ?? instCombo.Items[0];
        instanceSection.Children.Add(instCombo);
        form.Children.Add(instanceSection);

        // ── Methods editor (defined before fields so a field's right-click can add accessors) ──
        var workMethods = entity.Methods.Select(m => new CodeMethod
        {
            Id = m.Id, Kind = m.Kind, Name = m.Name, ReturnType = m.ReturnType, Visibility = m.Visibility, IsStatic = m.IsStatic,
            Parameters = m.Parameters.Select(p => new CodeParam { Name = p.Name, DataType = p.DataType, Convention = p.Convention }).ToList()
        }).ToList();

        var methodsSection = CollapsibleSection(Loc.S("CodeEdit_Methods"), startCollapsed: workMethods.Count > 0,
            out var addMethodBtn, out var methodStack, out var methodCount);

        void RebuildMethodRows()
        {
            methodCount.Text = $"({workMethods.Count})";
            methodStack.Children.Clear();
            for (int i = 0; i < workMethods.Count; i++)
            {
                var ci = i;
                var m  = workMethods[i];

                bool isCtor = m.Kind == MethodKind.Constructor;
                bool isDtor = m.Kind == MethodKind.Destructor;

                var summary = ItemSummaryText(MethodSummary(m, entity.Name));
                void UpdateSummary() => summary.Text = MethodSummary(workMethods[ci], (nameBox.Text ?? "").Trim());

                var editor = new StackPanel { Margin = new(8, 2, 4, 6) };
                var topRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

                if (!isDtor)   // destructors have no visibility choice
                {
                    var visCombo = SmallCombo(78);
                    foreach (var v in Enum.GetValues<CodeVisibility>()) visCombo.Items.Add(v);
                    visCombo.SelectedItem = m.Visibility;
                    visCombo.SelectionChanged += (_, _) => { if (visCombo.SelectedItem is CodeVisibility v) { workMethods[ci].Visibility = v; UpdateSummary(); } };
                    topRow.Children.Add(visCombo);
                }

                if (!isCtor && !isDtor)   // ctor/dtor names derive from the class
                {
                    var nm = SmallBox(110, m.Name);
                    nm.TextChanged += (_, _) => { workMethods[ci].Name = nm.Text ?? ""; UpdateSummary(); };
                    topRow.Children.Add(nm);

                    var retLbl = new TextBlock { Text = ":", VerticalAlignment = VerticalAlignment.Center };
                    Ui.Theme(retLbl, TextBlock.ForegroundProperty, "SidebarTextBrush");
                    topRow.Children.Add(retLbl);

                    var ret = SmallBox(80, m.ReturnType);
                    ret.TextChanged += (_, _) => { workMethods[ci].ReturnType = ret.Text ?? ""; UpdateSummary(); };
                    topRow.Children.Add(ret);

                    var stat = new CheckBox { Content = "static", IsChecked = m.IsStatic, VerticalAlignment = VerticalAlignment.Center };
                    Ui.Theme(stat, CheckBox.ForegroundProperty, "SidebarTextBrush");
                    stat.IsCheckedChanged += (_, _) => { workMethods[ci].IsStatic = stat.IsChecked == true; UpdateSummary(); };
                    topRow.Children.Add(stat);
                }
                else
                {
                    var kindLbl = new TextBlock
                    {
                        Text = isCtor ? Loc.S("CodeEdit_ConstructorLbl") : Loc.S("CodeEdit_DestructorLbl"),
                        VerticalAlignment = VerticalAlignment.Center, FontStyle = FontStyle.Italic,
                    };
                    Ui.Theme(kindLbl, TextBlock.ForegroundProperty, "SidebarTextBrush");
                    topRow.Children.Add(kindLbl);
                }

                var flowM = Btn("🔁", Loc.S("CodeEdit_MethodFlowTip"));
                flowM.Click += (_, _) =>
                {
                    var key   = $"{entity.Id}#{m.Id}";
                    var title = $"{entity.Name}.{m.Name}";
                    _ = DiagramLauncher.ChooseAndOpen(this, _projFolder, key, title, _themePath);
                };
                topRow.Children.Add(flowM);

                var assignM = Btn("🗺", Loc.S("CodeEdit_AssignBoardTip"));
                assignM.Click += async (_, _) => await AssignBoardToKey($"{entity.Id}#{m.Id}");
                topRow.Children.Add(assignM);
                editor.Children.Add(topRow);

                var paramStack = new StackPanel { Margin = new(16, 2, 0, 0) };
                void RebuildParams()
                {
                    paramStack.Children.Clear();
                    for (int j = 0; j < m.Parameters.Count; j++)
                    {
                        var cj = j;
                        var p  = m.Parameters[j];
                        var prow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new(0, 1) };
                        var pConv = SmallCombo(86);
                        foreach (var c in Enum.GetValues<PassingConvention>()) pConv.Items.Add(c);
                        pConv.SelectedItem = p.Convention;
                        pConv.SelectionChanged += (_, _) => { if (pConv.SelectedItem is PassingConvention c) { m.Parameters[cj].Convention = c; UpdateSummary(); } };
                        prow.Children.Add(pConv);
                        var pName = SmallBox(90, p.Name);
                        pName.TextChanged += (_, _) => { m.Parameters[cj].Name = pName.Text ?? ""; UpdateSummary(); };
                        prow.Children.Add(pName);
                        var pType = SmallBox(80, p.DataType);
                        pType.TextChanged += (_, _) => { m.Parameters[cj].DataType = pType.Text ?? ""; UpdateSummary(); };
                        prow.Children.Add(pType);
                        var delP = Btn("✕");
                        delP.Click += (_, _) => { m.Parameters.RemoveAt(cj); RebuildParams(); UpdateSummary(); };
                        prow.Children.Add(delP);
                        paramStack.Children.Add(prow);
                    }
                    var addP = Btn(Loc.S("CodeEdit_AddParam"));
                    addP.Click += (_, _) => { m.Parameters.Add(new CodeParam()); RebuildParams(); UpdateSummary(); };
                    paramStack.Children.Add(addP);
                }
                if (!isDtor)   // destructors take no parameters
                {
                    RebuildParams();
                    editor.Children.Add(paramStack);
                }

                methodStack.Children.Add(ItemRow(summary, editor, () => { workMethods.RemoveAt(ci); RebuildMethodRows(); }, null, () => BoardWarn($"{entity.Id}#{m.Id}")));
            }
        }
        RebuildMethodRows();
        addMethodBtn.Click += (_, _) =>
        {
            var cm = new ContextMenu();
            void Add(string header, MethodKind kind)
            {
                var mi = new MenuItem { Header = header };
                mi.Click += (_, _) =>
                {
                    var nm = new CodeMethod { Kind = kind, Visibility = CodeVisibility.Public };
                    if (kind == MethodKind.Constructor) nm.ReturnType = "";
                    if (kind == MethodKind.Destructor)  { nm.ReturnType = ""; nm.Parameters.Clear(); }
                    workMethods.Add(nm);
                    RebuildMethodRows();
                    methodStack.IsVisible = true;
                };
                cm.Items.Add(mi);
            }
            Add(Loc.S("CodeEdit_AddMethod"),      MethodKind.Normal);
            Add(Loc.S("CodeEdit_AddConstructor"), MethodKind.Constructor);
            Add(Loc.S("CodeEdit_AddDestructor"),  MethodKind.Destructor);
            cm.Open(addMethodBtn);
        };

        // ── Fields editor (each field is a one-line summary; right-click → add accessor) ──
        var workFields = entity.Fields.Select(f => new CodeField
        { Name = f.Name, DataType = f.DataType, Visibility = f.Visibility, IsStatic = f.IsStatic, DefaultValue = f.DefaultValue }).ToList();

        var fieldsSection = CollapsibleSection(Loc.S("CodeEdit_Fields"), startCollapsed: workFields.Count > 0,
            out var addFieldBtn, out var fieldStack, out var fieldCount);

        void AddAccessor(CodeField f, bool getter)
        {
            var cap = string.IsNullOrWhiteSpace(f.Name) ? "Value" : char.ToUpperInvariant(f.Name[0]) + f.Name[1..];
            var m = new CodeMethod { Visibility = CodeVisibility.Public, IsStatic = f.IsStatic };
            if (getter) { m.Name = "Get" + cap; m.ReturnType = string.IsNullOrWhiteSpace(f.DataType) ? "var" : f.DataType; }
            else        { m.Name = "Set" + cap; m.ReturnType = "void"; m.Parameters.Add(new CodeParam { Name = "value", DataType = f.DataType }); }
            workMethods.Add(m);
            PrefillAccessorDiagrams(m, f, getter);
            RebuildMethodRows();
            methodStack.IsVisible = true;   // reveal so the new accessor is visible
        }

        void RebuildFieldRows()
        {
            fieldCount.Text = $"({workFields.Count})";
            fieldStack.Children.Clear();
            for (int i = 0; i < workFields.Count; i++)
            {
                var ci = i;
                var f  = workFields[i];

                var summary = ItemSummaryText(FieldSummary(f));
                void UpdateSummary() => summary.Text = FieldSummary(workFields[ci]);

                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new(8, 2, 4, 6) };

                var visCombo = SmallCombo(78);
                foreach (var v in Enum.GetValues<CodeVisibility>()) visCombo.Items.Add(v);
                visCombo.SelectedItem = f.Visibility;
                visCombo.SelectionChanged += (_, _) => { if (visCombo.SelectedItem is CodeVisibility v) { workFields[ci].Visibility = v; UpdateSummary(); } };
                row.Children.Add(visCombo);

                var nm = SmallBox(110, f.Name);
                nm.TextChanged += (_, _) => { workFields[ci].Name = nm.Text ?? ""; UpdateSummary(); };
                row.Children.Add(nm);

                var ty = SmallBox(90, f.DataType);
                ty.TextChanged += (_, _) => { workFields[ci].DataType = ty.Text ?? ""; UpdateSummary(); };
                row.Children.Add(ty);

                var stat = new CheckBox { Content = "static", IsChecked = f.IsStatic, VerticalAlignment = VerticalAlignment.Center };
                Ui.Theme(stat, CheckBox.ForegroundProperty, "SidebarTextBrush");
                stat.IsCheckedChanged += (_, _) => { workFields[ci].IsStatic = stat.IsChecked == true; UpdateSummary(); };
                row.Children.Add(stat);

                // Right-click on the summary → add Get / Set accessor, or delete (confirmed).
                var getMi = new MenuItem { Header = Loc.S("CodeEdit_AddGetter") };
                getMi.Click += (_, _) => AddAccessor(workFields[ci], getter: true);
                var setMi = new MenuItem { Header = Loc.S("CodeEdit_AddSetter") };
                setMi.Click += (_, _) => AddAccessor(workFields[ci], getter: false);

                fieldStack.Children.Add(ItemRow(summary, row, () => { workFields.RemoveAt(ci); RebuildFieldRows(); }, new[] { getMi, setMi }));
            }
        }
        RebuildFieldRows();
        addFieldBtn.Click += (_, _) => { workFields.Add(new CodeField()); RebuildFieldRows(); };

        // Fields above methods on the form (methods were built first only for the accessor reference).
        form.Children.Add(fieldsSection);
        form.Children.Add(methodsSection);

        // ── Enum values editor ──
        var workEnum = new List<string>(entity.EnumValues);
        var enumSection = CollapsibleSection(Loc.S("CodeEdit_EnumValues"), startCollapsed: workEnum.Count > 0,
            out var addEnumBtn, out var enumStack, out var enumCount);
        form.Children.Add(enumSection);

        void RebuildEnumRows()
        {
            enumCount.Text = $"({workEnum.Count})";
            enumStack.Children.Clear();
            for (int i = 0; i < workEnum.Count; i++)
            {
                var ci = i;
                var summary = ItemSummaryText("• " + workEnum[ci]);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new(8, 2, 4, 6) };
                var nm  = SmallBox(160, workEnum[ci]);
                nm.TextChanged += (_, _) => { workEnum[ci] = nm.Text ?? ""; summary.Text = "• " + nm.Text; };
                row.Children.Add(nm);
                enumStack.Children.Add(ItemRow(summary, row, () => { workEnum.RemoveAt(ci); RebuildEnumRows(); }));
            }
        }
        RebuildEnumRows();
        addEnumBtn.Click += (_, _) => { workEnum.Add(Loc.S("CodeEdit_DefaultValue")); RebuildEnumRows(); };

        // ── Data ports editor ──
        var workPorts = entity.Ports.Select(p => new CodePort
        { Id = p.Id, Name = p.Name, DataType = p.DataType, Direction = p.Direction, Convention = p.Convention }).ToList();

        var portsSection = CollapsibleSection(Loc.S("CodeEdit_DataPorts"), startCollapsed: workPorts.Count > 0,
            out var addPortBtn, out var portStack, out var portCount);
        form.Children.Add(portsSection);

        void RebuildPortRows()
        {
            portCount.Text = $"({workPorts.Count})";
            portStack.Children.Clear();
            for (int i = 0; i < workPorts.Count; i++)
            {
                var ci   = i;
                var port = workPorts[i];

                var summary = ItemSummaryText(PortSummary(port));
                void UpdateSummary() => summary.Text = PortSummary(workPorts[ci]);

                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new(8, 2, 4, 6) };

                var dirCombo = SmallCombo(84);
                foreach (var d in Enum.GetValues<PortDirection>()) dirCombo.Items.Add(d);
                dirCombo.SelectedItem = port.Direction;
                dirCombo.SelectionChanged += (_, _) => { if (dirCombo.SelectedItem is PortDirection d) { workPorts[ci].Direction = d; UpdateSummary(); } };
                row.Children.Add(dirCombo);

                var nm = SmallBox(100, port.Name);
                nm.TextChanged += (_, _) => { workPorts[ci].Name = nm.Text ?? ""; UpdateSummary(); };
                row.Children.Add(nm);

                var ty = SmallBox(80, port.DataType);
                ty.TextChanged += (_, _) => { workPorts[ci].DataType = ty.Text ?? ""; UpdateSummary(); };
                row.Children.Add(ty);

                var convCombo = SmallCombo(86);
                foreach (var c in Enum.GetValues<PassingConvention>()) convCombo.Items.Add(c);
                convCombo.SelectedItem = port.Convention;
                convCombo.SelectionChanged += (_, _) => { if (convCombo.SelectedItem is PassingConvention c) { workPorts[ci].Convention = c; UpdateSummary(); } };
                row.Children.Add(convCombo);

                portStack.Children.Add(ItemRow(summary, row, () => { workPorts.RemoveAt(ci); RebuildPortRows(); }));
            }
        }
        RebuildPortRows();
        addPortBtn.Click += (_, _) => { workPorts.Add(new CodePort()); RebuildPortRows(); };

        // Section visibility per type.
        void UpdateSections()
        {
            var t = typeCombo.SelectedItem is CodeEntityType ct ? ct : entity.EntityType;
            bool isClassish = t is CodeEntityType.Class or CodeEntityType.Struct;
            inheritSection.IsVisible  = isClassish;
            instanceSection.IsVisible = t == CodeEntityType.Object;
            fieldsSection.IsVisible   = isClassish;
            methodsSection.IsVisible  = isClassish || t == CodeEntityType.Interface;
            enumSection.IsVisible     = t == CodeEntityType.Enum;
            portsSection.IsVisible    = t == CodeEntityType.Function;
        }
        typeCombo.SelectionChanged += (_, _) => UpdateSections();
        UpdateSections();

        // Save / Cancel
        var bottomRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new(0, 8, 0, 0) };
        Grid.SetRow(bottomRow, 1); root.Children.Add(bottomRow);

        var cancelBtn = Ui.Btn(Loc.S("Common_Cancel")); cancelBtn.IsCancel = true;
        cancelBtn.Click += (_, _) => Close(false);
        bottomRow.Children.Add(cancelBtn);

        var saveBtn = Ui.Btn(Loc.S("Common_Save")); saveBtn.IsDefault = true;
        saveBtn.Click += (_, _) =>
        {
            OldType = entity.EntityType;

            entity.Name         = (nameBox.Text ?? "").Trim();
            entity.EntityType   = typeCombo.SelectedItem is CodeEntityType et ? et : entity.EntityType;
            entity.Namespace    = (nsCombo.SelectedItem as ComboItem)?.Id ?? "";
            entity.Description  = (descBox.Text ?? "").Trim();
            entity.BaseClassId  = (baseCombo.SelectedItem as ComboItem)?.Id ?? "";
            entity.ImplementsIds = implChecks.Where(c => c.Cb.IsChecked == true).Select(c => c.Id).ToList();
            entity.InstanceOfId  = (instCombo.SelectedItem as ComboItem)?.Id ?? "";
            entity.Fields       = workFields;
            entity.Methods      = workMethods;
            entity.EnumValues   = workEnum.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            entity.Ports        = workPorts;

            if (OldType != entity.EntityType)
                CodeEntityService.Delete(_projFolder, OldType.ToString(), entity.Id);
            CodeEntityService.Save(_projFolder, entity.EntityType.ToString(), entity);

            Saved = true;
            Close(true);
        };
        bottomRow.Children.Add(saveBtn);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    // Every entity matching one of the given types, sorted by name — the candidate pool for combos.
    List<CodeEntity> AllEntitiesOfTypes(params CodeEntityType[] types)
    {
        var set = types.ToHashSet();
        return _known.Values.Where(e => set.Contains(e.EntityType)).OrderBy(e => e.Name).ToList();
    }

    /// <summary>
    /// Pre-fills the standard flowchart and structogram for a click-created accessor:
    ///   getter → Start → read field → return field → End;  setter → Start → field = value → End.
    /// Never overwrites an existing diagram — it only seeds the empty ones.
    /// </summary>
    void PrefillAccessorDiagrams(CodeMethod m, CodeField f, bool getter)
    {
        var key   = $"{_entity.Id}#{m.Id}";
        var field = string.IsNullOrWhiteSpace(f.Name) ? "value" : f.Name;
        var title = $"{_entity.Name}.{m.Name}";

        if (!FlowChartService.Exists(_projFolder, key))
        {
            var fc    = new FlowChartData { Title = title };
            var start = new FlowNode { Kind = FlowNodeKind.Start, Text = "Start", X = 90, Y = 40, Width = 90, Height = 44 };
            fc.Nodes.Add(start);

            FlowNode prev = start;
            if (getter)
            {
                var read = new FlowNode { Kind = FlowNodeKind.Process, Text = $"read {field}", X = 60, Y = 130, Width = 150, Height = 52 };
                var ret  = new FlowNode { Kind = FlowNodeKind.InputOutput, Text = $"return {field}", X = 60, Y = 230, Width = 150, Height = 52 };
                fc.Nodes.Add(read); fc.Nodes.Add(ret);
                fc.Connections.Add(new FlowConnection { FromId = prev.Id, ToId = read.Id });
                fc.Connections.Add(new FlowConnection { FromId = read.Id, ToId = ret.Id });
                prev = ret;
            }
            else
            {
                var set = new FlowNode { Kind = FlowNodeKind.Process, Text = $"{field} = value", X = 60, Y = 130, Width = 150, Height = 52 };
                fc.Nodes.Add(set);
                fc.Connections.Add(new FlowConnection { FromId = prev.Id, ToId = set.Id });
                prev = set;
            }

            var end = new FlowNode { Kind = FlowNodeKind.End, Text = "End", X = 90, Y = getter ? 330 : 230, Width = 90, Height = 44 };
            fc.Nodes.Add(end);
            fc.Connections.Add(new FlowConnection { FromId = prev.Id, ToId = end.Id });

            FlowChartService.Save(_projFolder, key, fc);
        }

        if (!StructogramService.Exists(_projFolder, key))
        {
            var sd = new StructogramData { Title = title };
            sd.Root.Add(new NsBlock { Kind = NsBlockKind.Statement, Text = getter ? $"return {field}" : $"{field} = value" });
            StructogramService.Save(_projFolder, key, sd);
        }
    }

    // ── Summary helpers (compact one-line member descriptions) ───────────────

    static string VisSym(CodeVisibility v) => v switch
    {
        CodeVisibility.Public    => "+",
        CodeVisibility.Private   => "−",
        CodeVisibility.Protected => "#",
        CodeVisibility.Internal  => "~",
        _                        => " ",
    };

    static string ConvSym(PassingConvention c) => c switch
    {
        PassingConvention.Reference => "&",
        PassingConvention.Pointer   => "*",
        _                           => "",
    };

    static string FieldSummary(CodeField f) =>
        $"{VisSym(f.Visibility)} {(f.IsStatic ? "static " : "")}{(string.IsNullOrWhiteSpace(f.Name) ? "(field)" : f.Name)}: {f.DataType}";

    static string MethodSummary(CodeMethod m, string className)
    {
        var cls = string.IsNullOrWhiteSpace(className) ? "Class" : className;
        var ps  = string.Join(", ", m.Parameters.Select(p => $"{ConvSym(p.Convention)}{p.DataType} {p.Name}"));
        return m.Kind switch
        {
            MethodKind.Constructor => $"{VisSym(m.Visibility)} {cls}({ps})",
            MethodKind.Destructor  => $"~{cls}()",
            _                      => $"{VisSym(m.Visibility)} {(m.IsStatic ? "static " : "")}{(string.IsNullOrWhiteSpace(m.Name) ? "(method)" : m.Name)}({ps}): {m.ReturnType}",
        };
    }

    static string PortSummary(CodePort p) =>
        $"{(p.Direction == PortDirection.Input ? "▸ in " : "out ▸ ")}{(string.IsNullOrWhiteSpace(p.Name) ? "(port)" : p.Name)}: {ConvSym(p.Convention)}{p.DataType}";

    // A monospace summary line used as the collapsed header of an item.
    TextBlock ItemSummaryText(string text)
    {
        var tb = new TextBlock { Text = text, FontSize = 12, FontFamily = Mono, TextWrapping = TextWrapping.Wrap, Margin = new(6, 4) };
        Ui.Theme(tb, TextBlock.ForegroundProperty, "SidebarTextBrush");
        return tb;
    }

    // Wraps an item as a collapsed one-line summary that expands to its editor on click. A right-click
    // menu carries any extra entries (e.g. the field → Get/Set accessors) plus a confirmed Delete —
    // there is no inline ✕, so a member with a lot hanging off it can't be killed by a stray click.
    Border ItemRow(TextBlock summary, Control editor, Action onDelete, IEnumerable<MenuItem>? extra = null, Func<string?>? extraWarn = null)
    {
        editor.IsVisible = false;

        var header = new Border { Child = summary, Cursor = new Cursor(StandardCursorType.Hand), Background = Brushes.Transparent };
        header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
                editor.IsVisible = !editor.IsVisible;
        };

        var cm = new ContextMenu();
        var extras = extra?.ToList() ?? new();
        foreach (var mi in extras) cm.Items.Add(mi);
        if (extras.Count > 0) cm.Items.Add(new Separator());
        var del = new MenuItem { Header = Loc.S("CodeEdit_Delete") };
        del.Click += async (_, _) => { if (await ConfirmDelete(summary.Text ?? "", extraWarn?.Invoke())) onDelete(); };
        cm.Items.Add(del);
        header.ContextMenu = cm;

        var stack = new StackPanel { Children = { header, editor } };
        var outer = new Border { Child = stack, BorderThickness = new(0, 0, 0, 1), Margin = new(0, 0, 0, 1) };
        Ui.Theme(outer, Border.BorderBrushProperty, "ControlBorderBrush");
        return outer;
    }

    // Asks before deleting a member; returns true only on an explicit Yes. An extra warning line
    // (e.g. "a board is assigned") is appended when provided.
    async Task<bool> ConfirmDelete(string what, string? extraWarn = null)
    {
        var msg = string.Format(Loc.S("CodeEdit_DeleteConfirm"), what.Trim());
        if (!string.IsNullOrEmpty(extraWarn)) msg += "\n\n" + extraWarn;
        var res = await MessageDialog.Show(this, msg, Loc.S("CodeEdit_DeleteTitle"), DialogButtons.YesNo);
        return res == DialogResult.Yes;
    }

    // Assigns an existing project board to author this method's body (sets the board's TargetKey).
    async Task AssignBoardToKey(string key)
    {
        var boards = CodeBoardRegistryService.Load(_projFolder);
        if (boards.Count == 0) { await MessageDialog.Show(this, Loc.S("Boards_NoBoards"), Loc.S("Boards_Assign")); return; }
        var pick = await PickListDialog.Show(this, Loc.S("CodeEdit_AssignBoardTitle"), boards.Select(b => (b.Id, b.Name)).ToList());
        if (pick is null) return;
        // Refuse boards that carry classes/objects — they're architecture views, not function bodies.
        if (CodeBoardCodeGen.ContainsNonFunction(_projFolder, pick))
        { await MessageDialog.Show(this, Loc.S("Boards_HasNonFunc"), Loc.S("Boards_Assign")); return; }
        boards.First(b => b.Id == pick).TargetKey = key;
        CodeBoardRegistryService.Save(_projFolder, boards);
    }

    // A warning line if any board is assigned to the given diagram key, else null.
    string? BoardWarn(string key)
    {
        var names = CodeBoardRegistryService.Load(_projFolder).Where(b => b.TargetKey == key).Select(b => b.Name).ToList();
        return names.Count > 0 ? string.Format(Loc.S("CodeEdit_DeleteBoardWarn"), string.Join(", ", names)) : null;
    }

    TextBlock FieldLabel(string text)
    {
        var lbl = new TextBlock { Text = text, Margin = new(0, 8, 0, 2), FontSize = 12 };
        Ui.Theme(lbl, TextBlock.ForegroundProperty, "SidebarTextBrush");
        return lbl;
    }

    /// <summary>
    /// A collapsible editor section: a divider, a clickable header (arrow + title + item count),
    /// a "＋ Add" button, and a toggleable content panel that starts open or closed.
    /// </summary>
    StackPanel CollapsibleSection(string title, bool startCollapsed,
        out Button addBtn, out StackPanel content, out TextBlock countLabel)
    {
        var section = new StackPanel { Margin = new(0, 4, 0, 0) };

        var divider = new Border { Height = 1, Margin = new(0, 6) };
        Ui.Theme(divider, Border.BackgroundProperty, "ControlBorderBrush");
        section.Children.Add(divider);

        var hdr = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4, Margin = new(0, 0, 0, 4),
            Cursor = new Cursor(StandardCursorType.Hand), Background = Brushes.Transparent,
        };

        var arrow = new TextBlock { Text = startCollapsed ? "▸" : "▾", Width = 16, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Ui.Theme(arrow, TextBlock.ForegroundProperty, "SidebarTextBrush");
        hdr.Children.Add(arrow);

        var lbl = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Ui.Theme(lbl, TextBlock.ForegroundProperty, "SidebarTextBrush");
        hdr.Children.Add(lbl);

        countLabel = new TextBlock { Text = "", Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
        Ui.Theme(countLabel, TextBlock.ForegroundProperty, "SidebarTextBrush");
        hdr.Children.Add(countLabel);

        addBtn = Btn(Loc.S("Common_AddPlus"));
        addBtn.Margin = new(8, 0, 0, 0);
        hdr.Children.Add(addBtn);

        content = new StackPanel { Margin = new(0, 0, 0, 8), IsVisible = !startCollapsed };

        var capContent = content;
        var capArrow   = arrow;
        void Toggle(bool show) { capContent.IsVisible = show; capArrow.Text = show ? "▾" : "▸"; }
        hdr.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(hdr).Properties.IsLeftButtonPressed && e.Source is not Button)
                Toggle(!capContent.IsVisible);
        };
        addBtn.Click += (_, _) => Toggle(true);   // adding always reveals the section

        section.Children.Add(hdr);
        section.Children.Add(content);
        return section;
    }

    TextBox EditorBox(string value, bool multiLine = false)
    {
        var box = new TextBox
        {
            Text = value, AcceptsReturn = multiLine,
            TextWrapping = multiLine ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Height = multiLine ? 56 : double.NaN,
        };
        if (multiLine) ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        Ui.Theme(box, TextBox.BackgroundProperty,  "InputBgBrush");
        Ui.Theme(box, TextBox.ForegroundProperty,  "SidebarTextBrush");
        Ui.Theme(box, TextBox.BorderBrushProperty, "ControlBorderBrush");
        return box;
    }

    TextBox SmallBox(double width, string value)
    {
        var b = new TextBox { Width = width, Text = value, FontSize = 12 };
        Ui.Theme(b, TextBox.BackgroundProperty,  "InputBgBrush");
        Ui.Theme(b, TextBox.ForegroundProperty,  "SidebarTextBrush");
        Ui.Theme(b, TextBox.BorderBrushProperty, "ControlBorderBrush");
        return b;
    }

    ComboBox SmallCombo(double width)
    {
        var c = Ui.Combo(width);
        c.MinHeight = 28;
        c.FontSize  = 12;
        return c;
    }

    Button Btn(string label, string? tooltip = null)
    {
        var b = Ui.Btn(label, tooltip);
        b.Padding  = new(8, 4);
        b.FontSize = 12;
        return b;
    }
}
