using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sandbox.Game;
using Sandbox.Game.Entities;
using VRage.Game;

namespace ClientPlugin.Inventory;

public enum InventorySectionKind
{
    UnifiedCargo,
    DefinitionFallback
}

public enum InventoryRoleKind
{
    GeneralCargo,
    Unknown
}

public enum InventoryDiscoverySource
{
    CargoContainer,
    ConstraintFallback
}

public readonly struct InventorySectionKey : IEquatable<InventorySectionKey>
{
    private InventorySectionKey(
        InventorySectionKind kind,
        MyDefinitionId blockDefinitionId,
        int inventoryIndex,
        string constraintSignature)
    {
        Kind = kind;
        BlockDefinitionId = blockDefinitionId;
        InventoryIndex = inventoryIndex;
        ConstraintSignature = constraintSignature;
    }

    public InventorySectionKind Kind { get; }
    public MyDefinitionId BlockDefinitionId { get; }
    public int InventoryIndex { get; }
    public string ConstraintSignature { get; }

    public static InventorySectionKey UnifiedCargo =>
        new(InventorySectionKind.UnifiedCargo, default, -1, string.Empty);

    public static InventorySectionKey DefinitionFallback(
        MyDefinitionId blockDefinitionId,
        int inventoryIndex,
        string constraintSignature) =>
        new(InventorySectionKind.DefinitionFallback, blockDefinitionId, inventoryIndex, constraintSignature);

    public bool Equals(InventorySectionKey other) =>
        Kind == other.Kind &&
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

        var role = new InventoryRoleDescriptor(
            isUnifiedCargo ? InventoryRoleKind.GeneralCargo : InventoryRoleKind.Unknown,
            itemId => inventory.Constraint?.Check(itemId) ?? true);

        return new InventoryDescriptor(
            owner,
            inventoryIndex,
            inventory,
            isUnifiedCargo
                ? InventorySectionKey.UnifiedCargo
                : InventorySectionKey.DefinitionFallback(owner.BlockDefinition.Id, inventoryIndex, constraintSignature),
            new[] { role },
            constraintSignature,
            isUnifiedCargo ? InventoryDiscoverySource.CargoContainer : InventoryDiscoverySource.ConstraintFallback);
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
