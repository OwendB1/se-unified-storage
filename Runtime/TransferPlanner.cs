using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Inventory;
using Sandbox.Game;
using VRage;
using VRage.Game;

namespace ClientPlugin.Transfers;

public sealed class DestinationSnapshot
{
    public DestinationSnapshot(
        InventoryDescriptor inventory,
        MyFixedPoint currentItemAmount,
        MyFixedPoint capacity,
        double? fillRatio = null)
    {
        Inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        CurrentItemAmount = MyFixedPoint.Max(currentItemAmount, MyFixedPoint.Zero);
        Capacity = MyFixedPoint.Max(capacity, MyFixedPoint.Zero);
        FillRatio = fillRatio ?? (inventory.Inventory.MaxVolume > MyFixedPoint.Zero
            ? (double)inventory.Inventory.CurrentVolume / (double)inventory.Inventory.MaxVolume
            : 1d);
    }

    public InventoryDescriptor Inventory { get; }
    public MyFixedPoint CurrentItemAmount { get; }
    public MyFixedPoint Capacity { get; }
    public double FillRatio { get; }
}

public readonly struct DestinationAllocation
{
    public DestinationAllocation(InventoryDescriptor destination, MyFixedPoint amount)
    {
        Destination = destination;
        Amount = amount;
    }

    public InventoryDescriptor Destination { get; }
    public MyFixedPoint Amount { get; }
}

public readonly struct PhysicalTransferAllocation
{
    public PhysicalTransferAllocation(
        InventoryStackReference source,
        InventoryDescriptor destination,
        MyFixedPoint amount)
    {
        Source = source;
        DestinationDescriptor = destination ?? throw new ArgumentNullException(nameof(destination));
        DestinationInventory = destination.Inventory;
        Amount = amount;
    }

    public PhysicalTransferAllocation(
        InventoryStackReference source,
        MyInventory destination,
        MyFixedPoint amount)
    {
        Source = source;
        DestinationInventory = destination ?? throw new ArgumentNullException(nameof(destination));
        Amount = amount;
    }

    public InventoryStackReference Source { get; }
    public InventoryDescriptor DestinationDescriptor { get; }
    public MyInventory DestinationInventory { get; }
    public MyFixedPoint Amount { get; }
}

public sealed class TransferPlan
{
    public Func<bool> CanContinue { get; set; }
    public string GuardFailureMessage { get; set; }
    public Func<PhysicalTransferAllocation, MyFixedPoint> LimitAmount { get; set; }
    public TransferPlan(
        MyDefinitionId itemId,
        MyFixedPoint requestedAmount,
        IReadOnlyList<PhysicalTransferAllocation> allocations)
    {
        ItemId = itemId;
        RequestedAmount = requestedAmount;
        Allocations = allocations ?? throw new ArgumentNullException(nameof(allocations));
    }

    public MyDefinitionId ItemId { get; }
    public MyFixedPoint RequestedAmount { get; }
    public IReadOnlyList<PhysicalTransferAllocation> Allocations { get; }
    public MyFixedPoint PlannedAmount => Allocations.Aggregate(
        MyFixedPoint.Zero,
        (sum, allocation) => sum + allocation.Amount);
}

public static class TransferPlanner
{
    public static IReadOnlyList<DestinationAllocation> PlanDestinations(
        DistributionPolicy policy,
        MyDefinitionId itemId,
        MyFixedPoint requestedAmount,
        IEnumerable<DestinationSnapshot> candidates)
    {
        if (candidates == null)
            throw new ArgumentNullException(nameof(candidates));
        var amount = Normalize(itemId, requestedAmount);
        if (amount <= MyFixedPoint.Zero)
            return Array.Empty<DestinationAllocation>();

        var usable = candidates
            .Where(candidate => candidate.Capacity > MyFixedPoint.Zero)
            .OrderBy(candidate => candidate.Inventory.OwnerEntityId)
            .ThenBy(candidate => candidate.Inventory.InventoryIndex)
            .ToArray();
        return policy switch
        {
            DistributionPolicy.ExistingStackFirst => Greedy(
                usable.OrderByDescending(candidate => candidate.CurrentItemAmount > MyFixedPoint.Zero)
                    .ThenByDescending(candidate => candidate.CurrentItemAmount.RawValue)
                    .ThenBy(candidate => candidate.Inventory.OwnerEntityId),
                itemId,
                amount),
            DistributionPolicy.FillFirst => Greedy(
                usable.OrderByDescending(candidate => candidate.FillRatio)
                    .ThenBy(candidate => candidate.Inventory.OwnerEntityId),
                itemId,
                amount),
            DistributionPolicy.EvenByItem => Even(usable, itemId, amount),
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
        };
    }

    public static IReadOnlyList<DestinationAllocation> PlanTargetTotals(
        DistributionPolicy policy,
        MyDefinitionId itemId,
        MyFixedPoint totalAmount,
        IEnumerable<DestinationSnapshot> candidates)
    {
        if (candidates == null)
            throw new ArgumentNullException(nameof(candidates));
        var amount = Normalize(itemId, totalAmount);
        if (amount <= MyFixedPoint.Zero)
            return Array.Empty<DestinationAllocation>();

        var usable = candidates
            .Where(candidate => candidate.Capacity > MyFixedPoint.Zero)
            .OrderBy(candidate => candidate.Inventory.OwnerEntityId)
            .ThenBy(candidate => candidate.Inventory.InventoryIndex)
            .ToArray();
        IEnumerable<DestinationSnapshot> ordered = policy switch
        {
            DistributionPolicy.ExistingStackFirst => usable
                .OrderByDescending(candidate => candidate.CurrentItemAmount > MyFixedPoint.Zero)
                .ThenByDescending(candidate => candidate.CurrentItemAmount.RawValue)
                .ThenBy(candidate => candidate.Inventory.OwnerEntityId),
            DistributionPolicy.FillFirst => usable
                .OrderByDescending(candidate => candidate.FillRatio)
                .ThenBy(candidate => candidate.Inventory.OwnerEntityId),
            DistributionPolicy.EvenByItem => usable,
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
        };
        var candidatesByKey = ordered.Select((candidate, index) => (candidate, index))
            .ToDictionary(pair => (long)pair.index, pair => pair.candidate);
        var coreCandidates = candidatesByKey.Select(pair => new DistributionCandidateCore(
            pair.Key,
            0,
            pair.Value.Capacity.RawValue));
        var quantum = IsFractional(itemId) ? 1L : 1_000_000L;
        var allocations = DistributionPlannerCore.PreferWholeUnits(amount.RawValue, coreCandidates, quantum,
            policy == DistributionPolicy.EvenByItem);
        return allocations.Select(allocation => new DestinationAllocation(
            candidatesByKey[allocation.Key].Inventory,
            FromRaw(allocation.Amount))).ToArray();
    }

    public static TransferPlan Pair(
        MyDefinitionId itemId,
        MyFixedPoint requestedAmount,
        IEnumerable<InventoryStackReference> sourceStacks,
        IEnumerable<DestinationAllocation> destinations,
        Func<InventoryStackReference, InventoryDescriptor, bool> canPair = null)
    {
        if (sourceStacks == null)
            throw new ArgumentNullException(nameof(sourceStacks));
        if (destinations == null)
            throw new ArgumentNullException(nameof(destinations));

        var remainingRequest = Normalize(itemId, requestedAmount);
        var sources = sourceStacks
            .Where(source => source.DefinitionId == itemId && source.SnapshotAmount > MyFixedPoint.Zero)
            .OrderByDescending(source => source.SnapshotAmount.RawValue)
            .ThenBy(source => source.Descriptor?.OwnerEntityId ?? source.Inventory.Owner?.EntityId ?? 0L)
            .ThenBy(source => source.ItemId)
            .Select(source => new RemainingSource(source))
            .ToArray();
        var result = new List<PhysicalTransferAllocation>();
        foreach (var destination in destinations)
        {
            var destinationRemaining = Normalize(itemId, destination.Amount);
            foreach (var source in sources)
            {
                if (remainingRequest <= MyFixedPoint.Zero || destinationRemaining <= MyFixedPoint.Zero)
                    break;
                if (source.Amount <= MyFixedPoint.Zero ||
                    ReferenceEquals(source.Source.Inventory, destination.Destination.Inventory) ||
                    (canPair != null && !canPair(source.Source, destination.Destination)))
                    continue;
                var amount = Normalize(
                    itemId,
                    MyFixedPoint.Min(remainingRequest, MyFixedPoint.Min(destinationRemaining, source.Amount)));
                if (amount <= MyFixedPoint.Zero)
                    continue;
                result.Add(new PhysicalTransferAllocation(source.Source, destination.Destination, amount));
                source.Amount -= amount;
                destinationRemaining -= amount;
                remainingRequest -= amount;
            }
            if (remainingRequest <= MyFixedPoint.Zero)
                break;
        }
        return new TransferPlan(itemId, Normalize(itemId, requestedAmount), result);
    }

    public static MyFixedPoint Normalize(MyDefinitionId itemId, MyFixedPoint amount)
    {
        amount = MyFixedPoint.Max(amount, MyFixedPoint.Zero);
        return IsFractional(itemId) ? amount : MyFixedPoint.Floor(amount);
    }

    private static IReadOnlyList<DestinationAllocation> Greedy(
        IEnumerable<DestinationSnapshot> ordered,
        MyDefinitionId itemId,
        MyFixedPoint amount)
    {
        var candidates = ordered.ToArray();
        var byKey = candidates.Select((candidate, index) => (candidate, index))
            .ToDictionary(pair => (long)pair.index, pair => pair.candidate);
        return DistributionPlannerCore.PreferWholeUnits(
                amount.RawValue,
                byKey.Select(pair => new DistributionCandidateCore(
                    pair.Key,
                    pair.Value.CurrentItemAmount.RawValue,
                    pair.Value.Capacity.RawValue)),
                IsFractional(itemId) ? 1L : 1_000_000L, even: false)
            .Select(allocation => new DestinationAllocation(
                byKey[allocation.Key].Inventory,
                FromRaw(allocation.Amount)))
            .ToArray();
    }

    private static IReadOnlyList<DestinationAllocation> Even(
        IReadOnlyList<DestinationSnapshot> candidates,
        MyDefinitionId itemId,
        MyFixedPoint amount)
    {
        var byKey = candidates.Select((candidate, index) => (candidate, index))
            .ToDictionary(pair => (long)pair.index, pair => pair.candidate);
        return DistributionPlannerCore.PreferWholeUnits(
                amount.RawValue,
                byKey.Select(pair => new DistributionCandidateCore(
                    pair.Key,
                    pair.Value.CurrentItemAmount.RawValue,
                    pair.Value.Capacity.RawValue)),
                IsFractional(itemId) ? 1L : 1_000_000L, even: true)
            .Select(allocation => new DestinationAllocation(
                byKey[allocation.Key].Inventory,
                FromRaw(allocation.Amount)))
            .ToArray();
    }

    private static bool IsFractional(MyDefinitionId itemId) =>
        itemId.TypeId == typeof(MyObjectBuilder_Ore) || itemId.TypeId == typeof(MyObjectBuilder_Ingot);

    private static MyFixedPoint FromRaw(long rawValue)
    {
        var value = MyFixedPoint.Zero;
        value.RawValue = rawValue;
        return value;
    }

    private sealed class RemainingSource
    {
        public RemainingSource(InventoryStackReference source)
        {
            Source = source;
            Amount = source.SnapshotAmount;
        }

        public InventoryStackReference Source { get; }
        public MyFixedPoint Amount { get; set; }
    }

}
