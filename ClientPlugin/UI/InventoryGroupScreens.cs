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
    private MyGuiControlTable table;

    public InventoryGroupsScreen(MechanicalInventorySession session, ScopeProfile profile) : base("Inventory groups")
    {
        this.session = session;
        this.profile = profile;
    }

    protected override void CreateControls()
    {
        table = new MyGuiControlTable
        {
            Name = "InventoryGroups", Position = new Vector2(-0.36f, -0.31f), Size = new Vector2(0.72f, 0.4f),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP, ColumnsCount = 3, VisibleRowsCount = 12
        };
        table.SetCustomColumnWidths(new[] { 0.36f, 0.3f, 0.34f });
        table.SetColumnName(0, new StringBuilder("Group"));
        table.SetColumnName(1, new StringBuilder("Selector"));
        table.SetColumnName(2, new StringBuilder("Status"));
        foreach (var group in profile.Groups)
        {
            var members = InventoryGroups.Resolve(session.Refresh().Scope, group, out var error);
            var row = new MyGuiControlTable.Row(group);
            row.AddCell(new MyGuiControlTable.Cell(group.Name));
            row.AddCell(new MyGuiControlTable.Cell(group.Selector.ToString()));
            row.AddCell(new MyGuiControlTable.Cell(error ?? $"{members.Count} inventories"));
            table.Add(row);
        }
        Controls.Add(table);
        Controls.Add(Label("Groups are views. Creating or editing one does not move items.", new Vector2(-0.36f, 0.15f)));
        Controls.Add(Button("New", new Vector2(-0.24f, 0.21f), () => Edit(null)));
        Controls.Add(Button("Edit", new Vector2(0, 0.21f), () => { if (Selected != null) Edit(Selected); }));
        Controls.Add(Button("Duplicate", new Vector2(0.24f, 0.21f), () =>
        {
            if (Selected == null) return;
            var copy = Selected.Copy();
            copy.Id = Guid.NewGuid().ToString("N"); copy.Name += " copy";
            profile.Groups.Add(copy); Save();
        }));
        Controls.Add(Button("Move up", new Vector2(-0.24f, 0.275f), () => Move(-1)));
        Controls.Add(Button("Move down", new Vector2(0, 0.275f), () => Move(1)));
        Controls.Add(Button("Delete", new Vector2(0.24f, 0.275f), () =>
        {
            if (Selected == null) return;
            profile.Groups.Remove(Selected); Save(); // Referencing rules remain visible and paused.
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

    private InventoryGroupRecord Selected => table.SelectedRow?.UserData as InventoryGroupRecord;
    private void Edit(InventoryGroupRecord group) => MyGuiSandbox.AddScreen(new InventoryGroupEditor(session, group, value =>
    {
        var index = group == null ? -1 : profile.Groups.IndexOf(group);
        if (index < 0) profile.Groups.Add(value); else profile.Groups[index] = value;
        Save();
    }));
    private void Move(int offset)
    {
        var selected = Selected;
        var index = profile.Groups.IndexOf(selected);
        if (index < 0 || index + offset < 0 || index + offset >= profile.Groups.Count) return;
        profile.Groups.RemoveAt(index); profile.Groups.Insert(index + offset, selected); Save();
        table.SelectedRow = table.GetRow(index + offset);
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
        Controls.Add(box); return box;
    }
    protected MyGuiControlCheckbox Check(string label, float x, float y, bool value)
    {
        var box = new MyGuiControlCheckbox(new Vector2(x, y))
        { IsChecked = value, OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER };
        Controls.Add(box); Controls.Add(Label(label, new Vector2(x + 0.035f, y))); return box;
    }
}

internal sealed class InventoryGroupEditor : InventoryRuleEditor
{
    private readonly MechanicalInventorySession session;
    private readonly InventoryGroupRecord record;
    private readonly Action<InventoryGroupRecord> save;
    private MyGuiControlTextbox name;
    private MyGuiControlCombobox selector, value, role, itemType, item;
    private List<string> values;
    private List<string> itemTypes;
    private List<MyDefinitionId> items;

    public InventoryGroupEditor(MechanicalInventorySession session, InventoryGroupRecord record,
        Action<InventoryGroupRecord> save) : base("Edit inventory group")
    {
        this.session = session; this.record = record?.Copy() ?? new InventoryGroupRecord(); this.save = save;
    }
    protected override void CreateControls()
    {
        name = Text("GroupName", "Display name", -0.36f, -0.24f, 0.72f, record.Name);
        selector = Combo("GroupSelector", "Select blocks by", -0.36f, -0.12f, 0.22f,
            new[] { "All blocks", "Block family", "Block type", "Block definition", "Terminal group name", "Specific block", "Recipe output" }, (int)record.Selector);
        value = Combo("GroupValue", "Selection (resolved on this ship)", -0.12f, -0.12f, 0.48f, Array.Empty<string>());
        role = Combo("GroupRole", "Inventory role", -0.36f, 0f, 0.3f,
            new[] { "All roles" }.Concat(Enum.GetNames(typeof(InventoryRoleKind))), record.AllRoles ? 0 : (int)record.Role + 1);
        items = MyDefinitionManager.Static.GetPhysicalItemDefinitions().Select(d => d.Id)
            .OrderBy(id => Display(id), StringComparer.CurrentCultureIgnoreCase).ToList();
        itemTypes = new[] { string.Empty }.Concat(items.Select(id => id.TypeId.ToString()).Distinct().OrderBy(v => v)).ToList();
        itemType = Combo("GroupMaterialType", "Material / item category", -0.04f, 0f, 0.4f,
            itemTypes.Select(v => v.Length == 0 ? "All item categories" : v.Replace("MyObjectBuilder_", "")), itemTypes.IndexOf(record.ItemType ?? ""));
        item = Combo("GroupMaterial", "Exact material / item (optional)", -0.36f, 0.12f, 0.72f,
            new[] { "All items" }.Concat(items.Select(Display)), items.FindIndex(id => id.ToString() == record.ItemDefinitionId) + 1);
        Controls.Add(Label("Block selection, role and item filters are combined. Live constraints still apply.", new Vector2(-0.36f, 0.22f)));
        Controls.Add(Label("Terminal group names are saved; members are never frozen into block IDs.", new Vector2(-0.36f, 0.26f)));
        Controls.Add(Button("Apply", new Vector2(-0.12f, 0.34f), Apply));
        Controls.Add(Button("Cancel", new Vector2(0.12f, 0.34f), () => CloseScreen()));
        RefreshValues(); selector.ItemSelected += RefreshValues;
    }
    private void RefreshValues()
    {
        var kind = (InventoryGroupSelector)selector.GetSelectedKey();
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
        var saved = kind == InventoryGroupSelector.Family ? record.Family.ToString() : record.Value ?? "";
        if (kind == record.Selector && !labels.ContainsKey(saved)) labels[saved] = saved + " (not found)";
        values = labels.OrderBy(pair => pair.Value, StringComparer.CurrentCultureIgnoreCase).Select(pair => pair.Key).ToList();
        value.ClearItems();
        for (var i = 0; i < values.Count; i++) value.AddItem(i, labels[values[i]]);
        if (values.Count > 0) value.SelectItemByIndex(Math.Max(0, kind == record.Selector ? values.IndexOf(saved) : 0));
    }
    private void Apply()
    {
        if (string.IsNullOrWhiteSpace(name.Text) || value.GetSelectedKey() < 0) return;
        record.Name = name.Text.Trim(); record.Selector = (InventoryGroupSelector)selector.GetSelectedKey();
        record.Value = values[(int)value.GetSelectedKey()];
        if (record.Selector == InventoryGroupSelector.Family) record.Family = (InventorySectionKind)Enum.Parse(typeof(InventorySectionKind), record.Value);
        record.AllRoles = role.GetSelectedKey() == 0;
        record.Role = record.AllRoles ? InventoryRoleKind.GeneralCargo : (InventoryRoleKind)(role.GetSelectedKey() - 1);
        record.ItemType = itemTypes[(int)itemType.GetSelectedKey()];
        record.ItemDefinitionId = item.GetSelectedKey() > 0 ? items[(int)item.GetSelectedKey() - 1].ToString() : string.Empty;
        save(record); CloseScreen();
    }
    private static string Display(MyDefinitionId id) => MyDefinitionManager.Static.GetPhysicalItemDefinition(id)?.DisplayNameText ?? id.SubtypeName;
}
