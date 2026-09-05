using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ClientPlugin.Automation;
using ClientPlugin.Inventory;
using ClientPlugin.Profiles;
using ClientPlugin.Transfers;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Graphics.GUI;
using VRage;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin.UI;

internal abstract class UnifiedStorageScreen : MyGuiScreenBase
{
    protected UnifiedStorageScreen(string caption, Vector2? size = null)
        : base(
            new Vector2(0.5f, 0.5f),
            MyGuiConstants.SCREEN_BACKGROUND_COLOR,
            size ?? new Vector2(0.86f, 0.82f),
            false,
            null,
            MySandboxGame.Config.UIBkOpacity,
            MySandboxGame.Config.UIOpacity)
    {
        Caption = caption;
        EnabledBackgroundFade = true;
        m_closeOnEsc = true;
        m_drawEvenWithoutFocus = true;
        CanHideOthers = true;
        CanBeHidden = true;
        CloseButtonEnabled = true;
    }

    protected string Caption { get; }
    public override string GetFriendlyName() => "UnifiedStorage" + GetType().Name;

    public override void LoadContent()
    {
        base.LoadContent();
        RecreateControls(true);
    }

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        AddCaption(Caption).SetToolTip(UnifiedStorageHelp.Screen(Caption));
        m_closeButton?.SetToolTip(UnifiedStorageHelp.Button(Caption, "Close"));
        CreateControls();
    }

    protected abstract void CreateControls();

    protected MyGuiControlButton Button(string text, Vector2 position, Action click, float width = 0.13f) =>
        new(
            position,
            MyGuiControlButtonStyleEnum.Default,
            new Vector2(width, 0.045f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
            text: new StringBuilder(text),
            toolTip: UnifiedStorageHelp.Button(Caption, text),
            textScale: 0.65f,
            onButtonClick: _ => click(),
            isAutoscaleEnabled: true) { ShowTooltipWhenDisabled = true };

    protected static MyGuiControlLabel Label(string text, Vector2 position)
    {
        var label = new MyGuiControlLabel
        {
            Text = text, Position = position,
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, TextScale = 0.7f
        };
        var help = UnifiedStorageHelp.Field(text);
        if (help != null) label.SetToolTip(help);
        return label;
    }
}

internal sealed class MemberManagementScreen : UnifiedStorageScreen
{
    private readonly MechanicalInventorySession session;
    private readonly IReadOnlyList<InventoryRoleProjection> roles;
    private readonly ScopeProfile profile;
    private MultiSelectTable table;
    private MyGuiControlIndeterminateCheckbox manual;
    private MyGuiControlIndeterminateCheckbox reserved;
    private MyGuiControlIndeterminateCheckbox noDestination;
    private MyGuiControlButton apply;
    private readonly Dictionary<(long Block, int Inventory), (InventoryManagementFlags Value, InventoryManagementFlags Mask)> pending = new();
    private bool loading;

    public MemberManagementScreen(
        MechanicalInventorySession session,
        IReadOnlyList<InventoryRoleProjection> roles,
        ScopeProfile profile)
        : base("Unified Storage members")
    {
        this.session = session;
        this.roles = roles;
        this.profile = profile;
    }

    protected override void CreateControls()
    {
        var selected = new HashSet<(long, int)>(SelectedMembers.Select(member => (member.OwnerEntityId, member.InventoryIndex)));
        table = new MultiSelectTable
        {
            Name = "InventoryMembers",
            Position = new Vector2(-0.36f, -0.31f),
            Size = new Vector2(0.72f, 0.48f),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            ColumnsCount = 3,
            VisibleRowsCount = 15
        };
        table.SetCustomColumnWidths(new[] { 0.55f, 0.15f, 0.3f });
        table.SetColumnName(0, new StringBuilder("Block"));
        table.SetColumnName(1, new StringBuilder("Inv."));
        table.SetColumnName(2, new StringBuilder("State (* unsaved)"));
        table.SetToolTip(UnifiedStorageHelp.Wrap(MultiSelectTable.SelectionHelp + " Checkbox edits affect all selected rows. Changes stay buffered until Apply; Close or Escape discards them."));
        foreach (var member in roles.SelectMany(role => role.Members)
                     .GroupBy(member => (member.OwnerEntityId, member.InventoryIndex))
                     .Select(group => group.First())
                     .OrderBy(member => member.Owner.DisplayNameText)
                     .ThenBy(member => member.InventoryIndex))
        {
            var row = new MyGuiControlTable.Row(member);
            row.AddCell(new MyGuiControlTable.Cell(member.Owner.DisplayNameText,
                toolTip: $"{member.Owner.CubeGrid.DisplayName}\nBlock ID: {member.OwnerEntityId}\nSelect this row to edit its buffered exclusions."));
            row.AddCell(new MyGuiControlTable.Cell((member.InventoryIndex + 1).ToString(CultureInfo.InvariantCulture),
                toolTip: "Inventory number within this block. Reserved and Source Only affect only this inventory; Unmanaged affects the whole block."));
            row.AddCell(new MyGuiControlTable.Cell(
                UnifiedStorageHelp.ManagementState(Flags(member)), toolTip: "Pending exclusions for this row. Managed means no exclusions. An asterisk means Apply is still required."));
            table.Add(row);
        }
        table.SelectionChanged += LoadSelected;
        Controls.Add(table);

        manual = AddCheckbox("Unmanaged", new Vector2(-0.34f, 0.27f));
        reserved = AddCheckbox("Reserved", new Vector2(-0.08f, 0.27f));
        noDestination = AddCheckbox("Source Only", new Vector2(0.2f, 0.27f));
        manual.IsCheckedChanged += _ => Stage(InventoryManagementFlags.ManualBlock, manual.State == CheckStateEnum.Checked);
        reserved.IsCheckedChanged += _ => Stage(InventoryManagementFlags.ReservedInventory, reserved.State == CheckStateEnum.Checked);
        noDestination.IsCheckedChanged += _ => Stage(InventoryManagementFlags.NoUnifiedCargoDestination, noDestination.State == CheckStateEnum.Checked);
        apply = Button("Apply", new Vector2(-0.1f, 0.34f), Apply);
        Controls.Add(apply);
        Controls.Add(Button("Close", new Vector2(0.1f, 0.34f), () => CloseScreen()));
        if (table.RowsCount > 0)
        {
            table.RestoreSelection(Enumerable.Range(0, table.RowsCount).Where(index =>
                table.GetRow(index).UserData is InventoryDescriptor member && selected.Contains((member.OwnerEntityId, member.InventoryIndex))));
            if (selected.Count == 0) table.SetSelectedRow(0);
        }
        LoadSelected();
        RefreshDraftRows();
    }

    private MyGuiControlIndeterminateCheckbox AddCheckbox(string text, Vector2 position)
    {
        var checkbox = new MyGuiControlIndeterminateCheckbox(position)
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER
        };
        checkbox.SetToolTip(UnifiedStorageHelp.Field(text));
        checkbox.ShowTooltipWhenDisabled = true;
        Controls.Add(checkbox);
        Controls.Add(Label(text, position + new Vector2(0.035f, 0f)));
        return checkbox;
    }

    private void LoadSelected()
    {
        var members = SelectedMembers.ToArray();
        manual.Enabled = reserved.Enabled = members.Length > 0;
        var cargo = members.Where(member => member.Section.Kind == InventorySectionKind.UnifiedCargo).ToArray();
        noDestination.Enabled = cargo.Length > 0;
        loading = true;
        CheckStateEnum State(InventoryDescriptor[] rows, InventoryManagementFlags flag) =>
            rows.Length == 0 || rows.All(member => (Flags(member) & flag) == 0) ? CheckStateEnum.Unchecked :
            rows.All(member => (Flags(member) & flag) != 0) ? CheckStateEnum.Checked : CheckStateEnum.Indeterminate;
        manual.State = State(members, InventoryManagementFlags.ManualBlock);
        reserved.State = State(members, InventoryManagementFlags.ReservedInventory);
        noDestination.State = State(cargo, InventoryManagementFlags.NoUnifiedCargoDestination);
        table.SetColumnName(0, new StringBuilder($"Block ({members.Length} selected)"));
        loading = false;
    }

    private IEnumerable<InventoryDescriptor> SelectedMembers => table?.SelectedRows.Select(row => (InventoryDescriptor)row.UserData)
        ?? Enumerable.Empty<InventoryDescriptor>();

    private void Apply()
    {
        if (loading || pending.Count == 0)
            return;
        foreach (var pair in pending)
        {
            var current = profile.GetFlags(pair.Key.Block, pair.Key.Inventory);
            profile.SetFlags(pair.Key.Block, pair.Key.Inventory,
                (current & ~pair.Value.Mask) | (pair.Value.Value & pair.Value.Mask));
        }
        Plugin.Instance.Profiles.Save();
        pending.Clear();
        session.MarkContentsDirty();
        RefreshDraftRows();
        LoadSelected();
    }

    private InventoryManagementFlags Flags(InventoryDescriptor member)
    {
        var flags = profile.GetFlags(member.OwnerEntityId, member.InventoryIndex);
        return pending.TryGetValue((member.OwnerEntityId, member.InventoryIndex), out var edit)
            ? (flags & ~edit.Mask) | (edit.Value & edit.Mask) : flags;
    }

    private void Stage(InventoryManagementFlags flag, bool enabled)
    {
        if (loading)
            return;
        var selected = SelectedMembers.ToArray();
        var owners = new HashSet<long>(selected.Select(member => member.OwnerEntityId));
        var members = flag == InventoryManagementFlags.ManualBlock
            ? session.Scope.Inventories.Where(candidate => owners.Contains(candidate.OwnerEntityId))
            : selected.Where(member => flag != InventoryManagementFlags.NoUnifiedCargoDestination || member.Section.Kind == InventorySectionKind.UnifiedCargo);
        foreach (var descriptor in members)
        {
            var key = (descriptor.OwnerEntityId, descriptor.InventoryIndex);
            pending.TryGetValue(key, out var edit);
            var mask = edit.Mask | flag;
            var value = enabled ? edit.Value | flag : edit.Value & ~flag;
            // Returning a checkbox to the current saved value cancels only that edit.
            if ((profile.GetFlags(key.OwnerEntityId, key.InventoryIndex) & flag) == (value & flag))
                mask &= ~flag;
            if (mask == InventoryManagementFlags.None) pending.Remove(key);
            else pending[key] = (value, mask);
        }
        RefreshDraftRows();
        LoadSelected();
    }

    private void RefreshDraftRows()
    {
        for (var index = 0; index < table.RowsCount; index++)
        {
            var row = table.GetRow(index);
            var member = (InventoryDescriptor)row.UserData;
            var edited = pending.ContainsKey((member.OwnerEntityId, member.InventoryIndex));
            var cell = row.GetCell(2);
            var state = UnifiedStorageHelp.ManagementState(Flags(member));
            cell.Text.Clear().Append(edited ? "* " : string.Empty).Append(state);
            cell.ToolTip.ToolTips.Clear();
            cell.ToolTip.AddToolTip(state + (edited
                ? "\nUnsaved: Apply saves this together with all other edited rows."
                : "\nSaved exclusions. Select this row to stage changes."));
        }
        apply.Enabled = pending.Count > 0;
        apply.SetToolTip(UnifiedStorageHelp.Wrap($"{pending.Count} inventories have pending edits. " +
            UnifiedStorageHelp.Button(Caption, "Apply")));
    }
}

internal sealed class CraftingTargetsScreen : UnifiedStorageScreen
{
    private readonly MechanicalInventorySession session;
    private readonly ScopeProfile profile;
    private readonly Func<InventoryDescriptor, InventoryManagementFlags> getFlags;
    private MultiSelectTable table;
    private MyGuiControlTextbox target;
    private MyGuiControlCheckbox maintain;
    private MyGuiControlTextbox threshold;
    private MyGuiControlButton saveTarget;
    private long nextStatusRefresh;
    private IReadOnlyList<ComponentTargetStatus> statuses;
    private string searchText = string.Empty;

    public CraftingTargetsScreen(
        MechanicalInventorySession session,
        ScopeProfile profile,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags)
        : base("Crafting targets")
    {
        this.session = session;
        this.profile = profile;
        this.getFlags = getFlags;
    }

    protected override void CreateControls()
    {
        var selectedComponents = SelectedTargets.Select(status => status.ComponentId).ToArray();
        statuses = ComponentTargetEngine.Evaluate(session.Scope, profile, getFlags);
        var search = new MyGuiControlSearchBox(
            new Vector2(-0.36f, -0.32f),
            new Vector2(0.72f, 0.04f),
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER);
        search.Name = "CraftingSearch";
        search.TextBox.SetToolTip("Filter craftable items by name or definition.");
        search.SearchText = searchText;
        search.OnTextChanged += value =>
        {
            searchText = value ?? string.Empty;
            PopulateTable();
        };
        Controls.Add(search);
        table = new MultiSelectTable
        {
            Name = "CraftingTargets",
            Position = new Vector2(-0.36f, -0.27f),
            Size = new Vector2(0.72f, 0.42f),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            ColumnsCount = 6,
            VisibleRowsCount = 12
        };
        table.SetCustomColumnWidths(new[] { 0.29f, 0.10f, 0.10f, 0.10f, 0.20f, 0.21f });
        foreach (var (index, name) in new[] { "Item", "Stock", "Queued", "Target", "Recipe", "Status" }.Select((name, index) => (index, name)))
            table.SetColumnName(index, new StringBuilder(name));
        table.SelectionChanged += LoadSelected;
        Controls.Add(table);

        table.SetToolTip("Only items craftable by this construct's assemblers. Recipes are chosen automatically; existing saved recipe choices are retained.");

        Controls.Add(Label("Target", new Vector2(-0.36f, 0.21f)));
        target = new MyGuiControlTextbox(new Vector2(-0.28f, 0.21f), "0", 18, type: MyGuiControlTextboxType.DigitsOnly)
        {
            Name = "CraftingTargetQuantity",
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER,
            Size = new Vector2(0.15f, 0.04f)
        };
        target.SetToolTip(UnifiedStorageHelp.Field("Target"));
        Controls.Add(target);

        maintain = new MyGuiControlCheckbox(new Vector2(-0.36f, 0.28f))
        {
            Name = "MaintainCraftingTargets",
            IsChecked = profile.MaintainComponentTargets,
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER
        };
        maintain.SetToolTip(UnifiedStorageHelp.Field("Maintain targets locally"));
        Controls.Add(maintain);
        Controls.Add(Label("Maintain targets locally", new Vector2(-0.325f, 0.28f)));
        Controls.Add(Label("Start threshold", new Vector2(0.08f, 0.28f)));
        threshold = new MyGuiControlTextbox(new Vector2(0.26f, 0.28f),
            profile.ComponentStartThreshold.ToString("0.##", CultureInfo.InvariantCulture),
            8, type: MyGuiControlTextboxType.Normal)
        {
            Name = "CraftingStartThreshold",
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER,
            Size = new Vector2(0.1f, 0.04f)
        };
        Controls.Add(threshold);
        threshold.SetToolTip(UnifiedStorageHelp.Field("Start threshold"));
        saveTarget = Button("Save target", new Vector2(0.14f, 0.21f), SaveSelected, 0.20f);
        Controls.Add(saveTarget);
        Controls.Add(Button("Craft deficits", new Vector2(-0.14f, 0.35f), Craft, 0.20f));
        Controls.Add(Button("Close", new Vector2(0.14f, 0.35f), () => CloseScreen()));
        PopulateTable(selectedComponents);
    }

    private IEnumerable<ComponentTargetStatus> SelectedTargets => table?.SelectedRows.Select(row => (ComponentTargetStatus)row.UserData)
        ?? Enumerable.Empty<ComponentTargetStatus>();

    private void LoadSelected()
    {
        var selected = SelectedTargets.ToArray();
        var status = selected.FirstOrDefault();
        target.Enabled = saveTarget.Enabled = status != null;
        if (status == null)
        {
            target.Text = "0";
            return;
        }
        target.Text = selected.All(item => item.Target == status.Target)
            ? ((decimal)status.Target).ToString("0", CultureInfo.InvariantCulture) : "";
        target.SetToolTip("Desired stock. Zero disables the goal; a blank value indicates mixed targets.");
    }

    private void PopulateTable(IEnumerable<MyDefinitionId> selectedComponents = null)
    {
        if (table == null)
            return;
        var selected = (selectedComponents ?? SelectedTargets.Select(status => status.ComponentId)).ToArray();
        table.Clear();
        foreach (var status in statuses.Where(status => string.IsNullOrWhiteSpace(searchText) ||
                     DisplayName(status.ComponentId).IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                     status.ComponentId.ToString().IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0))
        {
            var row = new MyGuiControlTable.Row(status);
            row.AddCell(new MyGuiControlTable.Cell(
                "   " + DisplayName(status.ComponentId),
                icon: ComponentIcon(status.ComponentId),
                iconOriginAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER));
            row.AddCell(new MyGuiControlTable.Cell(status.Stock.ToString()));
            row.AddCell(new MyGuiControlTable.Cell(status.Queued.ToString()));
            row.AddCell(new MyGuiControlTable.Cell(status.Target.ToString()));
            row.AddCell(new MyGuiControlTable.Cell(RecipeName(status), toolTip: RecipeHelp(status)));
            row.AddCell(new MyGuiControlTable.Cell($"{status.Deficit} · {status.Status}", toolTip: status.Status));
            table.Add(row);
        }
        if (table.RowsCount > 0)
        {
            table.RestoreSelection(Enumerable.Range(0, table.RowsCount).Where(index => selected.Contains(((ComponentTargetStatus)table.GetRow(index).UserData).ComponentId)));
            if (selected.Length == 0) table.SetSelectedRow(0);
            table.ScrollToSelection();
        }
        // The first selection after clearing the table does not raise ItemSelected.
        LoadSelected();
    }

    public override bool Update(bool hasFocus)
    {
        var result = base.Update(hasFocus);
        if (!hasFocus || table == null || MySandboxGame.TotalGamePlayTimeInMilliseconds < nextStatusRefresh)
            return result;
        nextStatusRefresh = MySandboxGame.TotalGamePlayTimeInMilliseconds + 1000;
        statuses = ComponentTargetEngine.Evaluate(session.Refresh().Scope, profile, getFlags);
        var latest = statuses.ToDictionary(status => status.ComponentId);
        for (var index = 0; index < table.RowsCount; index++)
        {
            var row = table.GetRow(index);
            var status = (ComponentTargetStatus)row.UserData;
            if (!latest.TryGetValue(status.ComponentId, out var current)) continue;
            status.Stock = current.Stock;
            status.Queued = current.Queued;
            status.Deficit = current.Deficit;
            status.Status = current.Status;
            status.Blueprint = current.Blueprint;
            row.GetCell(1).Text.Clear().Append(status.Stock);
            row.GetCell(2).Text.Clear().Append(status.Queued);
            row.GetCell(4).Text.Clear().Append(RecipeName(status));
            row.GetCell(4).ToolTip.ToolTips.Clear();
            row.GetCell(4).ToolTip.AddToolTip(RecipeHelp(status));
            row.GetCell(5).Text.Clear().Append($"{status.Deficit} · {status.Status}");
            row.GetCell(5).ToolTip.ToolTips.Clear();
            row.GetCell(5).ToolTip.AddToolTip(status.Status);
        }
        // Keep the unsaved quantity and the table's selection/scroll intact.
        return result;
    }

    private void SaveSelected()
    {
        var selected = SelectedTargets.ToArray();
        if (selected.Length == 0)
            return;
        if (selected.Length > 1 && string.IsNullOrWhiteSpace(target.Text))
        {
            Sandbox.ModAPI.MyAPIGateway.Utilities?.ShowNotification("Enter a quantity for the selected items. Use 0 to disable their goals.", 5000);
            return;
        }
        var text = string.IsNullOrWhiteSpace(target.Text) ? "0" : target.Text;
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ||
            amount < 0 || amount > 1000000000000m || amount != decimal.Truncate(amount))
        {
            Sandbox.ModAPI.MyAPIGateway.Utilities?.ShowNotification("Target must be a whole number from 0 to 1,000,000,000,000. Blank disables it.", 5000);
            return;
        }
        foreach (var status in selected)
        {
            var record = profile.ComponentTargets.FirstOrDefault(candidate =>
                string.Equals(candidate.DefinitionId, status.ComponentId.ToString(), StringComparison.Ordinal));
            if (record == null)
            {
                record = new ComponentTargetRecord { DefinitionId = status.ComponentId.ToString() };
                profile.ComponentTargets.Add(record);
            }
            record.Amount = Math.Max(0, decimal.Truncate(amount));
        }
        SaveGlobalSettings();
        Plugin.Instance.Profiles.Save();
        RecreateControls(false);
    }

    private void Craft()
    {
        SaveGlobalSettings();
        Plugin.Instance.Profiles.Save();
        if (CompanionActions.TryRun(session.Scope, profile, Shared.Companion.ShipAction.QueueComponents)) return;
        statuses = ComponentTargetEngine.Evaluate(session.Scope, profile, getFlags);
        Plugin.Instance.ProductionQueue.Enqueue(ComponentTargetEngine.PlanDeficits(statuses));
    }

    private void SaveGlobalSettings()
    {
        profile.MaintainComponentTargets = maintain.IsChecked;
        if (decimal.TryParse(threshold.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            profile.ComponentStartThreshold = Math.Max(0.01m, Math.Min(1m, value));
    }

    public override bool CloseScreen(bool isUnloading = false)
    {
        if (maintain != null && threshold != null)
        {
            SaveGlobalSettings();
            Plugin.Instance?.Profiles?.Save();
        }
        return base.CloseScreen(isUnloading);
    }

    private static string DisplayName(MyDefinitionId id) => DefinitionLabels.Item(
        MyDefinitionManager.Static.GetPhysicalItemDefinition(id)?.DisplayNameText, id.TypeId.ToString(), id.SubtypeName);

    private static string RecipeName(ComponentTargetStatus status) => status.Blueprint == null ? "Unavailable" :
        DefinitionLabels.SingleLine(status.Blueprint.DisplayNameText, status.Blueprint.Id.SubtypeName);

    private static string RecipeHelp(ComponentTargetStatus status) => status.Blueprint == null ? "No supported recipe." :
        UnifiedStorageHelp.Wrap(status.Blueprint.DisplayNameText + "\n" + status.Blueprint.Id);

    private static MyGuiHighlightTexture? ComponentIcon(MyDefinitionId id)
    {
        var icon = MyDefinitionManager.Static.GetPhysicalItemDefinition(id)?.Icons?.FirstOrDefault();
        return string.IsNullOrEmpty(icon)
            ? null
            : new MyGuiHighlightTexture
            {
                Normal = icon,
                Highlight = icon,
                Focus = icon,
                SizePx = new Vector2(24f, 24f)
            };
    }
}

internal sealed class LoadoutScreen : UnifiedStorageScreen
{
    private readonly MechanicalInventorySession session;
    private readonly ScopeProfile profile;
    private readonly string groupId;
    private readonly Func<InventoryDescriptor, InventoryManagementFlags> getFlags;
    private readonly Action<TransferPlan> enqueue;
    private MultiSelectTable rules;
    private int nextStatusRefresh;

    public LoadoutScreen(MechanicalInventorySession session, InventoryProjection projection, ScopeProfile profile,
        InventorySectionKey section, Func<InventoryDescriptor, InventoryManagementFlags> getFlags,
        Action<TransferPlan> enqueue) : base("Loadouts")
    {
        this.session = session; this.profile = profile; groupId = section.GroupId;
        this.getFlags = getFlags; this.enqueue = enqueue;
    }

    protected override void CreateControls()
    {
        var selected = SelectedRules.ToArray();
        rules = new MultiSelectTable
        {
            Name = "LoadoutRules", Position = new Vector2(-0.36f, -0.31f), Size = new Vector2(0.72f, 0.4f),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP, ColumnsCount = 5, VisibleRowsCount = 12
        };
        rules.SetCustomColumnWidths(new[] { 0.22f, 0.24f, 0.16f, 0.28f, 0.1f });
        foreach (var pair in new[] { "Group", "Item", "Target", "State", "Local" }.Select((name, index) => (name, index)))
            rules.SetColumnName(pair.index, new StringBuilder(pair.name));
        foreach (var record in profile.Loadouts.Where(rule => groupId == null || rule.GroupId == groupId))
        {
            var row = new MyGuiControlTable.Row(record);
            row.AddCell(new MyGuiControlTable.Cell(profile.Groups.FirstOrDefault(g => g.Id == record.GroupId)?.Name ?? "Group not found"));
            row.AddCell(new MyGuiControlTable.Cell(MyDefinitionId.TryParse(record.ItemDefinitionId, out var id)
                ? MyDefinitionManager.Static.GetPhysicalItemDefinition(id)?.DisplayNameText ?? id.SubtypeName : record.ItemDefinitionId));
            row.AddCell(new MyGuiControlTable.Cell(record.Amount.ToString(CultureInfo.InvariantCulture) + (record.PerMember ? " each" : " total")));
            row.AddCell(new MyGuiControlTable.Cell(LoadoutEngine.Status(session.Refresh().Scope, profile, record, getFlags)));
            row.AddCell(new MyGuiControlTable.Cell(record.Maintain ? "Yes" : "No"));
            rules.Add(row);
        }
        Controls.Add(rules);
        rules.SetToolTip(UnifiedStorageHelp.Wrap(MultiSelectTable.SelectionHelp + " Delete affects all selected rules. Edit requires one row. Apply loadouts still runs all rules in this scope."));
        rules.RestoreSelection(Enumerable.Range(0, rules.RowsCount).Where(index => selected.Contains((LoadoutRecord)rules.GetRow(index).UserData)));
        Controls.Add(Label("Targets, supply and excess returns use configurable inventory groups.", new Vector2(-0.36f, 0.16f)));
        Controls.Add(Label("Overlapping target rules are paused. Missing groups never broaden scope.", new Vector2(-0.36f, 0.20f)));
        Controls.Add(Button("New rule", new Vector2(-0.24f, 0.27f), () => Edit(null)));
        var edit = Button("Edit selected", new Vector2(0, 0.27f), () => { if (SelectedRules.Count() == 1) Edit(SelectedRules.Single()); });
        edit.SetToolTip("Edit one selected loadout. Select exactly one row to enable this action.");
        Controls.Add(edit);
        rules.SelectionChanged += () => edit.Enabled = SelectedRules.Count() == 1;
        edit.Enabled = SelectedRules.Count() == 1;
        Controls.Add(Button("Delete selected", new Vector2(0.24f, 0.27f), () =>
        {
            foreach (var rule in SelectedRules.ToArray()) profile.Loadouts.Remove(rule);
            Save();
        }));
        Controls.Add(Button("Apply loadouts", new Vector2(-0.12f, 0.34f), () =>
        {
            if (Plugin.Instance.Transfers.PendingCount != 0) return;
            if (CompanionActions.TryRun(session.Scope, profile, Shared.Companion.ShipAction.ApplyLoadouts, groupId: groupId)) return;
            var plans = LoadoutEngine.Plan(session.Refresh(), profile, getFlags, groupId: groupId);
            foreach (var plan in plans) enqueue(plan);
            if (plans.Count == 0)
                Sandbox.ModAPI.MyAPIGateway.Utilities?.ShowNotification("No eligible transfers. Check rule state, supply, returns and capacity.", 4000);
        }));
        Controls.Add(Button("Close", new Vector2(0.12f, 0.34f), () => CloseScreen()));
    }
    public override bool Update(bool hasFocus)
    {
        var result = base.Update(hasFocus);
        if (!hasFocus || rules == null || MySandboxGame.TotalGamePlayTimeInMilliseconds < nextStatusRefresh)
            return result;
        nextStatusRefresh = MySandboxGame.TotalGamePlayTimeInMilliseconds + 1000;
        var scope = session.Refresh().Scope;
        for (var i = 0; i < rules.RowsCount; i++)
        {
            var row = rules.GetRow(i);
            row.GetCell(3).Text.Clear().Append(LoadoutEngine.Status(scope, profile, (LoadoutRecord)row.UserData, getFlags));
        }
        return result;
    }

    private IEnumerable<LoadoutRecord> SelectedRules => rules?.SelectedRows.Select(row => (LoadoutRecord)row.UserData)
        ?? Enumerable.Empty<LoadoutRecord>();
    private void Edit(LoadoutRecord record) => MyGuiSandbox.AddScreen(new LoadoutRuleEditor(session, profile, record, groupId, value =>
    {
        var index = record == null ? -1 : profile.Loadouts.IndexOf(record);
        if (index < 0) profile.Loadouts.Add(value); else profile.Loadouts[index] = value;
        Save();
        rules.RestoreSelection(Enumerable.Range(0, rules.RowsCount).Where(row => ReferenceEquals(rules.GetRow(row).UserData, value)));
    }));
    private void Save()
    {
        Plugin.Instance.Profiles.Save(); session.MarkContentsDirty(); RecreateControls(false);
    }
}

internal sealed class LoadoutRuleEditor : InventoryRuleEditor
{
    private readonly MechanicalInventorySession session;
    private readonly ScopeProfile profile;
    private readonly LoadoutRecord original;
    private readonly string initialGroup;
    private readonly Action<LoadoutRecord> save;
    private MyGuiControlCombobox target, supply, returns, role, item, policy;
    private MyGuiControlTextbox amount;
    private MyGuiControlCheckbox each, maintain, nonWorking;
    private MyGuiControlLabel validation;
    private List<string> groupIds;
    private List<MyDefinitionId> items;

    public LoadoutRuleEditor(MechanicalInventorySession session, ScopeProfile profile, LoadoutRecord original,
        string initialGroup, Action<LoadoutRecord> save) : base("Edit loadout")
    {
        this.session = session; this.profile = profile; this.original = original;
        this.initialGroup = initialGroup; this.save = save;
    }
    protected override void CreateControls()
    {
        groupIds = new[] { string.Empty }.Concat(profile.Groups.Select(g => g.Id))
            .Concat(new[] { original?.GroupId, original?.SupplyGroupId, original?.ReturnGroupId }
                .Where(id => !string.IsNullOrEmpty(id))).Distinct().ToList();
        string Name(string id) => id.Length == 0 ? "None (disabled)" : profile.Groups.FirstOrDefault(g => g.Id == id)?.Name ?? "Group not found";
        var cargo = InventoryGroupRecord.DefaultId(InventorySectionKind.UnifiedCargo);
        target = Combo("LoadoutTarget", "Target group", -0.36f, -0.24f, 0.22f, groupIds.Select(Name),
            groupIds.IndexOf(original?.GroupId ?? initialGroup ?? profile.Groups.FirstOrDefault()?.Id ?? ""));
        supply = Combo("LoadoutSupply", "Supply group", -0.12f, -0.24f, 0.22f, groupIds.Select(Name),
            groupIds.IndexOf(original == null ? cargo : original.SupplyGroupId ?? ""));
        returns = Combo("LoadoutReturns", "Excess return group", 0.12f, -0.24f, 0.24f, groupIds.Select(Name),
            groupIds.IndexOf(original == null ? cargo : original.ReturnGroupId ?? ""));
        role = Combo("LoadoutRole", "Inventory role", -0.36f, -0.10f, 0.22f,
            Enum.GetNames(typeof(InventoryRoleKind)), (int)(original?.Role ?? InventoryRoleKind.GeneralCargo));
        item = Combo("LoadoutItem", "Item / material", -0.12f, -0.10f, 0.48f, Array.Empty<string>());
        amount = Text("LoadoutQuantity", "Target quantity", -0.36f, 0.04f, 0.22f,
            (original?.Amount ?? 0m).ToString(CultureInfo.InvariantCulture), 18);
        policy = Combo("LoadoutPolicy", "Distribution policy", -0.12f, 0.04f, 0.48f,
            Enum.GetNames(typeof(DistributionPolicy)), (int)(original?.Policy ?? DistributionPolicy.EvenByItem));
        each = Check("Per inventory", -0.34f, 0.14f, original?.PerMember ?? true);
        maintain = Check("Maintain locally", -0.08f, 0.14f, original?.Maintain ?? false);
        nonWorking = Check("Include non-working", 0.18f, 0.14f, original?.IncludeNonWorking ?? false);
        Controls.Add(Label("None disables supply or excess returns. Target inventories never supply other rules.", new Vector2(-0.36f, 0.22f)));
        validation = Label(original != null && original.TargetKind != LoadoutTargetKind.Section
            ? "Legacy block/definition restriction retained unless target group changes." : "", new Vector2(-0.36f, 0.27f));
        Controls.Add(validation);
        Controls.Add(Button("Apply", new Vector2(-0.12f, 0.34f), Apply));
        Controls.Add(Button("Cancel", new Vector2(0.12f, 0.34f), () => CloseScreen()));
        target.ItemSelected += () => RefreshItems(true);
        role.ItemSelected += () => RefreshItems(false);
        RefreshItems(original == null);
    }
    private void RefreshItems(bool chooseRole)
    {
        var group = profile.Groups.FirstOrDefault(g => g.Id == groupIds[(int)target.GetSelectedKey()]);
        var projected = group == null ? Array.Empty<InventoryRoleProjection>() : InventoryGroups.Build(session.Refresh(), new ScopeProfile
        { GroupSchemaVersion = InventoryGroupRecord.SchemaVersion, Groups = new() { group } }).Roles.ToArray();
        if (chooseRole)
        {
            var roles = projected.Select(r => r.Role).Distinct().ToArray();
            if (roles.Length > 0) role.SelectItemByKey((long)roles[0], sendEvent: false);
        }
        var roleKind = (InventoryRoleKind)role.GetSelectedKey();
        var restoringSavedItem = items == null && original != null;
        var previous = items != null && item.GetSelectedKey() >= 0 ? items[(int)item.GetSelectedKey()].ToString() : original?.ItemDefinitionId;
        items = MyDefinitionManager.Static.GetPhysicalItemDefinitions().Select(d => d.Id)
            .Where(id => projected.Any(r => r.Role == roleKind && r.Accepts(id)))
            .OrderBy(Display, StringComparer.CurrentCultureIgnoreCase).ToList();
        // Retain an unavailable saved item for repair, not an incompatible choice from another target/role.
        if (restoringSavedItem && MyDefinitionId.TryParse(previous, out var saved) && !items.Contains(saved)) items.Add(saved);
        item.ClearItems();
        for (var i = 0; i < items.Count; i++) item.AddItem(i, Display(items[i]), toolTip: items[i].ToString());
        if (items.Count > 0) item.SelectItemByIndex(Math.Max(0, items.FindIndex(id => id.ToString() == previous)));
    }
    private void Apply()
    {
        if (target.GetSelectedKey() <= 0 || item.GetSelectedKey() < 0 ||
            !decimal.TryParse(amount.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var quantity) ||
            quantity < 0 || quantity > (decimal)MyFixedPoint.MaxValue)
        {
            validation.Text = "Choose target group, item and a valid non-negative quantity."; return;
        }
        var id = groupIds[(int)target.GetSelectedKey()];
        var sameTarget = original?.GroupId == id;
        var record = new LoadoutRecord
        {
            GroupId = id, SupplyGroupId = groupIds[(int)supply.GetSelectedKey()], ReturnGroupId = groupIds[(int)returns.GetSelectedKey()],
            TargetKind = sameTarget ? original.TargetKind : LoadoutTargetKind.Section,
            TargetBlockEntityId = sameTarget ? original.TargetBlockEntityId : 0,
            TargetBlockDefinitionId = sameTarget ? original.TargetBlockDefinitionId : null,
            Role = (InventoryRoleKind)role.GetSelectedKey(), ItemDefinitionId = items[(int)item.GetSelectedKey()].ToString(),
            Amount = quantity, PerMember = each.IsChecked, Maintain = maintain.IsChecked,
            IncludeNonWorking = nonWorking.IsChecked, Policy = (DistributionPolicy)policy.GetSelectedKey()
        };
        save(record); CloseScreen();
    }
    private static string Display(MyDefinitionId id) => DefinitionLabels.Item(
        MyDefinitionManager.Static.GetPhysicalItemDefinition(id)?.DisplayNameText, id.TypeId.ToString(), id.SubtypeName);
}

internal sealed class RefineryPriorityScreen : UnifiedStorageScreen
{
    private readonly MechanicalInventorySession session;
    private readonly ScopeProfile profile;
    private readonly Func<InventoryDescriptor, InventoryManagementFlags> getFlags;
    private readonly Action sortNow;
    private MultiSelectTable table;
    private MyGuiControlCheckbox automatic;
    private MyGuiControlCheckbox autoSort;
    private RefineryPriorityModel model;

    public RefineryPriorityScreen(
        MechanicalInventorySession session,
        ScopeProfile profile,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags,
        Action sortNow)
        : base("Refinery ore priority")
    {
        this.session = session;
        this.profile = profile;
        this.getFlags = getFlags;
        this.sortNow = sortNow;
    }

    protected override void CreateControls()
    {
        var selectedInputs = SelectedInputs.ToArray();
        model = RefineryPriorityEngine.Build(session.Scope, profile, getFlags);
        if (!profile.RefineryPriority.Automatic)
        {
            var changed = false;
            foreach (var id in model.OrderedInputs.Select(id => id.ToString()))
                if (!profile.RefineryPriority.ManualDefinitionIds.Contains(id))
                {
                    profile.RefineryPriority.ManualDefinitionIds.Add(id);
                    changed = true;
                }
            if (changed)
                Plugin.Instance.Profiles.Save();
        }
        table = new MultiSelectTable
        {
            Name = "RefineryInputs",
            Position = new Vector2(-0.36f, -0.31f),
            Size = new Vector2(0.72f, 0.48f),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            ColumnsCount = 3,
            VisibleRowsCount = 15
        };
        table.SetCustomColumnWidths(new[] { 0.15f, 0.6f, 0.25f });
        table.SetColumnName(0, new StringBuilder("#"));
        table.SetColumnName(1, new StringBuilder("Input"));
        table.SetColumnName(2, new StringBuilder("Accepting"));
        table.SetToolTip(UnifiedStorageHelp.Wrap(MultiSelectTable.SelectionHelp + " Higher rows are processed first. Pin and move affect all selected ores and save immediately. Sort now reorders physical inputs."));
        for (var index = 0; index < model.OrderedInputs.Count; index++)
        {
            var id = model.OrderedInputs[index];
            var row = new MyGuiControlTable.Row(id);
            var pinned = profile.RefineryPriority.PinnedDefinitionIds.Contains(id.ToString());
            row.AddCell(new MyGuiControlTable.Cell(
                (pinned ? "P " : string.Empty) + (index + 1).ToString(CultureInfo.InvariantCulture),
                toolTip: pinned ? "Pinned priority" : null));
            row.AddCell(new MyGuiControlTable.Cell(
                MyDefinitionManager.Static.GetPhysicalItemDefinition(id)?.DisplayNameText ?? id.SubtypeName));
            row.AddCell(new MyGuiControlTable.Cell(model.AcceptingRefineryCounts[id].ToString(CultureInfo.InvariantCulture),
                toolTip: "Number of refineries whose definitions support this ore. Support alone does not guarantee available power, capacity or access."));
            table.Add(row);
        }
        Controls.Add(table);
        if (table.RowsCount > 0)
        {
            table.RestoreSelection(Enumerable.Range(0, table.RowsCount).Where(index => selectedInputs.Contains((MyDefinitionId)table.GetRow(index).UserData)));
            if (selectedInputs.Length == 0) table.SetSelectedRow(0);
        }
        automatic = Checkbox("Automatic priority", new Vector2(-0.34f, 0.27f), profile.RefineryPriority.Automatic,
            value => profile.RefineryPriority.Automatic = value);
        autoSort = Checkbox("Auto-sort inputs", new Vector2(0.02f, 0.27f), profile.RefineryPriority.AutoSortInputs,
            value => profile.RefineryPriority.AutoSortInputs = value);
        Controls.Add(Button("Pin / unpin", new Vector2(-0.27f, 0.34f), TogglePin));
        Controls.Add(Button("Move up", new Vector2(-0.09f, 0.34f), () => Move(-1)));
        Controls.Add(Button("Move down", new Vector2(0.09f, 0.34f), () => Move(1)));
        Controls.Add(Button("Sort now", new Vector2(0.27f, 0.34f), sortNow));
    }

    private MyGuiControlCheckbox Checkbox(string label, Vector2 position, bool value, Action<bool> changed)
    {
        var result = new MyGuiControlCheckbox(position)
        {
            IsChecked = value,
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER
        };
        result.SetToolTip(UnifiedStorageHelp.Field(label));
        result.IsCheckedChanged += checkbox =>
        {
            changed(checkbox.IsChecked);
            Plugin.Instance.Profiles.Save();
            RecreateControls(false);
        };
        Controls.Add(result);
        Controls.Add(Label(label, position + new Vector2(0.035f, 0f)));
        return result;
    }

    private void TogglePin()
    {
        var selected = SelectedInputs.Select(id => id.ToString()).ToArray();
        if (selected.Length == 0) return;
        var list = profile.RefineryPriority.PinnedDefinitionIds;
        var pin = selected.Any(value => !list.Contains(value));
        foreach (var value in selected)
            if (pin) { if (!list.Contains(value)) list.Add(value); }
            else list.Remove(value);
        Plugin.Instance.Profiles.Save();
        RecreateControls(false);
    }

    private void Move(int direction)
    {
        var selected = SelectedInputs.Select(id => id.ToString()).ToArray();
        if (selected.Length == 0) return;
        var list = profile.RefineryPriority.Automatic
            ? profile.RefineryPriority.PinnedDefinitionIds
            : profile.RefineryPriority.ManualDefinitionIds;
        foreach (var value in selected)
            if (!list.Contains(value)) list.Add(value);
        ListSelection.Move(list, selected, direction);
        Plugin.Instance.Profiles.Save();
        RecreateControls(false);
    }

    private IEnumerable<MyDefinitionId> SelectedInputs => table?.SelectedRowsIndexes.OrderBy(index => index)
        .Where(index => index >= 0 && index < table.RowsCount).Select(index => (MyDefinitionId)table.GetRow(index).UserData)
        ?? Enumerable.Empty<MyDefinitionId>();
}
