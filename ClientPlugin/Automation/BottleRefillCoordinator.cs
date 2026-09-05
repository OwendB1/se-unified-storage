using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClientPlugin.Inventory;
using ClientPlugin.Profiles;
using ClientPlugin.Transfers;
using HarmonyLib;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.Entity;

namespace ClientPlugin.Automation;

public sealed class BottleRefillCoordinator
{
    private readonly Queue<BottleStage> stages = new();
    private static readonly MethodInfo TankRefill = AccessTools.Method(typeof(MyGasTank), "SendRefillRequest");
    private BottleStage active;
    private TransferOperationResult pendingTransfer;
    private DateTime refillDeadline;

    public bool IsRunning => active != null || stages.Count > 0 || pendingTransfer != null;

    public void Start(
        InventoryProjection projection,
        ScopeProfile profile,
        MyEntity interactedEntity,
        long identityId,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags = null)
    {
        if (IsRunning)
            return;
        getFlags ??= _ => InventoryManagementFlags.None;
        var flagSnapshot = projection.Scope.Inventories.ToDictionary(
            descriptor => (descriptor.OwnerEntityId, descriptor.InventoryIndex),
            getFlags);
        InventoryManagementFlags CapturedFlags(InventoryDescriptor descriptor) =>
            flagSnapshot.TryGetValue((descriptor.OwnerEntityId, descriptor.InventoryIndex), out var value)
                ? value | getFlags(descriptor)
                : InventoryManagementFlags.ManualBlock;
        var fillers = projection.Scope.Inventories.Where(descriptor =>
                descriptor.Roles.Any(role => role.Kind == InventoryRoleKind.Bottles) &&
                (CapturedFlags(descriptor) & (InventoryManagementFlags.ManualBlock |
                                              InventoryManagementFlags.ReservedInventory)) == 0 &&
                IsUsableFiller(descriptor.Owner))
            .ToArray();
        foreach (var source in projection.Scope.Inventories.Where(descriptor =>
                     (CapturedFlags(descriptor) & (InventoryManagementFlags.ManualBlock |
                                                   InventoryManagementFlags.ReservedInventory)) == 0))
        foreach (var item in source.Inventory.GetItems())
        {
            if (item.Content is not MyObjectBuilder_GasContainerObject bottle || bottle.GasLevel > 0f)
                continue;
            var definitionId = item.Content.GetObjectId();
            var filler = fillers.FirstOrDefault(candidate =>
                !ReferenceEquals(candidate.Inventory, source.Inventory) &&
                candidate.Inventory.CheckConstraint(definitionId) &&
                candidate.Inventory.GetItemAmount(definitionId) == MyFixedPoint.Zero);
            if (filler != null)
            {
                if (stages.Count >= 16) break;
                var single = item;
                single.Amount = (MyFixedPoint)1;
                stages.Enqueue(new BottleStage(source, single, filler, interactedEntity, identityId, CapturedFlags,
                    projection.Scope, profile.Policy));
            }
        }
        Plugin.Instance.Transfers.OperationFinished += TransferFinished;
    }

    public void Update()
    {
        if (pendingTransfer != null)
            return;
        if (active == null)
        {
            if (stages.Count == 0)
            {
                Plugin.Instance.Transfers.OperationFinished -= TransferFinished;
                return;
            }
            active = stages.Dequeue();
            var source = new InventoryStackReference(active.Source, active.OriginalItem);
            QueueTransfer(new TransferPlan(
                source.DefinitionId,
                source.SnapshotAmount,
                new[] { new PhysicalTransferAllocation(source, active.Filler, source.SnapshotAmount) }));
            active.State = BottleStageState.MovingToFiller;
            return;
        }
        if (active.State != BottleStageState.WaitingForRefill)
            return;
        var filled = FindBottle(active.Filler.Inventory, active.DefinitionId, active.StagedItemId, requireFilled: true);
        if (filled.HasValue)
        {
            active.WasFilled = true;
            QueueReturn(filled.Value);
            return;
        }
        if (DateTime.UtcNow < refillDeadline)
            return;
        var unfilled = FindBottle(active.Filler.Inventory, active.DefinitionId, active.StagedItemId, requireFilled: false);
        if (unfilled.HasValue)
            QueueReturn(unfilled.Value);
        else
            CompleteStage("bottle disappeared while waiting for refill");
    }

    public void Clear()
    {
        stages.Clear();
        active = null;
        pendingTransfer = null;
        if (Plugin.Instance?.Transfers != null)
            Plugin.Instance.Transfers.OperationFinished -= TransferFinished;
    }

    private void TransferFinished(TransferOperationResult result)
    {
        if (!ReferenceEquals(result, pendingTransfer))
            return;
        pendingTransfer = null;
        if (result.Status is not (TransferOperationStatus.Complete or TransferOperationStatus.Partial) ||
            result.MovedAmount <= MyFixedPoint.Zero)
        {
            CompleteStage(result.Message);
            return;
        }
        if (active.State == BottleStageState.MovingToFiller)
        {
            var staged = active.Filler.Inventory.GetItems().Where(item => item.Content.GetObjectId() == active.DefinitionId).ToArray();
            if (staged.Length != 1 || staged[0].Amount != (MyFixedPoint)1)
            {
                CompleteStage("staged bottle identity is ambiguous; no further transfers");
                return;
            }
            active.StagedItemId = staged[0].ItemId;
            switch (active.Filler.Owner)
            {
                case MyGasGenerator generator:
                    generator.SendRefillRequest();
                    break;
                case MyGasTank tank:
                    if (TankRefill == null)
                    {
                        CompleteStage("tank refill request is unavailable in this game version");
                        return;
                    }
                    TankRefill.Invoke(tank, null);
                    break;
                default:
                    CompleteStage("unsupported filler");
                    return;
            }
            active.State = BottleStageState.WaitingForRefill;
            refillDeadline = DateTime.UtcNow.AddMilliseconds(
                Math.Max(1000, Config.Current.AcknowledgementTimeoutMilliseconds));
        }
        else if (active.State == BottleStageState.Returning)
            CompleteStage(result.Message);
    }

    private void QueueReturn(MyPhysicalInventoryItem item)
    {
        var amount = MyFixedPoint.Min(item.Amount, active.OriginalItem.Amount);
        var source = new InventoryStackReference(active.Filler, item);
        active.State = BottleStageState.Returning;
        var fallback = TransferPlanFactory.Deposit(active.Filler.Inventory, item, amount,
            active.Scope.Inventories.Where(member => member.Section.Kind == InventorySectionKind.UnifiedCargo &&
                !member.Owner.Closed && !ReferenceEquals(member.Inventory, active.Source.Inventory) &&
                !ReferenceEquals(member.Inventory, active.Filler.Inventory)), active.Policy, active.GetFlags);
        // One bounded operation tries the original first, then normal cargo destinations.
        // The executor clamps to the remaining amount and stops on uncertain outcomes.
        var allocations = new[] { new PhysicalTransferAllocation(source, active.Source, amount) }
            .Concat(fallback.Allocations.Select(allocation =>
                new PhysicalTransferAllocation(source, allocation.DestinationDescriptor, allocation.Amount)));
        QueueTransfer(new TransferPlan(
            source.DefinitionId,
            amount,
            allocations.ToArray()));
    }

    private void QueueTransfer(TransferPlan plan)
    {
        pendingTransfer = Plugin.Instance.Transfers.Enqueue(
            plan,
            active.InteractedEntity,
            active.IdentityId,
            active.GetFlags);
    }

    private void CompleteStage(string message)
    {
        Plugin.Instance.Log.Info("Bottle refill stage for {0} ({1}): {2}",
            active?.DefinitionId,
            active?.WasFilled == true ? "filled" : "returned unfilled or stranded",
            message);
        active = null;
    }

    private static MyPhysicalInventoryItem? FindBottle(
        Sandbox.Game.MyInventory inventory,
        MyDefinitionId definitionId,
        uint stagedItemId,
        bool requireFilled)
    {
        foreach (var item in inventory.GetItems())
            if (item.ItemId == stagedItemId && item.Amount == (MyFixedPoint)1 && item.Content.GetObjectId() == definitionId &&
                item.Content is MyObjectBuilder_GasContainerObject bottle &&
                (!requireFilled || bottle.GasLevel > 0f))
                return item;
        return null;
    }

    private static bool IsUsableFiller(Sandbox.Game.Entities.MyCubeBlock owner) => owner switch
    {
        MyGasTank tank => tank.IsWorking && tank.Enabled && tank.FilledRatio > 0d,
        MyGasGenerator generator => generator.CanProduce &&
                                    (MySession.Static.CreativeMode || generator.GetInventory()
                                        .GetItemAmount(new MyDefinitionId(typeof(MyObjectBuilder_Ore), "Ice")) > 0),
        _ => false
    };

    private enum BottleStageState
    {
        MovingToFiller,
        WaitingForRefill,
        Returning
    }

    private sealed class BottleStage
    {
        public BottleStage(
            InventoryDescriptor source,
            MyPhysicalInventoryItem originalItem,
            InventoryDescriptor filler,
            MyEntity interactedEntity,
            long identityId,
            Func<InventoryDescriptor, InventoryManagementFlags> getFlags,
            MechanicalInventoryScope scope, DistributionPolicy policy)
        {
            Source = source;
            OriginalItem = originalItem;
            Filler = filler;
            InteractedEntity = interactedEntity;
            IdentityId = identityId;
            GetFlags = getFlags;
            Scope = scope;
            Policy = policy;
        }

        public InventoryDescriptor Source { get; }
        public MyPhysicalInventoryItem OriginalItem { get; }
        public InventoryDescriptor Filler { get; }
        public MyDefinitionId DefinitionId => OriginalItem.Content.GetObjectId();
        public MyEntity InteractedEntity { get; }
        public long IdentityId { get; }
        public Func<InventoryDescriptor, InventoryManagementFlags> GetFlags { get; }
        public MechanicalInventoryScope Scope { get; }
        public DistributionPolicy Policy { get; }
        public uint StagedItemId { get; set; }
        public BottleStageState State { get; set; }
        public bool WasFilled { get; set; }
    }
}
