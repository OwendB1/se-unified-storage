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
        AddCaption(Caption);
        CreateControls();
    }

    protected abstract void CreateControls();

    protected static MyGuiControlButton Button(string text, Vector2 position, Action click, float width = 0.13f) =>
        new(
            position,
            MyGuiControlButtonStyleEnum.Default,
            new Vector2(width, 0.045f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
            text: new StringBuilder(text),
            textScale: 0.65f,
            onButtonClick: _ => click(),
            isAutoscaleEnabled: true);

    protected static MyGuiControlLabel Label(string text, Vector2 position) => new()
    {
        Text = text,
        Position = position,
        OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER,
        TextScale = 0.7f
    };
}

internal sealed class MemberManagementScreen : UnifiedStorageScreen
{
    private readonly MechanicalInventorySession session;
    private readonly IReadOnlyList<InventoryRoleProjection> roles;
    private readonly ScopeProfile profile;
    private MyGuiControlTable table;
    private MyGuiControlCheckbox manual;
    private MyGuiControlCheckbox reserved;
    private MyGuiControlCheckbox noDestination;
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
        table = new MyGuiControlTable
        {
            Position = new Vector2(-0.36f, -0.31f),
            Size = new Vector2(0.72f, 0.48f),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            ColumnsCount = 3,
            VisibleRowsCount = 15
        };
        table.SetCustomColumnWidths(new[] { 0.55f, 0.15f, 0.3f });
        table.SetColumnName(0, new StringBuilder("Block"));
        table.SetColumnName(1, new StringBuilder("Inv."));
        table.SetColumnName(2, new StringBuilder("State"));
        foreach (var member in roles.SelectMany(role => role.Members)
                     .GroupBy(member => (member.OwnerEntityId, member.InventoryIndex))
                     .Select(group => group.First())
                     .OrderBy(member => member.Owner.DisplayNameText)
                     .ThenBy(member => member.InventoryIndex))
        {
            var row = new MyGuiControlTable.Row(member);
            row.AddCell(new MyGuiControlTable.Cell(member.Owner.DisplayNameText));
            row.AddCell(new MyGuiControlTable.Cell((member.InventoryIndex + 1).ToString(CultureInfo.InvariantCulture)));
            row.AddCell(new MyGuiControlTable.Cell(
                profile.GetFlags(member.OwnerEntityId, member.InventoryIndex).ToString()));
            table.Add(row);
        }
        table.ItemSelected += (_, _) => LoadSelected();
        Controls.Add(table);

        manual = AddCheckbox("Manual block", new Vector2(-0.34f, 0.27f));
        reserved = AddCheckbox("Reserved / not counted", new Vector2(-0.08f, 0.27f));
        noDestination = AddCheckbox("Not a cargo destination", new Vector2(0.2f, 0.27f));
        noDestination.Enabled = roles.SelectMany(role => role.Members).Any(member => member.Section.Kind == InventorySectionKind.UnifiedCargo);
        Controls.Add(Button("Apply", new Vector2(-0.1f, 0.34f), Apply));
        Controls.Add(Button("Close", new Vector2(0.1f, 0.34f), () => CloseScreen()));
        if (table.RowsCount > 0)
            table.SetSelectedRow(0);
    }

    private MyGuiControlCheckbox AddCheckbox(string text, Vector2 position)
    {
        var checkbox = new MyGuiControlCheckbox(position)
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER
        };
        Controls.Add(checkbox);
        Controls.Add(Label(text, position + new Vector2(0.035f, 0f)));
        return checkbox;
    }

    private void LoadSelected()
    {
        if (table.SelectedRow?.UserData is not InventoryDescriptor member)
            return;
        loading = true;
        var flags = profile.GetFlags(member.OwnerEntityId, member.InventoryIndex);
        manual.IsChecked = (flags & InventoryManagementFlags.ManualBlock) != 0;
        reserved.IsChecked = (flags & InventoryManagementFlags.ReservedInventory) != 0;
        noDestination.IsChecked = (flags & InventoryManagementFlags.NoUnifiedCargoDestination) != 0;
        loading = false;
    }

    private void Apply()
    {
        if (loading || table.SelectedRow?.UserData is not InventoryDescriptor member)
            return;
        var selectedFlags = InventoryManagementFlags.None;
        if (reserved.IsChecked)
            selectedFlags |= InventoryManagementFlags.ReservedInventory;
        if (noDestination.Enabled && noDestination.IsChecked)
            selectedFlags |= InventoryManagementFlags.NoUnifiedCargoDestination;
        var existing = profile.GetFlags(member.OwnerEntityId, member.InventoryIndex);
        selectedFlags |= existing & InventoryManagementFlags.ManualBlock;
        profile.SetFlags(member.OwnerEntityId, member.InventoryIndex, selectedFlags);

        foreach (var descriptor in session.Scope.Inventories.Where(candidate =>
                     candidate.OwnerEntityId == member.OwnerEntityId))
        {
            var flags = profile.GetFlags(descriptor.OwnerEntityId, descriptor.InventoryIndex);
            flags = manual.IsChecked
                ? flags | InventoryManagementFlags.ManualBlock
                : flags & ~InventoryManagementFlags.ManualBlock;
            profile.SetFlags(descriptor.OwnerEntityId, descriptor.InventoryIndex, flags);
        }
        Plugin.Instance.Profiles.Save();
        RecreateControls(false);
    }
}

internal sealed class ComponentTargetsScreen : UnifiedStorageScreen
{
    private readonly MechanicalInventorySession session;
    private readonly ScopeProfile profile;
    private readonly Func<InventoryDescriptor, InventoryManagementFlags> getFlags;
    private MyGuiControlTable table;
    private MyGuiControlTextbox target;
    private MyGuiControlCombobox blueprint;
    private MyGuiControlCheckbox maintain;
    private MyGuiControlTextbox threshold;
    private IReadOnlyList<ComponentTargetStatus> statuses;
    private string searchText = string.Empty;

    public ComponentTargetsScreen(
        MechanicalInventorySession session,
        ScopeProfile profile,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags)
        : base("Component targets")
    {
        this.session = session;
        this.profile = profile;
        this.getFlags = getFlags;
    }

    protected override void CreateControls()
    {
        statuses = ComponentTargetEngine.Evaluate(session.Scope, profile, getFlags);
        var search = new MyGuiControlSearchBox(
            new Vector2(-0.4f, -0.33f),
            new Vector2(0.8f, 0.04f),
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER);
        search.SearchText = searchText;
        search.OnTextChanged += value =>
        {
            searchText = value ?? string.Empty;
            PopulateTable();
        };
        Controls.Add(search);
        table = new MyGuiControlTable
        {
            Position = new Vector2(-0.4f, -0.28f),
            Size = new Vector2(0.8f, 0.4f),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            ColumnsCount = 5
        };
        table.SetCustomColumnWidths(new[] { 0.3f, 0.16f, 0.16f, 0.16f, 0.22f });
        foreach (var (index, name) in new[] { "Component", "Stock", "Queued", "Target", "Status" }.Select((name, index) => (index, name)))
            table.SetColumnName(index, new StringBuilder(name));
        table.ItemSelected += (_, _) => LoadSelected();
        Controls.Add(table);
        PopulateTable();

        Controls.Add(Label("Target", new Vector2(-0.38f, 0.16f)));
        target = new MyGuiControlTextbox(new Vector2(-0.2f, 0.16f), "0", 18, type: MyGuiControlTextboxType.DigitsOnly)
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER,
            Size = new Vector2(0.15f, 0.04f)
        };
        Controls.Add(target);
        Controls.Add(Label("Blueprint", new Vector2(-0.02f, 0.16f)));
        blueprint = new MyGuiControlCombobox(
            new Vector2(0.12f, 0.16f), new Vector2(0.28f, 0.04f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER);
        Controls.Add(blueprint);
        Controls.Add(Button("Save target", new Vector2(0.32f, 0.23f), SaveSelected));

        maintain = new MyGuiControlCheckbox(new Vector2(-0.38f, 0.25f))
        {
            IsChecked = profile.MaintainComponentTargets,
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER
        };
        Controls.Add(maintain);
        Controls.Add(Label("Maintain targets (local automation)", new Vector2(-0.345f, 0.25f)));
        Controls.Add(Label("Start threshold", new Vector2(-0.02f, 0.25f)));
        threshold = new MyGuiControlTextbox(new Vector2(0.17f, 0.25f),
            profile.ComponentStartThreshold.ToString("0.##", CultureInfo.InvariantCulture),
            8, type: MyGuiControlTextboxType.Normal)
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER,
            Size = new Vector2(0.1f, 0.04f)
        };
        Controls.Add(threshold);
        Controls.Add(Button("Craft deficits", new Vector2(0.16f, 0.32f), Craft));
        Controls.Add(Button("Close", new Vector2(0.34f, 0.32f), () => CloseScreen()));
        if (table.RowsCount > 0)
            table.SetSelectedRow(0);
    }

    private void LoadSelected()
    {
        if (table.SelectedRow?.UserData is not ComponentTargetStatus status)
            return;
        target.Text = ((decimal)status.Target).ToString("0", CultureInfo.InvariantCulture);
        blueprint.ClearItems();
        for (var index = 0; index < status.BlueprintChoices.Count; index++)
            blueprint.AddItem(index, status.BlueprintChoices[index].DisplayNameText ??
                status.BlueprintChoices[index].Id.SubtypeName);
        if (status.Blueprint != null)
        {
            var selected = -1;
            for (var index = 0; index < status.BlueprintChoices.Count; index++)
                if (ReferenceEquals(status.BlueprintChoices[index], status.Blueprint))
                {
                    selected = index;
                    break;
                }
            if (selected >= 0)
                blueprint.SelectItemByKey(selected, sendEvent: false);
        }
    }

    private void PopulateTable()
    {
        if (table == null)
            return;
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
            row.AddCell(new MyGuiControlTable.Cell($"{status.Deficit} · {status.Status}"));
            table.Add(row);
        }
        if (table.RowsCount > 0)
            table.SetSelectedRow(0);
    }

    private void SaveSelected()
    {
        if (table.SelectedRow?.UserData is not ComponentTargetStatus status ||
            !decimal.TryParse(target.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            return;
        var record = profile.ComponentTargets.FirstOrDefault(candidate =>
            string.Equals(candidate.DefinitionId, status.ComponentId.ToString(), StringComparison.Ordinal));
        if (record == null)
        {
            record = new ComponentTargetRecord { DefinitionId = status.ComponentId.ToString() };
            profile.ComponentTargets.Add(record);
        }
        record.Amount = Math.Max(0, decimal.Truncate(amount));
        var blueprintIndex = blueprint.GetSelectedKey();
        record.BlueprintDefinitionId = blueprintIndex >= 0 && blueprintIndex < status.BlueprintChoices.Count
            ? status.BlueprintChoices[(int)blueprintIndex].Id.ToString()
            : null;
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

    private static string DisplayName(MyDefinitionId id) =>
        MyDefinitionManager.Static.GetPhysicalItemDefinition(id)?.DisplayNameText ?? id.SubtypeName;

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
    private MyGuiControlTable rules;
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
        rules = new MyGuiControlTable
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
        Controls.Add(Label("Targets, supply and excess returns use configurable inventory groups.", new Vector2(-0.36f, 0.16f)));
        Controls.Add(Label("Overlapping target rules are paused. Missing groups never broaden scope.", new Vector2(-0.36f, 0.20f)));
        Controls.Add(Button("New rule", new Vector2(-0.24f, 0.27f), () => Edit(null)));
        Controls.Add(Button("Edit selected", new Vector2(0, 0.27f), () => { if (Selected != null) Edit(Selected); }));
        Controls.Add(Button("Delete selected", new Vector2(0.24f, 0.27f), () =>
        {
            if (Selected == null) return;
            profile.Loadouts.Remove(Selected); Save();
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

    private LoadoutRecord Selected => rules.SelectedRow?.UserData as LoadoutRecord;
    private void Edit(LoadoutRecord record) => MyGuiSandbox.AddScreen(new LoadoutRuleEditor(session, profile, record, groupId, value =>
    {
        var index = record == null ? -1 : profile.Loadouts.IndexOf(record);
        if (index < 0) profile.Loadouts.Add(value); else profile.Loadouts[index] = value;
        Save();
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
        var members = InventoryGroups.Resolve(session.Refresh().Scope, group, out _);
        if (chooseRole)
        {
            var roles = members.SelectMany(m => m.Roles).Select(r => r.Kind)
                .Where(r => group == null || group.AllRoles || group.Role == r).Distinct().ToArray();
            if (roles.Length > 0) role.SelectItemByKey((long)roles[0], sendEvent: false);
        }
        var roleKind = (InventoryRoleKind)role.GetSelectedKey();
        var restoringSavedItem = items == null && original != null;
        var previous = items != null && item.GetSelectedKey() >= 0 ? items[(int)item.GetSelectedKey()].ToString() : original?.ItemDefinitionId;
        items = MyDefinitionManager.Static.GetPhysicalItemDefinitions().Select(d => d.Id)
            .Where(id => InventoryGroups.Accepts(group, id) && members.Any(m => m.Roles.Any(r => r.Kind == roleKind && r.Accepts(id))))
            .OrderBy(Display, StringComparer.CurrentCultureIgnoreCase).ToList();
        // Retain an unavailable saved item for repair, not an incompatible choice from another target/role.
        if (restoringSavedItem && MyDefinitionId.TryParse(previous, out var saved) && !items.Contains(saved)) items.Add(saved);
        item.ClearItems();
        for (var i = 0; i < items.Count; i++) item.AddItem(i, Display(items[i]));
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
    private static string Display(MyDefinitionId id) => MyDefinitionManager.Static.GetPhysicalItemDefinition(id)?.DisplayNameText ?? id.SubtypeName;
}

internal sealed class RefineryPriorityScreen : UnifiedStorageScreen
{
    private readonly MechanicalInventorySession session;
    private readonly ScopeProfile profile;
    private readonly Func<InventoryDescriptor, InventoryManagementFlags> getFlags;
    private readonly Action sortNow;
    private MyGuiControlTable table;
    private MyGuiControlCheckbox automatic;
    private MyGuiControlCheckbox autoSort;
    private RefineryPriorityModel model;

    public RefineryPriorityScreen(
        MechanicalInventorySession session,
        ScopeProfile profile,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags,
        Action sortNow)
        : base("Refinery ore priority", new Vector2(0.7f, 0.78f))
    {
        this.session = session;
        this.profile = profile;
        this.getFlags = getFlags;
        this.sortNow = sortNow;
    }

    protected override void CreateControls()
    {
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
        table = new MyGuiControlTable
        {
            Position = new Vector2(-0.31f, -0.28f),
            Size = new Vector2(0.62f, 0.42f),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            ColumnsCount = 3
        };
        table.SetCustomColumnWidths(new[] { 0.15f, 0.6f, 0.25f });
        table.SetColumnName(0, new StringBuilder("#"));
        table.SetColumnName(1, new StringBuilder("Input"));
        table.SetColumnName(2, new StringBuilder("Accepting"));
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
            row.AddCell(new MyGuiControlTable.Cell(model.AcceptingRefineryCounts[id].ToString(CultureInfo.InvariantCulture)));
            table.Add(row);
        }
        Controls.Add(table);
        automatic = Checkbox("Automatic priority", new Vector2(-0.29f, 0.19f), profile.RefineryPriority.Automatic,
            value => profile.RefineryPriority.Automatic = value);
        autoSort = Checkbox("Auto-sort inputs", new Vector2(0.02f, 0.19f), profile.RefineryPriority.AutoSortInputs,
            value => profile.RefineryPriority.AutoSortInputs = value);
        Controls.Add(Button("Pin / unpin", new Vector2(-0.21f, 0.28f), TogglePin));
        Controls.Add(Button("Move up", new Vector2(-0.06f, 0.28f), () => Move(-1)));
        Controls.Add(Button("Move down", new Vector2(0.09f, 0.28f), () => Move(1)));
        Controls.Add(Button("Sort now", new Vector2(0.24f, 0.28f), sortNow));
    }

    private MyGuiControlCheckbox Checkbox(string label, Vector2 position, bool value, Action<bool> changed)
    {
        var result = new MyGuiControlCheckbox(position)
        {
            IsChecked = value,
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER
        };
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
        if (table.SelectedRow?.UserData is not MyDefinitionId id)
            return;
        var value = id.ToString();
        var list = profile.RefineryPriority.PinnedDefinitionIds;
        if (!list.Remove(value))
            list.Add(value);
        Plugin.Instance.Profiles.Save();
        RecreateControls(false);
    }

    private void Move(int direction)
    {
        if (table.SelectedRow?.UserData is not MyDefinitionId id)
            return;
        var list = profile.RefineryPriority.Automatic
            ? profile.RefineryPriority.PinnedDefinitionIds
            : profile.RefineryPriority.ManualDefinitionIds;
        if (!list.Contains(id.ToString()))
            list.Add(id.ToString());
        var index = list.IndexOf(id.ToString());
        var target = Math.Max(0, Math.Min(list.Count - 1, index + direction));
        if (target == index)
            return;
        (list[index], list[target]) = (list[target], list[index]);
        Plugin.Instance.Profiles.Save();
        RecreateControls(false);
    }
}
