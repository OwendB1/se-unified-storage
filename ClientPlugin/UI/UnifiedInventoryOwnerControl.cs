using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClientPlugin.Inventory;
using ClientPlugin.Profiles;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Graphics.GUI;
using VRage;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin.UI;

internal sealed class ProjectedGridContext
{
    public ProjectedGridContext(UnifiedInventoryOwnerControl owner, InventoryRoleProjection role, MyGuiControlGrid grid)
    {
        Owner = owner;
        Role = role;
        Grid = grid;
    }

    public UnifiedInventoryOwnerControl Owner { get; }
    public InventoryRoleProjection Role { get; internal set; }
    public MyGuiControlGrid Grid { get; }
}

internal sealed class UnifiedInventoryOwnerControl : MyGuiControlBase
{
    private const float Padding = 0.008f;
    private const float OwnerHeaderHeight = 0.045f;
    private const float SectionHeaderHeight = 0.031f;
    private const float ExpandedSectionHeaderHeight = 0.058f;
    private static readonly float SectionGap = 12f / MyGuiConstants.GUI_OPTIMAL_SIZE.Y;
    private const float RoleHeaderHeight = 0.025f;
    private const float FooterHeight = 0.033f;
    private readonly List<MyGuiControlGrid> grids = new();
    private readonly List<InventoryRoleProjection[]> sectionBindings = new();
    private readonly List<MyGuiControlButton> rebalanceButtons = new();
    private VisibleRole[] visibleRoles;
    private string memberLayout;
    private readonly MyGuiControlLabel totals;

    public UnifiedInventoryOwnerControl(
        MechanicalInventorySession session,
        string viewId,
        InventoryProjection projection,
        DistributionPolicy policy,
        string search,
        Func<InventoryRoleProjection, bool> roleFilter,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags,
        Action<UnifiedInventoryOwnerControl, DistributionPolicy> policyChanged,
        Action<ProjectedGridContext, MyGuiControlGrid.EventArgs> itemDragged,
        Action<ProjectedGridContext, MyGuiControlGrid.EventArgs> itemDoubleClicked,
        Action<IReadOnlyList<InventoryRoleProjection>> rebalance,
        Action<IReadOnlyList<InventoryRoleProjection>> manage,
        Action<InventorySectionKey> configure,
        Action<InventorySectionKey> utility,
        Action groups,
        Action loadouts)
        : base(
            size: new Vector2(0.392f, 0.1f),
            isActiveControl: false,
            canHaveFocus: true)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        ViewId = viewId ?? throw new ArgumentNullException(nameof(viewId));
        Projection = projection ?? throw new ArgumentNullException(nameof(projection));
        OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP;
        CanFocusChildren = true;
        CanPlaySoundOnMouseOver = false;

        visibleRoles = GetVisibleRoles(projection, search, roleFilter);
        memberLayout = MemberLayout(visibleRoles, getFlags);
        var sections = visibleRoles.GroupBy(entry => entry.Role.Section).ToArray();
        var height = Padding * 2 + OwnerHeaderHeight + FooterHeight +
                     sections.Sum(section => GetSectionHeaderHeight(section.Key) + Padding + SectionGap) +
                     visibleRoles.Sum(entry => RoleHeaderHeight + GridHeight(entry.Stacks.Count) + Padding);
        Size = new Vector2(0.392f, Math.Max(0.12f, height));
        var topLeft = Size * -0.5f + new Vector2(Padding, Padding);

        var policyCombo = new MyGuiControlCombobox(
            topLeft,
            new Vector2(0.17f, 0.03f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            openAreaItemsCount: 3,
            toolTip: "Save this scope's placement policy immediately. Used for deposits and rebalance; choosing a policy alone does not move items. Loadout rules use their own policies.")
        {
            Name = "UnifiedPolicy",
            CanPlaySoundOnMouseOver = false
        };
        foreach (DistributionPolicy value in Enum.GetValues(typeof(DistributionPolicy)))
            policyCombo.AddItem((long)value, SplitWords(value.ToString()), toolTip: value switch
            {
                DistributionPolicy.ExistingStackFirst => "Prefer inventories already holding this item, then use other eligible space.",
                DistributionPolicy.FillFirst => "Fill eligible inventories in priority order before using the next container.",
                _ => "Spread each item across eligible inventories, redistributing around capacity and block constraints."
            });
        policyCombo.SelectItemByKey((long)policy, sendEvent: false);
        policyCombo.ItemSelected += () => policyChanged?.Invoke(this,
            (DistributionPolicy)policyCombo.GetSelectedKey());
        Elements.Add(policyCombo);
        Elements.Add(MakeButton("Groups", topLeft.X + 0.19f, topLeft.Y, 0.08f,
            _ => groups(), "Create, edit and order live inventory views. Groups alone do not move items; saved terminal-group names also match future members."));
        Elements.Add(MakeButton("Loadouts", topLeft.X + 0.28f, topLeft.Y, 0.094f,
            _ => loadouts(), "Set item targets, supply and excess-return groups. Rule edits save settings; Apply loadouts starts transfers."));

        var y = topLeft.Y + OwnerHeaderHeight;
        foreach (var section in sections)
        {
            // Keep backgrounds passive and behind the existing controls so the
            // native-style split does not change focus or drag/drop ownership.
            Elements.Add(new MyGuiControlPanel(
                position: new Vector2(-Size.X * 0.5f, y),
                size: new Vector2(Size.X, Padding + GetSectionHeaderHeight(section.Key) +
                    section.Sum(entry => RoleHeaderHeight + GridHeight(entry.Stacks.Count) + Padding)),
                texture: "Textures\\GUI\\Controls\\item_dark.dds",
                originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP)
            {
                Name = "UnifiedSectionBackground",
                IsHitTestVisible = false,
                CanPlaySoundOnMouseOver = false
            });
            y += Padding;
            var sectionRoles = section.Select(entry => entry.Role).ToArray();
            sectionBindings.Add(sectionRoles);
            var members = sectionRoles.SelectMany(role => role.Members).Select(member => member.OwnerEntityId).Distinct().Count();
            var reserved = sectionRoles.SelectMany(role => role.Members)
                .GroupBy(member => (member.OwnerEntityId, member.InventoryIndex))
                .Count(group => (getFlags?.Invoke(group.First()) & InventoryManagementFlags.ReservedInventory) != 0);
            Elements.Add(new MyGuiControlLabel(
                new Vector2(topLeft.X + 0.004f, y),
                text: $"{GetSectionName(section.First().Role)} × {members}" +
                      (reserved > 0 ? $"  Reserved: {reserved}" : string.Empty),
                textScale: 0.64f,
                originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
                isAutoEllipsisEnabled: true,
                maxWidth: 0.14f)
            {
                Size = new Vector2(0.14f, 0.025f)
            });
            Elements.Add(MakeButton("Manage", topLeft.X + 0.148f, y, 0.064f,
                _ => manage?.Invoke(sectionRoles), "Edit Unmanaged, Reserved and Source Only settings for these members. Edits are buffered across rows until Apply saves them all."));
            var rebalanceButton = MakeButton("Rebalance", topLeft.X + 0.216f, y, 0.086f,
                _ => rebalance?.Invoke(sectionRoles), "Immediately redistribute items among eligible members of this section using the selected policy. Respects exclusions, capacity and conveyor access. Disabled while local transfers are pending or no item has multiple eligible members.");
            rebalanceButton.Enabled = Plugin.Instance.Transfers.PendingCount == 0 && sectionRoles.Any(role =>
                role.Stacks.Any(stack => role.Members.Count(member => role.Accepts(member, stack.DefinitionId)) >= 2));
            Elements.Add(rebalanceButton);
            rebalanceButtons.Add(rebalanceButton);

            var feature = FeatureName(section.Key);
            if (feature != null)
                Elements.Add(MakeButton(feature, topLeft.X + 0.306f, y, 0.068f,
                    _ => configure?.Invoke(section.Key), FeatureTooltip(section.Key)));
            var utilityName = UtilityName(section.Key);
            if (utilityName != null)
                Elements.Add(MakeButton(utilityName, topLeft.X + 0.306f,
                    y + (feature == null ? 0f : 0.027f), 0.068f,
                    _ => utility?.Invoke(section.Key), UtilityTooltip(section.Key)));
            y += GetSectionHeaderHeight(section.Key);

            foreach (var entry in section)
            {
                Elements.Add(new MyGuiControlLabel(
                    new Vector2(topLeft.X + 0.004f, y),
                    text: GetRoleName(entry.Role.Role),
                    textScale: 0.58f,
                    originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
                    isAutoEllipsisEnabled: true,
                    maxWidth: 0.37f));
                y += RoleHeaderHeight;
                var grid = CreateGrid(topLeft.X, y, entry, getFlags, itemDragged, itemDoubleClicked);
                grids.Add(grid);
                Elements.Add(grid);
                y += GridHeight(entry.Stacks.Count) + Padding;
            }
            y += SectionGap;
        }

        totals = new MyGuiControlLabel(
            new Vector2(topLeft.X + 0.004f, Size.Y * 0.5f - FooterHeight),
            textScale: 0.58f,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Elements.Add(totals);
        UpdateTotals();
    }

    private void UpdateTotals()
    {
        var uniqueInventories = Projection.Roles.SelectMany(role => role.Members)
            .Select(member => member.Inventory).Distinct().ToArray();
        var mass = uniqueInventories.Aggregate(MyFixedPoint.Zero,
            (sum, inventory) => sum + inventory.CurrentMass);
        var volume = uniqueInventories.Aggregate(MyFixedPoint.Zero,
            (sum, inventory) => sum + inventory.CurrentVolume);
        totals.Text = $"Mass: {(double)mass:N2} kg    Volume: {(double)MyFixedPoint.MultiplySafe(volume, 1000):N2} L";
    }

    public MechanicalInventorySession Session { get; }
    public string ViewId { get; }
    public InventoryProjection Projection { get; private set; }
    public IReadOnlyList<MyGuiControlGrid> Grids => grids;

    // Quantity-only refreshes keep controls, selection, hover and scrollbar alive.
    // Changed membership, ordering or stack identity takes the full rebuild path.
    public bool TryRefresh(InventoryProjection projection, string search,
        Func<InventoryRoleProjection, bool> roleFilter,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags)
    {
        var next = GetVisibleRoles(projection, search, roleFilter);
        if (next.Length != visibleRoles.Length || MemberLayout(next, getFlags) != memberLayout)
            return false;
        for (var i = 0; i < next.Length; i++)
        {
            var before = visibleRoles[i];
            var after = next[i];
            if (!before.Role.Section.Equals(after.Role.Section) || before.Role.Role != after.Role.Role ||
                before.Stacks.Count != after.Stacks.Count)
                return false;
            for (var j = 0; j < after.Stacks.Count; j++)
                if (before.Stacks[j].DefinitionId != after.Stacks[j].DefinitionId ||
                    !before.Stacks[j].Sources.Select(source => (source.Inventory, source.ItemId))
                        .SequenceEqual(after.Stacks[j].Sources.Select(source => (source.Inventory, source.ItemId))))
                    return false;
        }

        Projection = projection;
        visibleRoles = next;
        for (var i = 0; i < grids.Count; i++)
        {
            ((ProjectedGridContext)grids[i].UserData).Role = next[i].Role;
            for (var j = 0; j < next[i].Stacks.Count; j++)
                grids[i].SetItemAt(j, CreateItem(next[i].Stacks[j], getFlags));
        }
        var roleIndex = 0;
        for (var i = 0; i < sectionBindings.Count; i++)
        {
            var roles = sectionBindings[i];
            for (var j = 0; j < roles.Length; j++)
                roles[j] = next[roleIndex++].Role;
            rebalanceButtons[i].Enabled = Plugin.Instance.Transfers.PendingCount == 0 && roles.Any(role =>
                role.Stacks.Any(stack => role.Members.Count(member => role.Accepts(member, stack.DefinitionId)) >= 2));
        }
        UpdateTotals();
        return true;
    }

    private static VisibleRole[] GetVisibleRoles(InventoryProjection projection, string search,
        Func<InventoryRoleProjection, bool> roleFilter) => projection.Roles
        .Where(role => roleFilter?.Invoke(role) ?? true)
        .Select(role => new VisibleRole(role, role.Stacks.Where(stack => MatchesSearch(stack, search)).ToArray()))
        .Where(entry => entry.Stacks.Count > 0 || string.IsNullOrWhiteSpace(search))
        .GroupBy(entry => entry.Role.Section).SelectMany(section => section).ToArray();

    private static string MemberLayout(IEnumerable<VisibleRole> roles,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags) => string.Join("\n", roles.Select(entry =>
        GetSectionName(entry.Role) + ":" + string.Join(",", entry.Role.Members.Select(member =>
            $"{member.OwnerEntityId}/{member.InventoryIndex}/{getFlags?.Invoke(member)}"))));

    public override MyGuiControlBase HandleInput()
    {
        base.HandleInput();
        return HandleInputElements();
    }

    private MyGuiControlGrid CreateGrid(
        float x,
        float y,
        VisibleRole entry,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags,
        Action<ProjectedGridContext, MyGuiControlGrid.EventArgs> itemDragged,
        Action<ProjectedGridContext, MyGuiControlGrid.EventArgs> itemDoubleClicked)
    {
        var grid = new MyGuiControlGrid
        {
            Name = "UnifiedInventoryGrid",
            VisualStyle = MyGuiControlGridStyleEnum.Inventory,
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(x, y),
            ColumnsCount = 7,
            RowsCount = GridRows(entry.Stacks.Count),
            ShowTooltipWhenDisabled = true
        };
        var context = new ProjectedGridContext(this, entry.Role, grid);
        grid.UserData = context;
        if (entry.Role.Members.FirstOrDefault()?.Inventory.Constraint is { } constraint)
        {
            grid.EmptyItemIcon = constraint.Icon;
            grid.SetEmptyItemToolTip(constraint.Description);
        }
        foreach (var stack in entry.Stacks)
            grid.Add(CreateItem(stack, getFlags));
        grid.ItemDragged += (_, args) => itemDragged?.Invoke(context, args);
        grid.ItemDoubleClicked += (_, args) => itemDoubleClicked?.Invoke(context, args);
        return grid;
    }

    private static MyGuiGridItem CreateItem(ProjectedInventoryStack stack,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags)
    {
        var item = MyGuiControlInventoryOwner.CreateInventoryGridItem(stack.ToDisplayItem());
        var reservedAmount = stack.Sources.Where(source => source.Descriptor != null &&
                (getFlags?.Invoke(source.Descriptor) & InventoryManagementFlags.ReservedInventory) != 0)
            .Aggregate(MyFixedPoint.Zero, (sum, source) => sum + source.SnapshotAmount);
        if (reservedAmount > MyFixedPoint.Zero)
        {
            item.AddText("R", MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_TOP);
            item.ToolTip ??= new MyToolTips();
            item.ToolTip.AddToolTip($"Reserved / not counted: {reservedAmount}", 0.7f, "White");
        }
        item.UserData = stack;
        return item;
    }

    private static MyGuiControlButton MakeButton(
        string text, float x, float y, float width, Action<MyGuiControlButton> click, string tooltip) =>
        new(new Vector2(x, y), MyGuiControlButtonStyleEnum.Rectangular, new Vector2(width, 0.026f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: UnifiedStorageHelp.Wrap(tooltip), text: new StringBuilder(text), textScale: 0.45f,
            // Rebuilt headers can appear under a stationary pointer during bulk moves.
            // Keep deliberate click sounds, but do not replay hover sounds on refresh.
            onButtonClick: click, isAutoscaleEnabled: true)
        { ShowTooltipWhenDisabled = true, CanPlaySoundOnMouseOver = false };

    private static string FeatureName(InventorySectionKey section) => section.Kind switch
    {
        InventorySectionKind.Refineries => "Priority",
        InventorySectionKind.Assemblers => "Targets",
        InventorySectionKind.UnifiedCargo => null,
        InventorySectionKind.DefinitionFallback => "Actions",
        _ => "Loadouts"
    };

    private static string UtilityName(InventorySectionKey section) => section.Kind switch
    {
        InventorySectionKind.Refineries => "Drain",
        InventorySectionKind.Assemblers => "Drain",
        _ => null
    };

    private static string FeatureTooltip(InventorySectionKey section) =>
        section.Kind == InventorySectionKind.Refineries
            ? "Configure ship-wide ore priorities. Priority settings save immediately; Sort now reorders refinery input stacks."
            : section.Kind == InventorySectionKind.Assemblers
                ? "Set ship-wide component goals and supported assembler recipes. Save target saves quantities for selected components; Craft deficits queues missing stock from all saved goals."
                : section.Kind == InventorySectionKind.DefinitionFallback
                    ? "Open the settings available for this block type, including loadouts and supported production controls."
                    : "Configure item targets for this group's inventories, including supply, excess returns and optional maintenance.";

    private static string UtilityTooltip(InventorySectionKey section) =>
        section.Kind == InventorySectionKind.Refineries
            ? "Immediately move ingots from this ship's refinery outputs into general cargo using the selected policy. Input ores stay untouched; refining may continue. Exclusions, access and capacity still apply."
            : "Immediately return inventory from this ship's idle assembly-mode assemblers to general cargo. Queued, producing or disassembling machines are skipped. Exclusions, access and capacity still apply.";

    private static float GetSectionHeaderHeight(InventorySectionKey section) =>
        FeatureName(section) != null && UtilityName(section) != null
            ? ExpandedSectionHeaderHeight
            : SectionHeaderHeight;

    private static int GridRows(int itemCount) =>
        Math.Max(1, (int)Math.Ceiling((itemCount + 1) / 7d));

    private static float GridHeight(int itemCount)
    {
        var style = MyGuiControlGrid.GetVisualStyle(MyGuiControlGridStyleEnum.Inventory);
        return style.ContentPadding.SizeChange.Y + style.ItemMargin.TopLeftOffset.Y +
               (style.ItemTexture.SizeGui.Y + style.ItemMargin.MarginStep.Y) * GridRows(itemCount);
    }

    private static bool MatchesSearch(ProjectedInventoryStack stack, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;
        var definition = Sandbox.Definitions.MyDefinitionManager.Static.GetPhysicalItemDefinition(stack.DefinitionId);
        return (definition?.DisplayNameText?.IndexOf(search, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
               stack.DefinitionId.ToString().IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string GetSectionName(InventoryRoleProjection role) => role.Group != null
        ? role.Group.Name + (role.Section.BlockDefinitionId == default ? string.Empty : " / " + role.Section.BlockDefinitionId.SubtypeName)
        : role.Section.Kind switch
    {
        InventorySectionKind.UnifiedCargo => "Unified Cargo",
        InventorySectionKind.PowerProducers => "Power Producers",
        InventorySectionKind.GasSystems => "Gas Systems",
        InventorySectionKind.ShipTools => "Ship Tools",
        InventorySectionKind.SafetySystems => "Safety Systems",
        InventorySectionKind.DefinitionFallback => role.Section.BlockDefinitionId.SubtypeName,
        _ => SplitWords(role.Section.Kind.ToString())
    };

    private static string GetRoleName(InventoryRoleKind role) => role switch
    {
        InventoryRoleKind.ProductionInput => "Input",
        InventoryRoleKind.ProductionOutput => "Output",
        InventoryRoleKind.GasGeneratorFuel => "Fuel",
        InventoryRoleKind.ToolInventory => "Inventory",
        InventoryRoleKind.ParachuteMaterial => "Canvas",
        _ => SplitWords(role.ToString())
    };

    private static string SplitWords(string value)
    {
        var result = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            if (i > 0 && char.IsUpper(value[i]) && !char.IsUpper(value[i - 1]))
                result.Append(' ');
            result.Append(value[i]);
        }
        return result.ToString();
    }

    private sealed class VisibleRole
    {
        public VisibleRole(InventoryRoleProjection role, IReadOnlyList<ProjectedInventoryStack> stacks)
        {
            Role = role;
            Stacks = stacks;
        }

        public InventoryRoleProjection Role { get; }
        public IReadOnlyList<ProjectedInventoryStack> Stacks { get; }
    }
}
