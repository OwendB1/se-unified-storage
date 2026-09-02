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
    public InventoryRoleProjection Role { get; }
    public MyGuiControlGrid Grid { get; }
}

internal sealed class UnifiedInventoryOwnerControl : MyGuiControlBase
{
    private const float Padding = 0.008f;
    private const float OwnerHeaderHeight = 0.035f;
    private const float SectionHeaderHeight = 0.058f;
    private const float RoleHeaderHeight = 0.025f;
    private const float FooterHeight = 0.033f;
    private readonly List<MyGuiControlGrid> grids = new();

    public UnifiedInventoryOwnerControl(
        MechanicalInventorySession session,
        string viewId,
        string viewName,
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
        Action<InventorySectionKey> utility)
        : base(
            size: new Vector2(0.392f, 0.1f),
            backgroundTexture: new MyGuiCompositeTexture
            {
                Center = new MyGuiSizedTexture { Texture = "Textures\\GUI\\Controls\\item_dark.dds" }
            },
            isActiveControl: false,
            canHaveFocus: true)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        ViewId = viewId ?? throw new ArgumentNullException(nameof(viewId));
        Projection = projection ?? throw new ArgumentNullException(nameof(projection));
        OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP;
        BorderHighlightEnabled = true;
        BorderColor = MyGuiConstants.HIGHLIGHT_BACKGROUND_COLOR;
        BorderSize = 2;
        CanFocusChildren = true;

        var visibleRoles = projection.Roles
            .Where(role => roleFilter?.Invoke(role) ?? true)
            .Select(role => new VisibleRole(role,
                role.Stacks.Where(stack => MatchesSearch(stack, search)).ToArray()))
            .Where(entry => entry.Stacks.Count > 0 || string.IsNullOrWhiteSpace(search))
            .ToArray();
        var sections = visibleRoles.GroupBy(entry => entry.Role.Section).ToArray();
        var height = Padding * 2 + OwnerHeaderHeight + FooterHeight +
                     sections.Length * SectionHeaderHeight +
                     visibleRoles.Sum(entry => RoleHeaderHeight + GridHeight(entry.Stacks.Count) + Padding);
        Size = new Vector2(0.392f, Math.Max(0.12f, height));
        var topLeft = Size * -0.5f + new Vector2(Padding, Padding);

        Elements.Add(new MyGuiControlLabel(
            topLeft,
            text: string.IsNullOrEmpty(viewName) ? GetScopeName(projection) : viewName,
            textScale: 0.72f,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP)
        {
            Size = new Vector2(0.19f, OwnerHeaderHeight)
        });
        var policyCombo = new MyGuiControlCombobox(
            topLeft + new Vector2(0.202f, 0f),
            new Vector2(0.17f, 0.03f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            openAreaItemsCount: 3,
            toolTip: "Placement policy used by this scope's Rebalance actions")
        {
            Name = "UnifiedPolicy"
        };
        foreach (DistributionPolicy value in Enum.GetValues(typeof(DistributionPolicy)))
            policyCombo.AddItem((long)value, SplitWords(value.ToString()));
        policyCombo.SelectItemByKey((long)policy, sendEvent: false);
        policyCombo.ItemSelected += () => policyChanged?.Invoke(this,
            (DistributionPolicy)policyCombo.GetSelectedKey());
        Elements.Add(policyCombo);

        var y = topLeft.Y + OwnerHeaderHeight;
        foreach (var section in sections)
        {
            var sectionRoles = section.Select(entry => entry.Role).ToArray();
            var members = sectionRoles.SelectMany(role => role.Members).Select(member => member.OwnerEntityId).Distinct().Count();
            var reserved = sectionRoles.SelectMany(role => role.Members)
                .GroupBy(member => (member.OwnerEntityId, member.InventoryIndex))
                .Count(group => (getFlags?.Invoke(group.First()) & InventoryManagementFlags.ReservedInventory) != 0);
            Elements.Add(new MyGuiControlLabel(
                new Vector2(topLeft.X + 0.004f, y),
                text: $"{GetSectionName(section.First().Role)} × {members}" +
                      (reserved > 0 ? $"  Reserved: {reserved}" : string.Empty),
                textScale: 0.64f,
                originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP)
            {
                Size = new Vector2(0.21f, 0.025f)
            });
            Elements.Add(MakeButton("Manage", topLeft.X + 0.22f, y, 0.07f,
                _ => manage?.Invoke(sectionRoles), "Configure member exclusions"));
            var rebalanceButton = MakeButton("Rebalance", topLeft.X + 0.294f, y, 0.08f,
                _ => rebalance?.Invoke(sectionRoles), "Redistribute this type using the selected policy");
            rebalanceButton.Enabled = Plugin.Instance.Transfers.PendingCount == 0 && sectionRoles.Any(role =>
                role.Stacks.Any(stack => role.Members.Count(member =>
                    member.Roles.Any(candidate => candidate.Kind == role.Role &&
                                                   candidate.Accepts(stack.DefinitionId))) >= 2));
            Elements.Add(rebalanceButton);

            var feature = FeatureName(section.Key);
            if (feature != null)
                Elements.Add(MakeButton(feature, topLeft.X + 0.22f, y + 0.027f, 0.075f,
                    _ => configure?.Invoke(section.Key), FeatureTooltip(section.Key)));
            var utilityName = UtilityName(section.Key);
            if (utilityName != null)
                Elements.Add(MakeButton(utilityName, topLeft.X + 0.299f, y + 0.027f, 0.075f,
                    _ => utility?.Invoke(section.Key), UtilityTooltip(section.Key)));
            y += SectionHeaderHeight;

            foreach (var entry in section)
            {
                Elements.Add(new MyGuiControlLabel(
                    new Vector2(topLeft.X + 0.004f, y),
                    text: GetRoleName(entry.Role.Role),
                    textScale: 0.58f,
                    originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP));
                y += RoleHeaderHeight;
                var grid = CreateGrid(topLeft.X, y, entry, getFlags, itemDragged, itemDoubleClicked);
                grids.Add(grid);
                Elements.Add(grid);
                y += GridHeight(entry.Stacks.Count) + Padding;
            }
        }

        var uniqueInventories = projection.Roles.SelectMany(role => role.Members)
            .Select(member => member.Inventory).Distinct().ToArray();
        var mass = uniqueInventories.Aggregate(MyFixedPoint.Zero,
            (sum, inventory) => sum + inventory.CurrentMass);
        var volume = uniqueInventories.Aggregate(MyFixedPoint.Zero,
            (sum, inventory) => sum + inventory.CurrentVolume);
        Elements.Add(new MyGuiControlLabel(
            new Vector2(topLeft.X + 0.004f, Size.Y * 0.5f - FooterHeight),
            text: $"Mass: {(double)mass:N2} kg    Volume: {(double)MyFixedPoint.MultiplySafe(volume, 1000):N2} L",
            textScale: 0.58f,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP));
    }

    public MechanicalInventorySession Session { get; }
    public string ViewId { get; }
    public InventoryProjection Projection { get; }
    public IReadOnlyList<MyGuiControlGrid> Grids => grids;

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
            RowsCount = Math.Max(1, (int)Math.Ceiling((entry.Stacks.Count + 1) / 7d)),
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
            grid.Add(item);
        }
        grid.ItemDragged += (_, args) => itemDragged?.Invoke(context, args);
        grid.ItemDoubleClicked += (_, args) => itemDoubleClicked?.Invoke(context, args);
        return grid;
    }

    private static MyGuiControlButton MakeButton(
        string text, float x, float y, float width, Action<MyGuiControlButton> click, string tooltip) =>
        new(new Vector2(x, y), MyGuiControlButtonStyleEnum.Rectangular, new Vector2(width, 0.026f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: tooltip, text: new StringBuilder(text), textScale: 0.45f,
            onButtonClick: click, isAutoscaleEnabled: true);

    private static string FeatureName(InventorySectionKey section) => section.Kind switch
    {
        InventorySectionKind.Refineries => "Priority",
        InventorySectionKind.Assemblers => "Targets",
        InventorySectionKind.UnifiedCargo => null,
        _ => "Loadouts"
    };

    private static string UtilityName(InventorySectionKey section) => section.Kind switch
    {
        InventorySectionKind.UnifiedCargo => "Refill",
        InventorySectionKind.Assemblers => "Drain idle",
        _ => null
    };

    private static string FeatureTooltip(InventorySectionKey section) =>
        section.Kind == InventorySectionKind.Refineries
            ? "Configure definition-derived ore priority and input sorting"
            : section.Kind == InventorySectionKind.Assemblers
                ? "Configure component production targets"
                : "Configure definition-driven inventory loadouts";

    private static string UtilityTooltip(InventorySectionKey section) =>
        section.Kind == InventorySectionKind.UnifiedCargo
            ? "Run the bounded bottle refill job"
            : "Move inventory from idle assembly-mode assemblers back to Unified Cargo";

    private static float GridHeight(int itemCount) =>
        Math.Max(1, (int)Math.Ceiling((itemCount + 1) / 7d)) * 0.0575f;

    private static bool MatchesSearch(ProjectedInventoryStack stack, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;
        var definition = Sandbox.Definitions.MyDefinitionManager.Static.GetPhysicalItemDefinition(stack.DefinitionId);
        return (definition?.DisplayNameText?.IndexOf(search, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
               stack.DefinitionId.ToString().IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string GetScopeName(InventoryProjection projection) =>
        projection.Scope.AnchorGrid.DisplayName ?? projection.Scope.AnchorGrid.Name ?? "Unified Cargo";

    private static string GetSectionName(InventoryRoleProjection role) => role.Section.Kind switch
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
