using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Inventory;
using ClientPlugin.Profiles;
using Sandbox.Game;
using VRage;
using VRage.Game;
using VRage.Game.Entity;

namespace ClientPlugin.Transfers;

public static class TransferPlanFactory
{
    public static TransferPlan Withdraw(
        ProjectedInventoryStack projectedStack,
        MyInventory destination,
        MyFixedPoint amount,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags = null)
    {
        if (projectedStack == null)
            throw new ArgumentNullException(nameof(projectedStack));
        if (destination == null)
            throw new ArgumentNullException(nameof(destination));
        getFlags ??= _ => InventoryManagementFlags.None;
        var itemId = projectedStack.DefinitionId;
        var requested = TransferPlanner.Normalize(itemId, amount);
        var allocations = new List<PhysicalTransferAllocation>();
        foreach (var source in projectedStack.Sources
                     .Where(candidate => !ReferenceEquals(candidate.Inventory, destination))
                     .Where(candidate => candidate.Descriptor == null ||
                         (getFlags(candidate.Descriptor) & (InventoryManagementFlags.ManualBlock |
                                                            InventoryManagementFlags.ReservedInventory)) == 0)
                     .OrderByDescending(candidate => candidate.SnapshotAmount.RawValue)
                     .ThenBy(candidate => candidate.Descriptor?.OwnerEntityId ?? 0L)
                     .ThenBy(candidate => candidate.ItemId))
        {
            var allocation = TransferPlanner.Normalize(
                itemId,
                MyFixedPoint.Min(requested, source.SnapshotAmount));
            if (allocation <= MyFixedPoint.Zero)
                continue;
            allocations.Add(new PhysicalTransferAllocation(source, destination, allocation));
        }
        return new TransferPlan(itemId, requested, allocations);
    }

    public static TransferPlan Deposit(
        MyInventory source,
        MyPhysicalInventoryItem item,
        MyFixedPoint amount,
        IEnumerable<InventoryDescriptor> destinationCandidates,
        DistributionPolicy policy,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags = null,
        bool allowFallbacks = true)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));
        if (destinationCandidates == null)
            throw new ArgumentNullException(nameof(destinationCandidates));
        getFlags ??= _ => InventoryManagementFlags.None;
        var itemId = item.Content.GetObjectId();
        var destinations = CreateDestinationSnapshots(itemId,
            destinationCandidates.Where(candidate => !ReferenceEquals(candidate.Inventory, source)), getFlags);
        var plannedDestinations = TransferPlanner.PlanDestinations(policy, itemId, amount, destinations);
        if (!allowFallbacks)
            return TransferPlanner.Pair(itemId, amount, new[] { new InventoryStackReference(source, item) }, plannedDestinations);
        return PairWithFallbacks(
            itemId,
            amount,
            new[] { new InventoryStackReference(source, item) },
            plannedDestinations,
            destinations);
    }

    public static TransferPlan BetweenScopes(
        ProjectedInventoryStack source,
        MyFixedPoint amount,
        IEnumerable<InventoryDescriptor> destinationCandidates,
        DistributionPolicy policy,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags = null)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));
        getFlags ??= _ => InventoryManagementFlags.None;
        var destinations = CreateDestinationSnapshots(source.DefinitionId, destinationCandidates, getFlags);
        var plannedDestinations = TransferPlanner.PlanDestinations(
            policy,
            source.DefinitionId,
            amount,
            destinations);
        var sources = source.Sources.Where(candidate => candidate.Descriptor == null ||
                (getFlags(candidate.Descriptor) & (InventoryManagementFlags.ManualBlock |
                                                   InventoryManagementFlags.ReservedInventory)) == 0)
            .ToArray();
        return PairWithFallbacks(
            source.DefinitionId,
            amount,
            sources,
            plannedDestinations,
            destinations);
    }

    public static IReadOnlyList<TransferPlan> Rebalance(
        InventoryRoleProjection role,
        DistributionPolicy policy,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags = null)
    {
        if (role == null)
            throw new ArgumentNullException(nameof(role));
        getFlags ??= _ => InventoryManagementFlags.None;
        var managedMembers = role.Members.Where(member =>
        {
            var flags = getFlags(member);
            return (flags & (InventoryManagementFlags.ManualBlock |
                             InventoryManagementFlags.ReservedInventory)) == 0 &&
                   (member.Section.Kind != InventorySectionKind.UnifiedCargo ||
                    (flags & InventoryManagementFlags.NoUnifiedCargoDestination) == 0);
        }).ToArray();
        var plans = new List<TransferPlan>();
        var managedSet = new HashSet<InventoryDescriptor>(managedMembers);
        var virtualVolume = managedMembers.ToDictionary(
            member => member.Inventory,
            member => member.Inventory.CurrentVolume);
        var virtualMass = managedMembers.ToDictionary(
            member => member.Inventory,
            member => member.Inventory.CurrentMass);
        // Balance quantities by definition, not by display stack. Stateful tools/bottles
        // remain separate physical items but must share one set of per-inventory targets.
        foreach (var projected in role.Stacks.GroupBy(stack => stack.DefinitionId)
                     .OrderBy(group => group.Key.ToString(), StringComparer.Ordinal))
        {
            var itemId = projected.Key;
            var managedSources = projected.SelectMany(stack => stack.Sources)
                .Where(source => source.Descriptor != null && managedSet.Contains(source.Descriptor))
                .ToArray();
            var byInventory = managedSources
                .GroupBy(source => source.Inventory)
                .ToDictionary(group => group.Key, group => group.Aggregate(
                    MyFixedPoint.Zero,
                    (sum, source) => sum + source.SnapshotAmount));
            var total = byInventory.Values.Aggregate(MyFixedPoint.Zero, (sum, value) => sum + value);
            if (total <= MyFixedPoint.Zero)
                continue;
            MyInventory.GetItemVolumeAndMass(itemId, out var itemMass, out var itemVolume);
            var snapshots = managedMembers
                .Where(member => role.Accepts(member, itemId))
                .Select(member => new DestinationSnapshot(
                    member,
                    byInventory.TryGetValue(member.Inventory, out var current) ? current : MyFixedPoint.Zero,
                    (byInventory.TryGetValue(member.Inventory, out current) ? current : MyFixedPoint.Zero) +
                    ComputeVirtualAmountThatFits(
                        member.Inventory,
                        itemId,
                        virtualVolume[member.Inventory],
                        virtualMass[member.Inventory],
                        itemVolume,
                        itemMass),
                    member.Inventory.MaxVolume > MyFixedPoint.Zero
                        ? (double)virtualVolume[member.Inventory] / (double)member.Inventory.MaxVolume
                        : 1d))
                .ToArray();
            var targets = TransferPlanner.PlanTargetTotals(policy, itemId, total, snapshots)
                .ToDictionary(allocation => allocation.Destination.Inventory, allocation => allocation.Amount);
            var deficits = new List<DestinationAllocation>();
            foreach (var member in managedMembers)
            {
                var current = byInventory.TryGetValue(member.Inventory, out var amount) ? amount : MyFixedPoint.Zero;
                var target = targets.TryGetValue(member.Inventory, out amount) ? amount : MyFixedPoint.Zero;
                var deficit = MyFixedPoint.Floor(MyFixedPoint.Max(target - current, MyFixedPoint.Zero));
                if (deficit > MyFixedPoint.Zero)
                    deficits.Add(new DestinationAllocation(member, deficit));
            }

            var surplusSources = new List<InventoryStackReference>();
            foreach (var inventoryGroup in managedSources.GroupBy(source => source.Inventory))
            {
                var current = inventoryGroup.Aggregate(MyFixedPoint.Zero, (sum, source) => sum + source.SnapshotAmount);
                var target = targets.TryGetValue(inventoryGroup.Key, out var targetAmount)
                    ? targetAmount
                    : MyFixedPoint.Zero;
                var surplus = MyFixedPoint.Floor(MyFixedPoint.Max(current - target, MyFixedPoint.Zero));
                foreach (var source in inventoryGroup.OrderByDescending(candidate => candidate.SnapshotAmount.RawValue))
                {
                    if (surplus <= MyFixedPoint.Zero)
                        break;
                    var available = MyFixedPoint.Floor(MyFixedPoint.Min(source.SnapshotAmount, surplus));
                    if (available <= MyFixedPoint.Zero) continue;
                    var adjusted = source;
                    adjusted = new InventoryStackReferenceWithAmount(source, available).ToReference();
                    surplusSources.Add(adjusted);
                    surplus -= available;
                }
            }
            var requested = deficits.Aggregate(MyFixedPoint.Zero, (sum, deficit) => sum + deficit.Amount);
            var plan = TransferPlanner.Pair(itemId, requested, surplusSources, deficits);
            if (plan.PlannedAmount > MyFixedPoint.Zero)
            {
                // Only reserve space for transfers actually planned, not tolerated
                // fractional differences or unmatched rounded deficits.
                foreach (var allocation in plan.Allocations)
                {
                    virtualVolume[allocation.Source.Inventory] -= allocation.Amount * itemVolume;
                    virtualMass[allocation.Source.Inventory] -= allocation.Amount * itemMass;
                    virtualVolume[allocation.DestinationInventory] += allocation.Amount * itemVolume;
                    virtualMass[allocation.DestinationInventory] += allocation.Amount * itemMass;
                }
                plans.Add(new TransferPlan(itemId, plan.PlannedAmount, plan.Allocations));
            }
        }
        return plans;
    }

    private static MyFixedPoint ComputeVirtualAmountThatFits(
        MyInventory inventory,
        MyDefinitionId itemId,
        MyFixedPoint currentVolume,
        MyFixedPoint currentMass,
        float itemVolume,
        float itemMass)
    {
        if (!inventory.IsConstrained)
            return inventory.ComputeAmountThatFits(itemId);
        var byVolume = itemVolume <= 0f
            ? MyFixedPoint.MaxValue
            : MyFixedPoint.Max((inventory.MaxVolume - currentVolume) * (1f / itemVolume), MyFixedPoint.Zero);
        var byMass = itemMass <= 0f
            ? MyFixedPoint.MaxValue
            : MyFixedPoint.Max((inventory.MaxMass - currentMass) * (1f / itemMass), MyFixedPoint.Zero);
        return TransferPlanner.Normalize(itemId, MyFixedPoint.Min(byVolume, byMass));
    }

    private static TransferPlan PairWithFallbacks(
        MyDefinitionId itemId,
        MyFixedPoint amount,
        IReadOnlyList<InventoryStackReference> sources,
        IReadOnlyList<DestinationAllocation> plannedDestinations,
        IReadOnlyList<DestinationSnapshot> allDestinations)
    {
        var primary = TransferPlanner.Pair(itemId, amount, sources, plannedDestinations);
        var allocations = primary.Allocations.ToList();
        var requested = TransferPlanner.Normalize(itemId, amount);
        foreach (var destination in allDestinations.OrderBy(candidate => candidate.Inventory.OwnerEntityId)
                     .ThenBy(candidate => candidate.Inventory.InventoryIndex))
        foreach (var source in sources.OrderByDescending(candidate => candidate.SnapshotAmount.RawValue)
                     .ThenBy(candidate => candidate.Descriptor?.OwnerEntityId ?? 0L)
                     .ThenBy(candidate => candidate.ItemId))
        {
            if (ReferenceEquals(source.Inventory, destination.Inventory.Inventory))
                continue;
            var fallbackAmount = TransferPlanner.Normalize(itemId,
                MyFixedPoint.Min(requested,
                    MyFixedPoint.Min(source.SnapshotAmount, destination.Capacity)));
            if (fallbackAmount > MyFixedPoint.Zero)
                allocations.Add(new PhysicalTransferAllocation(source, destination.Inventory, fallbackAmount));
        }
        return new TransferPlan(itemId, requested, allocations);
    }

    public static IReadOnlyList<DestinationSnapshot> CreateDestinationSnapshots(
        MyDefinitionId itemId,
        IEnumerable<InventoryDescriptor> candidates,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags)
    {
        return candidates
            .Where(candidate =>
            {
                var flags = getFlags(candidate);
                return (flags & (InventoryManagementFlags.ManualBlock |
                                 InventoryManagementFlags.ReservedInventory)) == 0 &&
                       (candidate.Section.Kind != InventorySectionKind.UnifiedCargo ||
                        (flags & InventoryManagementFlags.NoUnifiedCargoDestination) == 0) &&
                       candidate.Roles.Any(role => role.Accepts(itemId)) &&
                       candidate.Inventory.CheckConstraint(itemId);
            })
            .Select(candidate => new DestinationSnapshot(
                candidate,
                candidate.Inventory.GetItemAmount(itemId),
                candidate.Inventory.ComputeAmountThatFits(itemId)))
            .ToArray();
    }

    private readonly struct InventoryStackReferenceWithAmount
    {
        private readonly InventoryStackReference source;
        private readonly MyFixedPoint amount;

        public InventoryStackReferenceWithAmount(InventoryStackReference source, MyFixedPoint amount)
        {
            this.source = source;
            this.amount = amount;
        }

        public InventoryStackReference ToReference()
        {
            var item = source.Inventory.GetItemByID(source.ItemId);
            if (!item.HasValue)
                return source;
            var adjusted = item.Value;
            adjusted.Amount = amount;
            return source.Descriptor == null
                ? new InventoryStackReference(source.Inventory, adjusted)
                : new InventoryStackReference(source.Descriptor, adjusted);
        }
    }
}
