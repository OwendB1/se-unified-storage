using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ClientPlugin.Inventory;
using ClientPlugin.Profiles;
using Sandbox.Definitions;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin.UI;

internal sealed class InventoryGroupActionsScreen : UnifiedStorageScreen
{
    private readonly IReadOnlyList<(string Label, Action Run)> actions;
    public InventoryGroupActionsScreen(IReadOnlyList<(string Label, Action Run)> actions) : base("Group actions") => this.actions = actions;
    protected override void CreateControls()
    {
        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            Controls.Add(Button(action.Label, new Vector2(0, -0.22f + i * 0.08f), action.Run, 0.36f));
        }
        Controls.Add(Button("Close", new Vector2(0, 0.34f), () => CloseScreen()));
    }
}

internal sealed class InventoryGroupsScreen : UnifiedStorageScreen
{
    private readonly MechanicalInventorySession session;
    private readonly ScopeProfile profile;
    private MultiSelectTable table;

    public InventoryGroupsScreen(MechanicalInventorySession session, ScopeProfile profile) : base("Inventory groups")
    {
        this.session = session;
        this.profile = profile;
    }

    protected override void CreateControls()
    {
        var selectedIds = SelectedGroups.Select(group => group.Id).ToArray();
        table = new MultiSelectTable
        {
            Name = "InventoryGroups", Position = new Vector2(-0.36f, -0.31f), Size = new Vector2(0.72f, 0.4f),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP, ColumnsCount = 3, VisibleRowsCount = 12
        };
        table.SetCustomColumnWidths(new[] { 0.36f, 0.3f, 0.34f });
        table.SetColumnName(0, new StringBuilder("Group"));
        table.SetColumnName(1, new StringBuilder("Rules"));
        table.SetColumnName(2, new StringBuilder("Status"));
        foreach (var group in profile.Groups)
        {
            var members = InventoryGroups.Resolve(session.Refresh().Scope, group, out var error);
            var row = new MyGuiControlTable.Row(group);
            row.AddCell(new MyGuiControlTable.Cell(group.Name));
            row.AddCell(new MyGuiControlTable.Cell(group.EffectiveRules.Count().ToString(CultureInfo.InvariantCulture)));
            row.AddCell(new MyGuiControlTable.Cell(error ?? $"{members.Count} inventories"));
            table.Add(row);
        }
        Controls.Add(table);
        table.SetToolTip(UnifiedStorageHelp.Wrap(MultiSelectTable.SelectionHelp + " Duplicate, move and delete affect the selection. Edit requires exactly one row. No items are moved."));
        table.RestoreSelection(Enumerable.Range(0, table.RowsCount).Where(index => selectedIds.Contains(((InventoryGroupRecord)table.GetRow(index).UserData).Id)));
        Controls.Add(Label("Groups are views. Creating or editing one does not move items.", new Vector2(-0.36f, 0.15f)));
        Controls.Add(Button("New", new Vector2(-0.24f, 0.21f), () => Edit(null)));
        var edit = Button("Edit", new Vector2(0, 0.21f), () => { if (SelectedGroups.Count() == 1) Edit(SelectedGroups.Single()); });
        edit.SetToolTip("Edit one selected group. Select exactly one row to enable this action.");
        Controls.Add(edit);
        table.SelectionChanged += () => edit.Enabled = SelectedGroups.Count() == 1;
        edit.Enabled = SelectedGroups.Count() == 1;
        Controls.Add(Button("Duplicate", new Vector2(0.24f, 0.21f), () =>
        {
            foreach (var group in SelectedGroups.ToArray())
            {
                var copy = group.Copy();
                copy.Id = Guid.NewGuid().ToString("N"); copy.Name += " copy";
                profile.Groups.Add(copy);
            }
            Save();
        }));
        Controls.Add(Button("Move up", new Vector2(-0.24f, 0.275f), () => Move(-1)));
        Controls.Add(Button("Move down", new Vector2(0, 0.275f), () => Move(1)));
        Controls.Add(Button("Delete", new Vector2(0.24f, 0.275f), () =>
        {
            foreach (var group in SelectedGroups.ToArray()) profile.Groups.Remove(group);
            Save(); // Referencing rules remain visible and paused.
        }));
        Controls.Add(Button("Restore defaults", new Vector2(-0.24f, 0.34f), () =>
        {
            MyGuiSandbox.AddScreen(MyGuiSandbox.CreateMessageBox(buttonType: MyMessageBoxButtonsType.YES_NO,
                messageText: new StringBuilder("Reset built-in groups and restore deleted presets? Custom groups and loadouts are kept."),
                callback: answer =>
                {
                    if (answer != MyGuiScreenMessageBox.ResultEnum.YES) return;
                    foreach (var preset in InventoryGroupRecord.Defaults())
                    {
                        var index = profile.Groups.FindIndex(group => group.Id == preset.Id);
                        if (index < 0) profile.Groups.Add(preset); else profile.Groups[index] = preset;
                    }
                    Save();
                }));
        }));
        Controls.Add(Button("Shared profile", new Vector2(0, 0.34f),
            () => MyGuiSandbox.AddScreen(new SharedProfileScreen(session, profile, () => RecreateControls(false))), 0.2f));
        Controls.Add(Button("Close", new Vector2(0.24f, 0.34f), () => CloseScreen()));
    }

    private IEnumerable<InventoryGroupRecord> SelectedGroups => table?.SelectedRows.Select(row => (InventoryGroupRecord)row.UserData)
        ?? Enumerable.Empty<InventoryGroupRecord>();
    private void Edit(InventoryGroupRecord group) => MyGuiSandbox.AddScreen(new InventoryGroupEditor(session, group, value =>
    {
        var index = group == null ? -1 : profile.Groups.IndexOf(group);
        if (index < 0) profile.Groups.Add(value); else profile.Groups[index] = value;
        Save();
    }));
    private void Move(int offset)
    {
        ListSelection.Move(profile.Groups, SelectedGroups, offset);
        Save();
    }
    private void Save()
    {
        Plugin.Instance.Profiles.Save(); session.MarkContentsDirty(); RecreateControls(false);
    }
}

// Shared native controls keep the group and loadout editors aligned with Manage.
internal abstract class InventoryRuleEditor : UnifiedStorageScreen
{
    protected InventoryRuleEditor(string caption) : base(caption) { }
    protected MyGuiControlCombobox Combo(string name, string label, float x, float y, float width,
        IEnumerable<string> values, int selected = 0)
    {
        Controls.Add(Label(label, new Vector2(x, y - 0.035f)));
        var combo = new MyGuiControlCombobox(new Vector2(x, y), new Vector2(width, 0.04f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, openAreaItemsCount: 6) { Name = name };
        combo.SetToolTip(UnifiedStorageHelp.Field(label));
        var index = 0;
        foreach (var value in values) combo.AddItem(index++, value);
        if (index > 0) combo.SelectItemByIndex(Math.Max(0, Math.Min(selected, index - 1)));
        Controls.Add(combo); return combo;
    }
    protected MyGuiControlTextbox Text(string name, string label, float x, float y, float width, string value, int max = 256)
    {
        Controls.Add(Label(label, new Vector2(x, y - 0.035f)));
        var box = new MyGuiControlTextbox(new Vector2(x, y), value ?? string.Empty, max)
        {
            Name = name, Size = new Vector2(width, 0.04f),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER
        };
        box.SetToolTip(UnifiedStorageHelp.Field(label));
        Controls.Add(box); return box;
    }
    protected MyGuiControlCheckbox Check(string label, float x, float y, bool value)
    {
        var box = new MyGuiControlCheckbox(new Vector2(x, y))
        { IsChecked = value, OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER };
        box.SetToolTip(UnifiedStorageHelp.Field(label));
        Controls.Add(box); Controls.Add(Label(label, new Vector2(x + 0.035f, y))); return box;
    }
}

internal sealed class InventoryGroupEditor : InventoryRuleEditor
{
    private readonly MechanicalInventorySession session;
    private readonly InventoryGroupRecord record;
    private readonly Action<InventoryGroupRecord> save;
    private MyGuiControlTextbox name;
    private MultiSelectTable table;

    public InventoryGroupEditor(MechanicalInventorySession session, InventoryGroupRecord record,
        Action<InventoryGroupRecord> save) : base("Edit inventory group")
    {
        this.session = session;
        this.record = record?.Copy() ?? new InventoryGroupRecord { Rules = new() };
        this.save = save;
    }

    protected override void CreateControls()
    {
        // Child dialogs edit this copy only. Rebuilding controls must not lose a typed name.
        var draftName = name?.Text ?? record.Name;
        var selected = SelectedRules.ToArray();
        name = Text("GroupName", "Display name", -0.36f, -0.30f, 0.72f, draftName, 128);
        table = new MultiSelectTable
        {
            Name = "InventoryGroupRules", Position = new Vector2(-0.36f, -0.25f), Size = new Vector2(0.72f, 0.4f),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP, ColumnsCount = 4, VisibleRowsCount = 12
        };
        table.SetCustomColumnWidths(new[] { 0.19f, 0.32f, 0.19f, 0.30f });
        table.SetColumnName(0, new StringBuilder("Match by"));
        table.SetColumnName(1, new StringBuilder("Definition / type"));
        table.SetColumnName(2, new StringBuilder("Inventory role"));
        table.SetColumnName(3, new StringBuilder("Items"));
        foreach (var rule in record.Rules)
        {
            var row = new MyGuiControlTable.Row(rule);
            MyGuiControlTable.Cell Cell(string text, string detail = null) => new(text,
                toolTip: UnifiedStorageHelp.Wrap(detail == null ? text : text + "\n" + detail)) { IsAutoScaleEnabled = true };
            row.AddCell(Cell(InventoryGroupRuleEditor.SelectorNames[(int)rule.Selector]));
            row.AddCell(Cell(InventoryGroupRuleEditor.SelectionLabel(session, rule), rule.Value));
            row.AddCell(Cell(rule.AllRoles ? "All roles" : Friendly(rule.Role.ToString())));
            var category = string.IsNullOrEmpty(rule.ItemType) ? "All categories" : Friendly(rule.ItemType.Replace("MyObjectBuilder_", ""));
            row.AddCell(Cell(category + " / " +
                (string.IsNullOrEmpty(rule.ItemDefinitionId) ? "All items" : InventoryGroupRuleEditor.Display(rule.ItemDefinitionId)), rule.ItemDefinitionId));
            table.Add(row);
        }
        Controls.Add(table);
        table.SetToolTip(UnifiedStorageHelp.Wrap(MultiSelectTable.SelectionHelp +
            " Each row combines its block, role and item filters. Rows are alternatives (OR); matching inventories and stacks are counted once. All edits remain drafts until Apply below."));
        table.RestoreSelection(Enumerable.Range(0, table.RowsCount).Where(index => selected.Contains(record.Rules[index])));
        var status = InventoryGroups.Resolve(session.Refresh().Scope, record, out var error);
        var statusText = error ?? (record.Rules.Count == 0 ? "No rules: this group matches nothing." :
            $"{record.Rules.Count} rules (OR); {status.Count} matching inventories. Apply saves all edits.");
        var statusLabel = new MyGuiControlLabel(new Vector2(-0.36f, 0.22f), text: statusText, textScale: 0.7f,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER,
            isAutoEllipsisEnabled: true, maxWidth: 0.72f);
        statusLabel.SetToolTip(UnifiedStorageHelp.Wrap(statusText));
        Controls.Add(statusLabel);
        var add = Button("Add rule", new Vector2(-0.285f, 0.275f), () => Edit(null), 0.15f);
        add.Enabled = record.Rules.Count < InventoryGroupRecord.MaxRules;
        Controls.Add(add);
        var edit = Button("Edit rule", new Vector2(-0.095f, 0.275f), () =>
        { if (SelectedRules.Count() == 1) Edit(SelectedRules.Single()); }, 0.15f);
        var duplicate = Button("Duplicate", new Vector2(0.095f, 0.275f), () =>
        {
            record.Rules.AddRange(SelectedRules.Select(rule => rule.CopyRule()).ToArray());
            RecreateControls(false);
        }, 0.15f);
        var remove = Button("Remove", new Vector2(0.285f, 0.275f), () =>
        {
            foreach (var rule in SelectedRules.ToArray()) record.Rules.Remove(rule);
            RecreateControls(false);
        }, 0.15f);
        Controls.Add(edit); Controls.Add(duplicate); Controls.Add(remove);
        void SelectionChanged()
        {
            var count = SelectedRules.Count();
            edit.Enabled = count == 1;
            duplicate.Enabled = count > 0 && record.Rules.Count + count <= InventoryGroupRecord.MaxRules;
            remove.Enabled = count > 0;
        }
        table.SelectionChanged += SelectionChanged;
        SelectionChanged();
        var apply = Button("Apply", new Vector2(-0.12f, 0.34f), () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) return;
            record.Name = name.Text.Trim(); save(record); CloseScreen();
        });
        apply.Enabled = !string.IsNullOrWhiteSpace(name.Text);
        name.TextChanged += _ => apply.Enabled = !string.IsNullOrWhiteSpace(name.Text);
        Controls.Add(apply);
        Controls.Add(Button("Cancel", new Vector2(0.12f, 0.34f), () => CloseScreen()));
    }

    private IEnumerable<InventoryGroupRule> SelectedRules => table?.SelectedRows.Select(row => (InventoryGroupRule)row.UserData)
        ?? Enumerable.Empty<InventoryGroupRule>();

    private void Edit(InventoryGroupRule rule) => MyGuiSandbox.AddScreen(new InventoryGroupRuleEditor(session, rule, value =>
    {
        var index = rule == null ? -1 : record.Rules.IndexOf(rule);
        if (index < 0) record.Rules.Add(value); else record.Rules[index] = value;
        RecreateControls(false);
        table.SetSelectedRow(record.Rules.IndexOf(value));
    }));

    internal static string Friendly(string value) => System.Text.RegularExpressions.Regex.Replace(value, "([a-z])([A-Z])", "$1 $2");
}

internal sealed class InventoryGroupRuleEditor : InventoryRuleEditor
{
    private readonly MechanicalInventorySession session;
    private readonly InventoryGroupRule record;
    private readonly Action<InventoryGroupRule> save;
    private MyGuiControlCombobox selector, value, role, itemType, item;
    private List<string> values;
    private List<string> itemTypes;
    private List<MyDefinitionId> items;

    internal static readonly string[] SelectorNames = { "All blocks", "Block family", "Block type", "Block definition", "Terminal group name", "Specific block", "Recipe output" };

    public InventoryGroupRuleEditor(MechanicalInventorySession session, InventoryGroupRule record,
        Action<InventoryGroupRule> save) : base("Edit group rule")
    {
        this.session = session; this.record = record?.CopyRule() ?? new InventoryGroupRule(); this.save = save;
    }
    protected override void CreateControls()
    {
        Controls.Add(Label("One rule: all selected conditions must match together.", new Vector2(-0.36f, -0.27f)));
        selector = Combo("GroupSelector", "Select blocks by", -0.36f, -0.12f, 0.22f,
            SelectorNames, (int)record.Selector);
        value = Combo("GroupValue", "Selection (resolved on this ship)", -0.12f, -0.12f, 0.48f, Array.Empty<string>());
        role = Combo("GroupRole", "Inventory role", -0.36f, 0f, 0.3f,
            new[] { "All roles" }.Concat(Enum.GetNames(typeof(InventoryRoleKind)).Select(InventoryGroupEditor.Friendly)), record.AllRoles ? 0 : (int)record.Role + 1);
        items = MyDefinitionManager.Static.GetPhysicalItemDefinitions().Select(d => d.Id)
            .OrderBy(id => Display(id), StringComparer.CurrentCultureIgnoreCase).ToList();
        // Missing mod definitions stay saved, not silently broadened to All items on a name-only edit.
        if (MyDefinitionId.TryParse(record.ItemDefinitionId, out var savedItem) && !items.Contains(savedItem)) items.Add(savedItem);
        itemTypes = new[] { string.Empty }.Concat(items.Select(id => id.TypeId.ToString()).Distinct().OrderBy(v => v)).ToList();
        if (!string.IsNullOrEmpty(record.ItemType) && !itemTypes.Contains(record.ItemType)) itemTypes.Add(record.ItemType);
        itemType = Combo("GroupMaterialType", "Material / item category", -0.04f, 0f, 0.4f,
            itemTypes.Select(v => v.Length == 0 ? "All item categories" : v.Replace("MyObjectBuilder_", "")), itemTypes.IndexOf(record.ItemType ?? ""));
        item = Combo("GroupMaterial", "Exact material / item (optional)", -0.36f, 0.12f, 0.72f,
            new[] { "All items" }.Concat(items.Select(Display)), items.FindIndex(id => id.ToString() == record.ItemDefinitionId) + 1);
        Controls.Add(Label("Block selection, role and item filters are combined. Live constraints still apply.", new Vector2(-0.36f, 0.22f)));
        Controls.Add(Label("Terminal group names are saved; members are never frozen into block IDs.", new Vector2(-0.36f, 0.26f)));
        Controls.Add(Button("Save rule", new Vector2(-0.12f, 0.34f), Apply));
        Controls.Add(Button("Cancel", new Vector2(0.12f, 0.34f), () => CloseScreen()));
        RefreshValues(); selector.ItemSelected += RefreshValues;
    }
    private void RefreshValues()
    {
        var kind = (InventoryGroupSelector)selector.GetSelectedKey();
        var labels = SelectionLabels(session, kind);
        var saved = kind == InventoryGroupSelector.Family ? record.Family.ToString() : record.Value ?? "";
        if (kind == record.Selector && !labels.ContainsKey(saved)) labels[saved] = saved + " (not found)";
        values = labels.OrderBy(pair => pair.Value, StringComparer.CurrentCultureIgnoreCase).Select(pair => pair.Key).ToList();
        value.ClearItems();
        for (var i = 0; i < values.Count; i++) value.AddItem(i, labels[values[i]]);
        if (values.Count > 0) value.SelectItemByIndex(Math.Max(0, kind == record.Selector ? values.IndexOf(saved) : 0));
    }

    internal static string SelectionLabel(MechanicalInventorySession session, InventoryGroupRule rule)
    {
        var key = rule.Selector == InventoryGroupSelector.Family ? rule.Family.ToString() : rule.Value ?? "";
        return SelectionLabels(session, rule.Selector).TryGetValue(key, out var label) ? label : key + " (not found)";
    }

    private static Dictionary<string, string> SelectionLabels(MechanicalInventorySession session, InventoryGroupSelector kind)
    {
        var scope = session.Refresh().Scope;
        var labels = new Dictionary<string, string>();
        switch (kind)
        {
            case InventoryGroupSelector.Family:
                foreach (InventorySectionKind family in Enum.GetValues(typeof(InventorySectionKind))) labels[family.ToString()] = InventoryGroupRecord.DisplayName(family);
                break;
            case InventoryGroupSelector.BlockType:
                foreach (var member in scope.Inventories) labels[member.BlockDefinitionId.TypeId.ToString()] = member.BlockDefinitionId.TypeId.ToString().Replace("MyObjectBuilder_", "");
                break;
            case InventoryGroupSelector.BlockDefinition:
                foreach (var member in scope.Inventories) labels[member.BlockDefinitionId.ToString()] = member.Owner.BlockDefinition.DisplayNameText + " / " + member.BlockDefinitionId.SubtypeName;
                break;
            case InventoryGroupSelector.TerminalGroup:
                foreach (var groupName in InventoryGroups.NamedGroups(scope).Keys) labels[groupName] = groupName;
                break;
            case InventoryGroupSelector.Block:
                foreach (var member in scope.Inventories) labels[member.OwnerEntityId.ToString(CultureInfo.InvariantCulture)] = member.Owner.DisplayNameText + " / " + member.OwnerEntityId;
                break;
            case InventoryGroupSelector.RecipeOutput:
                foreach (var id in scope.Inventories.Select(m => m.Owner.BlockDefinition).OfType<MyProductionBlockDefinition>()
                             .SelectMany(d => d.BlueprintClasses).SelectMany(c => c).SelectMany(b => b.Results).Select(r => r.Id).Distinct())
                    labels[id.ToString()] = Display(id);
                break;
            default: labels[string.Empty] = "All blocks on this mechanical ship"; break;
        }
        return labels;
    }
    private void Apply()
    {
        if (value.GetSelectedKey() < 0) return;
        record.Selector = (InventoryGroupSelector)selector.GetSelectedKey();
        record.Value = values[(int)value.GetSelectedKey()];
        if (record.Selector == InventoryGroupSelector.Family) record.Family = (InventorySectionKind)Enum.Parse(typeof(InventorySectionKind), record.Value);
        record.AllRoles = role.GetSelectedKey() == 0;
        record.Role = record.AllRoles ? InventoryRoleKind.GeneralCargo : (InventoryRoleKind)(role.GetSelectedKey() - 1);
        record.ItemType = itemTypes[(int)itemType.GetSelectedKey()];
        record.ItemDefinitionId = item.GetSelectedKey() > 0 ? items[(int)item.GetSelectedKey() - 1].ToString() : string.Empty;
        save(record); CloseScreen();
    }
    private static string Display(MyDefinitionId id) => MyDefinitionManager.Static.GetPhysicalItemDefinition(id)?.DisplayNameText ?? id.SubtypeName;
    internal static string Display(string id) => MyDefinitionId.TryParse(id, out var parsed) ? Display(parsed) : id;
}
