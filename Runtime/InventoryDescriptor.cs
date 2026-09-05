using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using VRage.Game;

namespace ClientPlugin.Inventory;

public enum InventoryDiscoverySource
{
    CargoContainer,
    WeaponDefinition,
    ReactorDefinition,
    ProductionDefinition,
    GasGeneratorDefinition,
    ShipToolDefinition,
    ParachuteDefinition,
    ConstraintFallback,
    Connector
}

public readonly struct InventorySectionKey : IEquatable<InventorySectionKey>
{
    private InventorySectionKey(
        InventorySectionKind kind,
        MyDefinitionId blockDefinitionId,
        int inventoryIndex,
        string constraintSignature,
        string groupId = null)
    {
        Kind = kind;
        BlockDefinitionId = blockDefinitionId;
        InventoryIndex = inventoryIndex;
        ConstraintSignature = constraintSignature;
        GroupId = groupId;
    }

    public InventorySectionKind Kind { get; }
    public MyDefinitionId BlockDefinitionId { get; }
    public int InventoryIndex { get; }
    public string ConstraintSignature { get; }
    public string GroupId { get; }

    public InventorySectionKey InGroup(string groupId) =>
        new(Kind, BlockDefinitionId, InventoryIndex, ConstraintSignature, groupId);

    public static InventorySectionKey UnifiedCargo =>
        new(InventorySectionKind.UnifiedCargo, default, -1, string.Empty);

    public static InventorySectionKey Semantic(InventorySectionKind kind) =>
        new(kind, default, -1, string.Empty);

    public static InventorySectionKey DefinitionFallback(
        MyDefinitionId blockDefinitionId,
        int inventoryIndex,
        string constraintSignature) =>
        new(InventorySectionKind.DefinitionFallback, blockDefinitionId, inventoryIndex, constraintSignature);

    public bool Equals(InventorySectionKey other) =>
        string.Equals(GroupId, other.GroupId, StringComparison.Ordinal) && Kind == other.Kind &&
        BlockDefinitionId.Equals(other.BlockDefinitionId) &&
        InventoryIndex == other.InventoryIndex &&
        string.Equals(ConstraintSignature, other.ConstraintSignature, StringComparison.Ordinal);

    public override bool Equals(object obj) => obj is InventorySectionKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = (int)Kind;
            hash = (hash * 397) ^ BlockDefinitionId.GetHashCode();
            hash = (hash * 397) ^ InventoryIndex;
            hash = (hash * 397) ^ (ConstraintSignature?.GetHashCode() ?? 0);
            hash = (hash * 397) ^ (GroupId?.GetHashCode() ?? 0);
            return hash;
        }
    }
}

public sealed class InventoryRoleDescriptor
{
    private readonly Func<MyDefinitionId, bool> acceptsItem;

    public InventoryRoleDescriptor(InventoryRoleKind kind, Func<MyDefinitionId, bool> acceptsItem)
    {
        Kind = kind;
        this.acceptsItem = acceptsItem ?? throw new ArgumentNullException(nameof(acceptsItem));
    }

    public InventoryRoleKind Kind { get; }

    public bool Accepts(MyDefinitionId itemId) => acceptsItem(itemId);
}

public sealed class InventoryDescriptor
{
    public InventoryDescriptor(
        MyCubeBlock owner,
        int inventoryIndex,
        MyInventory inventory,
        InventorySectionKey section,
        IReadOnlyList<InventoryRoleDescriptor> roles,
        string constraintSignature,
        InventoryDiscoverySource discoverySource)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        Roles = roles ?? throw new ArgumentNullException(nameof(roles));
        InventoryIndex = inventoryIndex;
        Section = section;
        ConstraintSignature = constraintSignature;
        DiscoverySource = discoverySource;
    }

    public MyCubeBlock Owner { get; }
    public long OwnerEntityId => Owner.EntityId;
    public MyDefinitionId BlockDefinitionId => Owner.BlockDefinition.Id;
    public int InventoryIndex { get; }
    public MyInventory Inventory { get; }
    public MyInventoryFlags Flags => Inventory.GetFlags();
    public InventorySectionKey Section { get; }
    public IReadOnlyList<InventoryRoleDescriptor> Roles { get; }
    public string ConstraintSignature { get; }
    public InventoryDiscoverySource DiscoverySource { get; }
}

internal static class InventoryDescriptorFactory
{
    public static InventoryDescriptor Create(MyCubeBlock owner, int inventoryIndex, MyInventory inventory)
    {
        var constraintSignature = GetConstraintSignature(inventory.Constraint);
        var flags = inventory.GetFlags();
        var canSendAndReceive = (flags & (MyInventoryFlags.CanSend | MyInventoryFlags.CanReceive)) ==
                                (MyInventoryFlags.CanSend | MyInventoryFlags.CanReceive);
        var isUnifiedCargo = owner is MyCargoContainer && inventory.Constraint == null && canSendAndReceive;

        if (isUnifiedCargo)
            return CreateSemantic(
                owner,
                inventoryIndex,
                inventory,
                InventorySectionKey.UnifiedCargo,
                InventoryDiscoverySource.CargoContainer,
                new InventoryRoleDescriptor(InventoryRoleKind.GeneralCargo, itemId => AcceptsLive(inventory, itemId)),
                constraintSignature);

        if (owner is MyShipConnector)
            return CreateSemantic(
                owner,
                inventoryIndex,
                inventory,
                InventorySectionKey.Semantic(InventorySectionKind.Connectors),
                InventoryDiscoverySource.Connector,
                new InventoryRoleDescriptor(InventoryRoleKind.GeneralCargo, itemId => AcceptsLive(inventory, itemId)),
                constraintSignature);

        if (owner.BlockDefinition is MyWeaponBlockDefinition weaponBlock &&
            MyDefinitionManager.Static.TryGetWeaponDefinition(weaponBlock.WeaponDefinitionId, out var weapon))
        {
            var ammunition = new HashSet<MyDefinitionId>(weapon.AmmoMagazinesId ?? Array.Empty<MyDefinitionId>());
            return CreateSemantic(
                owner,
                inventoryIndex,
                inventory,
                InventorySectionKey.Semantic(InventorySectionKind.Weapons),
                InventoryDiscoverySource.WeaponDefinition,
                new InventoryRoleDescriptor(
                    InventoryRoleKind.Ammunition,
                    itemId => ammunition.Contains(itemId) && AcceptsLive(inventory, itemId)),
                constraintSignature);
        }

        if (owner.BlockDefinition is MyReactorDefinition reactor)
        {
            var fuels = new HashSet<MyDefinitionId>((reactor.FuelInfos ?? Array.Empty<MyReactorDefinition.FuelInfo>())
                .Select(info => info.FuelId));
            return CreateSemantic(
                owner,
                inventoryIndex,
                inventory,
                InventorySectionKey.Semantic(InventorySectionKind.PowerProducers),
                InventoryDiscoverySource.ReactorDefinition,
                new InventoryRoleDescriptor(
                    InventoryRoleKind.Fuel,
                    itemId => fuels.Contains(itemId) && AcceptsLive(inventory, itemId)),
                constraintSignature);
        }

        if (owner is MyProductionBlock production && owner.BlockDefinition is MyProductionBlockDefinition productionDefinition)
        {
            var isInput = ReferenceEquals(production.InputInventory, inventory);
            var isOutput = ReferenceEquals(production.OutputInventory, inventory);
            if (isInput || isOutput)
            {
                var kind = owner is MyRefinery
                    ? InventorySectionKind.Refineries
                    : owner is MyAssembler
                        ? InventorySectionKind.Assemblers
                        : InventorySectionKind.DefinitionFallback;
                var constraint = isInput
                    ? productionDefinition.InputInventoryConstraint
                    : productionDefinition.OutputInventoryConstraint;
                return CreateSemantic(
                    owner,
                    inventoryIndex,
                    inventory,
                    kind == InventorySectionKind.DefinitionFallback
                        ? InventorySectionKey.DefinitionFallback(owner.BlockDefinition.Id, inventoryIndex, constraintSignature)
                        : InventorySectionKey.Semantic(kind),
                    InventoryDiscoverySource.ProductionDefinition,
                    new InventoryRoleDescriptor(
                        isInput ? InventoryRoleKind.ProductionInput : InventoryRoleKind.ProductionOutput,
                        itemId => (constraint?.Check(itemId) ?? AcceptsLive(inventory, itemId)) &&
                                  AcceptsLive(inventory, itemId)),
                    constraintSignature);
            }
        }

        if (owner is MyGasGenerator && owner.BlockDefinition is MyOxygenGeneratorDefinition)
        {
            return new InventoryDescriptor(
                owner,
                inventoryIndex,
                inventory,
                InventorySectionKey.Semantic(InventorySectionKind.GasSystems),
                new[]
                {
                    new InventoryRoleDescriptor(
                        InventoryRoleKind.GasGeneratorFuel,
                        itemId => !IsBottle(itemId) && AcceptsLive(inventory, itemId)),
                    new InventoryRoleDescriptor(
                        InventoryRoleKind.Bottles,
                        itemId => IsBottle(itemId) && AcceptsLive(inventory, itemId))
                },
                constraintSignature,
                InventoryDiscoverySource.GasGeneratorDefinition);
        }

        if (owner is MyGasTank)
            return CreateSemantic(
                owner,
                inventoryIndex,
                inventory,
                InventorySectionKey.Semantic(InventorySectionKind.GasSystems),
                InventoryDiscoverySource.GasGeneratorDefinition,
                new InventoryRoleDescriptor(
                    InventoryRoleKind.Bottles,
                    itemId => IsBottle(itemId) && AcceptsLive(inventory, itemId)),
                constraintSignature);

        if (owner.BlockDefinition is MyShipToolDefinition)
            return CreateSemantic(
                owner,
                inventoryIndex,
                inventory,
                InventorySectionKey.Semantic(InventorySectionKind.ShipTools),
                InventoryDiscoverySource.ShipToolDefinition,
                new InventoryRoleDescriptor(InventoryRoleKind.ToolInventory, itemId => AcceptsLive(inventory, itemId)),
                constraintSignature);

        if (owner.BlockDefinition is MyParachuteDefinition parachute)
            return CreateSemantic(
                owner,
                inventoryIndex,
                inventory,
                InventorySectionKey.Semantic(InventorySectionKind.SafetySystems),
                InventoryDiscoverySource.ParachuteDefinition,
                new InventoryRoleDescriptor(
                    InventoryRoleKind.ParachuteMaterial,
                    itemId => itemId == parachute.MaterialDefinitionId && AcceptsLive(inventory, itemId)),
                constraintSignature);

        return CreateSemantic(
            owner,
            inventoryIndex,
            inventory,
            InventorySectionKey.DefinitionFallback(owner.BlockDefinition.Id, inventoryIndex, constraintSignature),
            InventoryDiscoverySource.ConstraintFallback,
            new InventoryRoleDescriptor(InventoryRoleKind.Unknown, itemId => AcceptsLive(inventory, itemId)),
            constraintSignature);
    }

    private static InventoryDescriptor CreateSemantic(
        MyCubeBlock owner,
        int inventoryIndex,
        MyInventory inventory,
        InventorySectionKey section,
        InventoryDiscoverySource source,
        InventoryRoleDescriptor role,
        string constraintSignature) =>
        new(owner, inventoryIndex, inventory, section, new[] { role }, constraintSignature, source);

    private static bool AcceptsLive(MyInventory inventory, MyDefinitionId itemId) =>
        inventory.Constraint?.Check(itemId) ?? true;

    private static bool IsBottle(MyDefinitionId itemId)
    {
        var type = (Type)itemId.TypeId;
        return type != null &&
               (typeof(MyObjectBuilder_GasContainerObject).IsAssignableFrom(type) ||
                typeof(MyObjectBuilder_OxygenContainerObject).IsAssignableFrom(type));
    }

    internal static string GetConstraintSignature(MyInventoryConstraint constraint)
    {
        if (constraint == null)
            return "None";

        var result = new StringBuilder(constraint.IsWhitelist ? "Whitelist" : "Blacklist");
        foreach (var id in constraint.ConstrainedIds.Select(id => id.ToString()).OrderBy(id => id, StringComparer.Ordinal))
            result.Append("|Id:").Append(id);
        foreach (var type in constraint.ConstrainedTypes.Select(type => type.ToString()).OrderBy(type => type, StringComparer.Ordinal))
            result.Append("|Type:").Append(type);
        return result.ToString();
    }

#if DEBUG
    internal static void RunSelfTest()
    {
        var firstId = new MyDefinitionId(typeof(MyObjectBuilder_Component), "Construction");
        var secondId = new MyDefinitionId(typeof(MyObjectBuilder_Component), "SteelPlate");
        var first = new MyInventoryConstraint("test").Add(firstId).Add(secondId);
        var second = new MyInventoryConstraint("test").Add(secondId).Add(firstId);

        if (GetConstraintSignature(first) != GetConstraintSignature(second))
            throw new InvalidOperationException("Constraint signatures depend on insertion order.");

        second.IsWhitelist = false;
        if (GetConstraintSignature(first) == GetConstraintSignature(second))
            throw new InvalidOperationException("Constraint signatures do not distinguish whitelist mode.");
    }
#endif
}
