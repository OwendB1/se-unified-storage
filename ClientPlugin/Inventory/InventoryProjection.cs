using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game;
using VRage;
using VRage.Game;
using VRage.Game.Entity;

namespace ClientPlugin.Inventory;

public readonly struct InventoryStackReference
{
    public InventoryStackReference(InventoryDescriptor inventory, MyPhysicalInventoryItem item)
    {
        Descriptor = inventory ?? throw new ArgumentNullException(nameof(inventory));
        Inventory = inventory.Inventory;
        ItemId = item.ItemId;
        DefinitionId = item.Content.GetObjectId();
        SnapshotAmount = item.Amount;
    }

    public InventoryStackReference(MyInventory inventory, MyPhysicalInventoryItem item)
    {
        Inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        ItemId = item.ItemId;
        DefinitionId = item.Content.GetObjectId();
        SnapshotAmount = item.Amount;
    }

    public InventoryDescriptor Descriptor { get; }
    public MyInventory Inventory { get; }
    public uint ItemId { get; }
    public MyDefinitionId DefinitionId { get; }
    public MyFixedPoint SnapshotAmount { get; }
}

public sealed class ProjectedInventoryStack
{
    private readonly List<InventoryStackReference> sources = new();

    internal ProjectedInventoryStack(MyPhysicalInventoryItem representative)
    {
        Representative = representative;
    }

    public MyPhysicalInventoryItem Representative { get; private set; }
    public MyDefinitionId DefinitionId => Representative.Content.GetObjectId();
    public MyFixedPoint Amount { get; private set; }
    public IReadOnlyList<InventoryStackReference> Sources => sources;

    internal bool CanStack(MyPhysicalInventoryItem item) =>
        Representative.Content.CanStack(item.Content) && item.Content.CanStack(Representative.Content);

    internal void Add(InventoryDescriptor inventory, MyPhysicalInventoryItem item)
    {
        if (sources.Count == 0)
            Representative = item;
        Amount += item.Amount;
        sources.Add(new InventoryStackReference(inventory, item));
    }

    public MyPhysicalInventoryItem ToDisplayItem()
    {
        var result = Representative;
        result.Amount = Amount;
        return result;
    }
}

public sealed class InventoryRoleProjection
{
    internal InventoryRoleProjection(
        InventorySectionKey section,
        InventoryRoleKind role,
        IReadOnlyList<InventoryDescriptor> members,
        IReadOnlyList<ProjectedInventoryStack> stacks,
        MyFixedPoint currentMass,
        MyFixedPoint currentVolume,
        MyFixedPoint maxVolume)
    {
        Section = section;
        Role = role;
        Members = members;
        Stacks = stacks;
        CurrentMass = currentMass;
        CurrentVolume = currentVolume;
        MaxVolume = maxVolume;
    }

    public InventorySectionKey Section { get; }
    public InventoryRoleKind Role { get; }
    public IReadOnlyList<InventoryDescriptor> Members { get; }
    public IReadOnlyList<ProjectedInventoryStack> Stacks { get; }
    public MyFixedPoint CurrentMass { get; }
    public MyFixedPoint CurrentVolume { get; }
    public MyFixedPoint MaxVolume { get; }
}

public sealed class InventoryProjection
{
    internal InventoryProjection(MechanicalInventoryScope scope, IReadOnlyList<InventoryRoleProjection> roles)
    {
        Scope = scope;
        Roles = roles;
    }

    public MechanicalInventoryScope Scope { get; }
    public IReadOnlyList<InventoryRoleProjection> Roles { get; }
}

public sealed class InventoryProjectionBuilder
{
    public InventoryProjection Build(MechanicalInventoryScope scope, Func<InventoryDescriptor, bool> includeInventory = null)
    {
        if (scope == null)
            throw new ArgumentNullException(nameof(scope));

        var buckets = new Dictionary<(InventorySectionKey Section, InventoryRoleKind Role), RoleBucket>();
        foreach (var descriptor in scope.Inventories)
        {
            if (includeInventory != null && !includeInventory(descriptor))
                continue;

            var assigned = new HashSet<uint>();
            foreach (var role in descriptor.Roles)
            {
                var key = (descriptor.Section, role.Kind);
                if (!buckets.TryGetValue(key, out var bucket))
                    buckets[key] = bucket = new RoleBucket();
                bucket.AddMember(descriptor);

                foreach (var item in descriptor.Inventory.GetItems())
                {
                    if (assigned.Contains(item.ItemId) || !role.Accepts(item.Content.GetObjectId()))
                        continue;
                    assigned.Add(item.ItemId);
                    bucket.AddStack(descriptor, item);
                }
            }
        }

        var roles = buckets
            .OrderBy(pair => pair.Key.Section.Kind)
            .ThenBy(pair => pair.Key.Section.BlockDefinitionId.ToString(), StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Section.InventoryIndex)
            .ThenBy(pair => pair.Key.Role)
            .Select(pair => pair.Value.Create(pair.Key.Section, pair.Key.Role))
            .ToArray();
        return new InventoryProjection(scope, roles);
    }

    private sealed class RoleBucket
    {
        private readonly List<InventoryDescriptor> members = new();
        private readonly List<ProjectedInventoryStack> stacks = new();

        public void AddMember(InventoryDescriptor descriptor)
        {
            if (!members.Contains(descriptor))
                members.Add(descriptor);
        }

        public void AddStack(InventoryDescriptor descriptor, MyPhysicalInventoryItem item)
        {
            var target = stacks.FirstOrDefault(stack => stack.CanStack(item));
            if (target == null)
            {
                target = new ProjectedInventoryStack(item);
                stacks.Add(target);
            }
            target.Add(descriptor, item);
        }

        public InventoryRoleProjection Create(InventorySectionKey section, InventoryRoleKind role)
        {
            var uniqueInventories = members.Select(member => member.Inventory).Distinct().ToArray();
            return new InventoryRoleProjection(
                section,
                role,
                members.ToArray(),
                stacks.ToArray(),
                uniqueInventories.Aggregate(MyFixedPoint.Zero, (sum, inventory) => sum + inventory.CurrentMass),
                uniqueInventories.Aggregate(MyFixedPoint.Zero, (sum, inventory) => sum + inventory.CurrentVolume),
                uniqueInventories.Aggregate(MyFixedPoint.Zero, (sum, inventory) => sum + inventory.MaxVolume));
        }
    }
}
