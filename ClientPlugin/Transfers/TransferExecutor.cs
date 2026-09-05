using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Inventory;
using ClientPlugin.Profiles;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using VRage;
using VRage.Game;
using VRage.Game.Entity;

namespace ClientPlugin.Transfers;

public enum TransferOperationStatus
{
    Queued,
    Running,
    Complete,
    Partial,
    Failed,
    TimedOut,
    Cancelled
}

public sealed class TransferOperationResult
{
    public TransferOperationResult(TransferPlan plan)
    {
        Plan = plan;
        Status = TransferOperationStatus.Queued;
    }

    public TransferPlan Plan { get; }
    public TransferOperationStatus Status { get; internal set; }
    public MyFixedPoint MovedAmount { get; internal set; }
    public string Message { get; internal set; }
    public bool Quiet { get; set; }
    internal bool CancelRequested { get; set; }
}

public sealed class TransferExecutor
{
    private readonly Queue<QueuedOperation> operations = new();
    private QueuedOperation active;

    public event Action<TransferOperationResult> OperationFinished;

    public int PendingCount => operations.Count + (active == null ? 0 : 1);

    public TransferOperationResult Enqueue(
        TransferPlan plan,
        MyEntity interactedEntity,
        long identityId,
        Func<InventoryDescriptor, InventoryManagementFlags> getManagementFlags = null,
        Func<bool> canContinue = null,
        string guardFailureMessage = null,
        Action<TransferOperationResult> completed = null)
    {
        if (plan == null)
            throw new ArgumentNullException(nameof(plan));
        var result = new TransferOperationResult(plan);
        operations.Enqueue(new QueuedOperation(
            plan,
            interactedEntity,
            identityId,
            getManagementFlags ?? (_ => InventoryManagementFlags.None),
            () => (plan.CanContinue?.Invoke() ?? true) && (canContinue?.Invoke() ?? true),
            guardFailureMessage ?? plan.GuardFailureMessage,
            completed,
            result));
        return result;
    }

    public void Update()
    {
        if (Plugin.Instance.Companion?.Busy == true && active?.InFlight == null)
            return;
        var requestBudget = Math.Max(1, Math.Min(
            Config.Current.TransfersPerFrame,
            Config.Current.ReachabilityQueriesPerFrame));
        if (active == null && operations.Count > 0)
        {
            active = operations.Dequeue();
            active.Result.Status = TransferOperationStatus.Running;
        }
        if (active == null)
            return;

        if (active.InFlight != null)
        {
            if (TryAcknowledge(active.InFlight, out var moved))
            {
                active.Result.MovedAmount += moved;
                active.InFlight = null;
                active.NextAllocation++;
                if (!active.Result.CancelRequested && active.Result.MovedAmount >= active.Plan.RequestedAmount)
                {
                    Finish(active, TransferOperationStatus.Complete,
                        $"{active.Result.MovedAmount} / {active.Plan.RequestedAmount} moved");
                    return;
                }
            }
            else if (DateTime.UtcNow >= active.InFlight.Deadline)
            {
                Finish(active, TransferOperationStatus.TimedOut,
                    $"{active.Result.MovedAmount} / {active.Plan.RequestedAmount} moved: server acknowledgement timed out");
                return;
            }
            else
            {
                return;
            }
        }

        if (active.Result.CancelRequested)
        {
            Finish(active, TransferOperationStatus.Cancelled, "Cancelled; accepted transfers are not undone.");
            return;
        }

        if (active.CanContinue != null && !active.CanContinue())
        {
            Finish(active, TransferOperationStatus.Partial,
                $"{active.Result.MovedAmount} / {active.Plan.RequestedAmount} moved: " +
                (active.GuardFailureMessage ?? "operation precondition changed"));
            return;
        }

        while (requestBudget-- > 0 && active.NextAllocation < active.Plan.Allocations.Count)
        {
            var allocation = active.Plan.Allocations[active.NextAllocation];
            if (!TryPreflight(active, allocation, out var amount, out var reason))
            {
                active.LastFailureReason = reason;
                Plugin.Instance.Log.Debug("Unified transfer skipped {0}: {1} ({2}) -> {3} ({4}): {5}",
                    active.Plan.ItemId,
                    allocation.Source.Inventory.Owner?.DisplayName,
                    allocation.Source.Inventory.Owner?.EntityId,
                    allocation.DestinationInventory.Owner?.DisplayName,
                    allocation.DestinationInventory.Owner?.EntityId,
                    reason);
                active.NextAllocation++;
                // The plan and per-frame budget bound this scan. Three unreachable
                // candidates must not hide a valid fourth destination.
                continue;
            }

            var beforeSource = allocation.Source.Inventory.GetItemAmount(active.Plan.ItemId);
            var beforeDestination = allocation.DestinationInventory.GetItemAmount(active.Plan.ItemId);
            MyInventory.TransferByUser(
                allocation.Source.Inventory,
                allocation.DestinationInventory,
                allocation.Source.ItemId,
                -1,
                amount);
            active.InFlight = new InFlightTransfer(
                allocation,
                amount,
                beforeSource,
                beforeDestination,
                DateTime.UtcNow.AddMilliseconds(Math.Max(500, Config.Current.AcknowledgementTimeoutMilliseconds)));
            // In a local world TransferByUser can complete synchronously. Capture
            // that evidence before the next production tick replenishes the source.
            TryAcknowledge(active.InFlight, out _);
            return;
        }

        if (active.NextAllocation >= active.Plan.Allocations.Count && active.InFlight == null)
        {
            var status = active.Result.MovedAmount >= active.Plan.RequestedAmount
                ? TransferOperationStatus.Complete
                : TransferOperationStatus.Partial;
            Finish(active, status,
                $"{active.Result.MovedAmount} / {active.Plan.RequestedAmount} moved" +
                (status == TransferOperationStatus.Partial && active.LastFailureReason != null
                    ? $": {active.LastFailureReason}"
                    : string.Empty));
        }
    }

    // Cancel only this batch. An already sent request is still acknowledged before
    // releasing the executor, so replacement work cannot race that request.
    public void Cancel(IEnumerable<TransferOperationResult> batch)
    {
        var selected = new HashSet<TransferOperationResult>(batch);
        if (active != null && selected.Contains(active.Result))
            active.Result.CancelRequested = true;
        var count = operations.Count;
        for (var i = 0; i < count; i++)
        {
            var operation = operations.Dequeue();
            if (selected.Contains(operation.Result))
                Finish(operation, TransferOperationStatus.Cancelled, "Cancelled before sending.");
            else
                operations.Enqueue(operation);
        }
    }

    public void Clear(string reason = "cancelled")
    {
        if (active != null)
            Finish(active, TransferOperationStatus.Failed, reason);
        while (operations.Count > 0)
        {
            var operation = operations.Dequeue();
            operation.Result.Status = TransferOperationStatus.Failed;
            operation.Result.Message = reason;
            OperationFinished?.Invoke(operation.Result);
        }
    }

    private static bool TryPreflight(
        QueuedOperation operation,
        PhysicalTransferAllocation allocation,
        out MyFixedPoint amount,
        out string reason)
    {
        amount = MyFixedPoint.Zero;
        reason = null;
        var source = allocation.Source.Descriptor;
        var destination = allocation.DestinationDescriptor;
        var sourceInventory = allocation.Source.Inventory;
        var destinationInventory = allocation.DestinationInventory;
        if (ReferenceEquals(sourceInventory, destinationInventory))
        {
            reason = "source and destination are the same inventory";
            return false;
        }
        var sourceFlags = source == null ? InventoryManagementFlags.None : operation.GetManagementFlags(source);
        var destinationFlags = destination == null ? InventoryManagementFlags.None : operation.GetManagementFlags(destination);
        if ((sourceFlags & (InventoryManagementFlags.ManualBlock | InventoryManagementFlags.ReservedInventory)) != 0 ||
            (destinationFlags & (InventoryManagementFlags.ManualBlock | InventoryManagementFlags.ReservedInventory)) != 0)
        {
            reason = "inventory is excluded from bulk operations";
            return false;
        }
        if ((destinationFlags & InventoryManagementFlags.NoUnifiedCargoDestination) != 0 &&
            destination?.Section.Kind == InventorySectionKind.UnifiedCargo)
        {
            reason = "destination is excluded from Unified Cargo deposits";
            return false;
        }
        if (sourceInventory.Owner == null || destinationInventory.Owner == null ||
            sourceInventory.Owner.Closed || destinationInventory.Owner.Closed)
        {
            reason = "block was removed";
            return false;
        }
        if (!HasAccess(sourceInventory.Owner, operation.IdentityId) ||
            !HasAccess(destinationInventory.Owner, operation.IdentityId))
        {
            reason = "access denied";
            return false;
        }
        var item = sourceInventory.GetItemByID(allocation.Source.ItemId);
        if (!item.HasValue || item.Value.Content.GetObjectId() != operation.Plan.ItemId)
        {
            reason = "source stack changed";
            return false;
        }
        // CanSend/CanReceive control conveyor automation, not vanilla manual transfers.
        // Reactors are receive-only but users can still withdraw their fuel.
        if (!destinationInventory.CheckConstraint(operation.Plan.ItemId))
        {
            reason = "destination constraint rejects the item";
            return false;
        }
        amount = TransferPlanner.Normalize(
            operation.Plan.ItemId,
            MyFixedPoint.Min(operation.Plan.RequestedAmount - operation.Result.MovedAmount,
                    MyFixedPoint.Min(MyFixedPoint.Min(allocation.Amount,
                            operation.Plan.LimitAmount?.Invoke(allocation) ?? allocation.Amount),
                    MyFixedPoint.Min(item.Value.Amount,
                        destinationInventory.ComputeAmountThatFits(operation.Plan.ItemId)))));
        if (amount <= MyFixedPoint.Zero || !destinationInventory.CanItemsBeAdded(amount, operation.Plan.ItemId))
        {
            reason = "destination is full";
            return false;
        }
        if (!ConveyorReachability.CanTransfer(
                sourceInventory,
                destinationInventory,
                operation.InteractedEntity,
                operation.IdentityId,
                operation.Plan.ItemId))
        {
            reason = "no valid conveyor path";
            return false;
        }
        return true;
    }

    private static bool HasAccess(MyEntity entity, long identityId) =>
        entity is not MyTerminalBlock terminal || terminal.HasPlayerAccess(identityId);

    private static bool TryAcknowledge(InFlightTransfer transfer, out MyFixedPoint moved)
    {
        var currentSource = transfer.Allocation.Source.Inventory
            .GetItemAmount(transfer.Allocation.Source.DefinitionId);
        var currentDestination = transfer.Allocation.DestinationInventory
            .GetItemAmount(transfer.Allocation.Source.DefinitionId);
        var sourceDelta = MyFixedPoint.Max(transfer.BeforeSource - currentSource, MyFixedPoint.Zero);
        var destinationDelta = MyFixedPoint.Max(currentDestination - transfer.BeforeDestination, MyFixedPoint.Zero);
        // Both replicated inventories must corroborate movement. Stack IDs can change during
        // a local rearrangement; a missing source ID alone never establishes a successful transfer.
        transfer.SourceDecrease = MyFixedPoint.Max(transfer.SourceDecrease, sourceDelta);
        transfer.DestinationIncrease = MyFixedPoint.Max(transfer.DestinationIncrease, destinationDelta);
        moved = MyFixedPoint.Min(transfer.Requested, MyFixedPoint.Min(transfer.SourceDecrease, transfer.DestinationIncrease));
        return moved > MyFixedPoint.Zero;
    }

    private void Finish(QueuedOperation operation, TransferOperationStatus status, string message)
    {
        operation.Result.Status = status;
        operation.Result.Message = message;
        if (ReferenceEquals(active, operation))
            active = null;
        operation.Completed?.Invoke(operation.Result);
        OperationFinished?.Invoke(operation.Result);
    }

    private sealed class QueuedOperation
    {
        public QueuedOperation(
            TransferPlan plan,
            MyEntity interactedEntity,
            long identityId,
            Func<InventoryDescriptor, InventoryManagementFlags> getManagementFlags,
            Func<bool> canContinue,
            string guardFailureMessage,
            Action<TransferOperationResult> completed,
            TransferOperationResult result)
        {
            Plan = plan;
            InteractedEntity = interactedEntity;
            IdentityId = identityId;
            GetManagementFlags = getManagementFlags;
            CanContinue = canContinue;
            GuardFailureMessage = guardFailureMessage;
            Completed = completed;
            Result = result;
        }

        public TransferPlan Plan { get; }
        public MyEntity InteractedEntity { get; }
        public long IdentityId { get; }
        public Func<InventoryDescriptor, InventoryManagementFlags> GetManagementFlags { get; }
        public Func<bool> CanContinue { get; }
        public string GuardFailureMessage { get; }
        public Action<TransferOperationResult> Completed { get; }
        public TransferOperationResult Result { get; }
        public int NextAllocation { get; set; }
        public string LastFailureReason { get; set; }
        public InFlightTransfer InFlight { get; set; }
    }

    private sealed class InFlightTransfer
    {
        public InFlightTransfer(
            PhysicalTransferAllocation allocation,
            MyFixedPoint requested,
            MyFixedPoint beforeSource,
            MyFixedPoint beforeDestination,
            DateTime deadline)
        {
            Allocation = allocation;
            Requested = requested;
            BeforeSource = beforeSource;
            BeforeDestination = beforeDestination;
            Deadline = deadline;
        }

        public PhysicalTransferAllocation Allocation { get; }
        public MyFixedPoint Requested { get; }
        public MyFixedPoint BeforeSource { get; }
        public MyFixedPoint BeforeDestination { get; }
        public DateTime Deadline { get; }
        public MyFixedPoint SourceDecrease { get; set; }
        public MyFixedPoint DestinationIncrease { get; set; }
    }
}

public static class ConveyorReachability
{
    [ThreadStatic]
    private static List<IMyConveyorEndpoint> reachable;

    public static bool CanTransfer(
        MyInventory source,
        MyInventory destination,
        MyEntity interactedEntity,
        long identityId,
        MyDefinitionId itemId)
    {
        if (source == null || destination == null)
            return false;
        if (ReferenceEquals(source, destination) || ReferenceEquals(source.Owner, destination.Owner))
            return true;

        var sourceEndpoint = ResolveEndpoint(source, interactedEntity);
        var destinationEndpoint = ResolveEndpoint(destination, interactedEntity);
        if (sourceEndpoint == null || destinationEndpoint == null)
            return false;

        reachable ??= new List<IMyConveyorEndpoint>();
        reachable.Clear();
        try
        {
            MyGridConveyorSystem.AppendReachableEndpoints(
                sourceEndpoint,
                identityId,
                reachable,
                itemId,
                endpoint => ReferenceEquals(endpoint, destinationEndpoint.ConveyorEndpoint));
            return reachable.Contains(destinationEndpoint.ConveyorEndpoint) &&
                   MyGridConveyorSystem.Reachable(
                       sourceEndpoint.ConveyorEndpoint,
                       destinationEndpoint.ConveyorEndpoint);
        }
        finally
        {
            reachable.Clear();
        }
    }

    private static IMyConveyorEndpointBlock ResolveEndpoint(MyInventory inventory, MyEntity interactedEntity)
    {
        var entity = inventory.IsCharacterOwner ? interactedEntity : inventory.Owner;
        if (entity is IMyConveyorEndpointBlock endpoint)
            return endpoint;
        return entity?.Components.TryGet<IMyConveyorEndpointBlock>(out var component) == true
            ? component
            : null;
    }
}
