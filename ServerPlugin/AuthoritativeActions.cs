using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Automation;
using ClientPlugin.Inventory;
using ClientPlugin.Profiles;
using ClientPlugin.Transfers;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using Shared.Companion;
using VRage;
using VRage.Game;

namespace ServerPlugin;

internal sealed class AuthoritativeActions
{
    private readonly CompanionConfig config;
    private readonly ProfilePermissions permissions;
    private readonly ScopeProfileStore store;
    private readonly TransferValidation validation;
    private readonly AuthoritativeTransfers transfers;
    private readonly CompanionStats stats;

    public AuthoritativeActions(CompanionConfig config, ProfilePermissions permissions, ScopeProfileStore store,
        AuthoritativeTransfers transfers, CompanionStats stats)
    {
        this.config = config; this.permissions = permissions; this.store = store; this.transfers = transfers; this.stats = stats;
        validation = new TransferValidation(permissions);
    }

    public bool Enabled(ShipAction action) => config.Enabled && store.Available && action switch
    {
        ShipAction.Rebalance => config.Transfers,
        ShipAction.SortRefineries => config.RefineryAutomation,
        ShipAction.QueueComponents => config.ComponentAutomation,
        ShipAction.ApplyLoadouts => config.LoadoutAutomation,
        _ => config.UtilityJobs
    };

    public ActionReceipt Execute(MyCubeGrid anchor, MyTerminalBlock terminal, ulong sender, long identity,
        ShipActionIntent intent, bool maintained = false)
    {
        intent.Validate();
        var result = new ActionReceipt();
        if (!Enabled(intent.Action)) { result.Failure = TransferFailure.PolicyDisabled; return result; }
        bool Access(MyCubeBlock block) => maintained ? ServerInventoryScope.PrincipalAccess(block, identity, config.AutomationFactionAccess) : permissions.HasAccess(block, sender);
        var scope = ServerInventoryScope.Capture(anchor, Access, config.InventoriesPerIntent);
        var shared = store.InScope(new HashSet<long>(scope.Grids.Select(grid => grid.EntityId)));
        if (shared.Length > 1 || maintained && (shared.Length != 1 || shared[0].OwnerIdentityId != identity || !anchor.BigOwners.Contains(identity)))
        { result.Failure = TransferFailure.ScopeChanged; return result; }
        var settings = ProfileCodec.Clone(intent.Settings);
        ServerInventoryScope.Restrict(settings, shared.SingleOrDefault()?.Settings);
        InventoryManagementFlags Flags(InventoryDescriptor member) => ServerInventoryScope.Flags(settings, member);
        var guard = InventoryGroups.Guard(scope, settings, settings.Groups.Select(group => group.Id));
        bool Current() => Enabled(intent.Action) && guard() && (!maintained || anchor.BigOwners.Contains(identity));
        var budget = Math.Max(1, Math.Min(128, maintained ? config.AutomationMutations : config.AllocationsPerIntent));
        try
        {
            if (intent.Action == ShipAction.SortRefineries)
            {
                var model = RefineryPriorityEngine.Build(scope, settings, Flags);
                foreach (var refinery in scope.Inventories.Select(member => member.Owner).OfType<MyRefinery>().Distinct())
                {
                    if (!Current() || !Access(refinery) || RefineryPriorityEngine.IsExcludedFromSorting(refinery, scope, Flags)) continue;
                    var ranks = RefineryPriorityEngine.ForRefinery(model, refinery).Select((id, index) => (id, index))
                        .ToDictionary(pair => pair.id, pair => pair.index);
                    int Rank(MyDefinitionId id) => ranks.TryGetValue(id, out var rank) ? rank : int.MaxValue;
                    var inventory = refinery.InputInventory;
                    for (var index = 0; index < inventory.GetItems().Count; index++)
                    {
                        var items = inventory.GetItems();
                        var best = index;
                        for (var candidate = index + 1; candidate < items.Count; candidate++)
                            if (Rank(items[candidate].Content.GetObjectId()) < Rank(items[best].Content.GetObjectId())) best = candidate;
                        if (best == index) continue;
                        if (result.Mutations >= budget) { result.Failure = TransferFailure.WorkLimit; return result; }
                        if (!Current() || !Access(refinery)) { result.Failure = TransferFailure.AccessDenied; return result; }
                        var before = items[index].ItemId;
                        var moving = items[best];
                        MyInventory.Transfer(inventory, inventory, moving.ItemId, index, moving.Amount, spawn: false);
                        result.Mutations++;
                        stats.RefinerySwaps++;
                        if (inventory.GetItems()[index].ItemId == before)
                        { result.Failure = TransferFailure.StackChanged; return result; }
                    }
                }
            }
            else if (intent.Action == ShipAction.QueueComponents)
            {
                for (var pass = 0; pass < budget && Current(); pass++)
                {
                    // Re-evaluate stock and every queue after each insertion, including blueprint co-products.
                    var statuses = ComponentTargetEngine.Evaluate(scope, settings, Flags);
                    if (maintained && !statuses.Any(status => status.Target > 0 &&
                        (decimal)(status.Stock + status.Queued) < (decimal)status.Target * settings.ComponentStartThreshold)) break;
                    var requests = ComponentTargetEngine.PlanDeficits(statuses).Where(request =>
                        Access(request.Assembler) && request.Assembler.Queue.Count() < MySession.Static.MaxProductionQueueLength).ToArray();
                    if (requests.Length == 0) break;
                    var request = requests[0];
                    var runs = MyFixedPoint.Min(MyFixedPoint.Floor(request.Runs), (MyFixedPoint)1000000);
                    if (runs <= 0) break;
                    var before = request.Assembler.Queue.Where(item => item.Blueprint == request.Blueprint).Aggregate(MyFixedPoint.Zero, (sum, item) => sum + item.Amount);
                    request.Assembler.AddQueueItemRequest(request.Blueprint, runs);
                    result.Mutations++;
                    stats.QueueAdditions++;
                    var after = request.Assembler.Queue.Where(item => item.Blueprint == request.Blueprint).Aggregate(MyFixedPoint.Zero, (sum, item) => sum + item.Amount);
                    if (after <= before) { result.Failure = TransferFailure.StackChanged; break; }
                }
            }
            else
            {
                var projection = new InventoryProjectionBuilder().Build(scope);
                var plans = new List<TransferPlan>();
                switch (intent.Action)
                {
                    case ShipAction.ApplyLoadouts:
                        if (settings.Loadouts.Any(rule => !transfers.PolicyEnabled(rule.Policy)))
                        { result.Failure = TransferFailure.PolicyDisabled; return result; }
                        plans.AddRange(LoadoutEngine.Plan(projection, settings, Flags, maintainedOnly: maintained, groupId: intent.GroupId));
                        break;
                    case ShipAction.DrainAssemblers:
                        foreach (var operation in DrainAssemblerEngine.Plan(projection, settings, Flags))
                        {
                            var previous = operation.Plan.CanContinue;
                            operation.Plan.CanContinue = () => operation.CanContinue && (previous == null || previous());
                            plans.Add(operation.Plan);
                        }
                        break;
                    case ShipAction.Rebalance:
                        if (!transfers.PolicyEnabled(settings.Policy)) { result.Failure = TransferFailure.PolicyDisabled; return result; }
                        foreach (var selection in intent.Selections)
                        {
                            if (selection.AnchorId != anchor.EntityId) { result.Failure = TransferFailure.ScopeChanged; return result; }
                            // Build physical membership per item through the same live selection resolver used by transfers.
                            var grouped = InventoryGroups.Build(projection, new ScopeProfile { GroupSchemaVersion = 1, Groups = new() { selection.Group } });
                            foreach (var role in grouped.Roles.Where(role => role.Role == selection.Role &&
                                (selection.InventoryIndex < 0 || role.Section.InventoryIndex == selection.InventoryIndex)))
                            foreach (var item in role.Stacks.Select(stack => stack.DefinitionId).Distinct())
                            {
                                var guards = new List<Func<bool>>();
                                var members = transfers.Select(selection, terminal, sender, item, guards);
                                var selectedScope = new MechanicalInventoryScope(anchor, anchor, scope.Grids, members);
                                var selectedProjection = InventoryGroups.Build(new InventoryProjectionBuilder().Build(selectedScope),
                                    new ScopeProfile { GroupSchemaVersion = 1, Groups = new() { selection.Group } });
                                foreach (var selectedRole in selectedProjection.Roles.Where(row => row.Role == selection.Role))
                                foreach (var plan in TransferPlanFactory.Rebalance(selectedRole, settings.Policy, Flags).Where(plan => plan.ItemId == item))
                                {
                                    plan.CanContinue = () => guards.All(check => check());
                                    plans.Add(plan);
                                }
                            }
                        }
                        break;
                }
                var pairs = 0;
                foreach (var plan in plans)
                foreach (var allocation in plan.Allocations)
                {
                    if (result.Mutations >= budget || ++pairs > Math.Max(1, Math.Min(128, config.TransferPairsPerIntent)))
                    { result.Failure = TransferFailure.WorkLimit; return result; }
                    if (!Current() || plan.CanContinue?.Invoke() == false)
                    { result.Failure = TransferFailure.ScopeChanged; return result; }
                    var source = scope.Inventories.FirstOrDefault(member => ReferenceEquals(member.Inventory, allocation.Source.Inventory));
                    var destination = scope.Inventories.FirstOrDefault(member => ReferenceEquals(member.Inventory, allocation.DestinationInventory));
                    if (source == null || destination == null || !Access(source.Owner) || !Access(destination.Owner) ||
                        ServerInventoryScope.Excluded(settings, source) || ServerInventoryScope.Excluded(settings, destination, true))
                    { result.Failure = TransferFailure.Excluded; continue; }
                    var live = source.Inventory.GetItemByID(allocation.Source.ItemId);
                    if (!live.HasValue || live.Value.Content.GetObjectId() != plan.ItemId)
                    { result.Failure = TransferFailure.StackChanged; continue; }
                    var amount = TransferPlanner.Normalize(plan.ItemId, MyFixedPoint.Min(allocation.Amount,
                        MyFixedPoint.Min(live.Value.Amount, destination.Inventory.ComputeAmountThatFits(plan.ItemId))));
                    if (plan.LimitAmount != null) amount = MyFixedPoint.Min(amount, plan.LimitAmount(allocation));
                    if (amount <= 0 || !destination.Inventory.CanItemsBeAdded(amount, plan.ItemId))
                    { result.Failure = TransferFailure.DestinationFull; continue; }
                    var failure = TransferFailure.NoConveyorPath;
                    var allowed = maintained ? OfflinePath(source, destination, identity, plan.ItemId, config.AutomationFactionAccess) :
                        validation.CanTransfer(source.Inventory, destination.Inventory, terminal, sender, identity, plan.ItemId, out failure);
                    if (!allowed) { result.Failure = failure; continue; }
                    var moved = MyInventory.Transfer(source.Inventory, destination.Inventory, live.Value.ItemId, -1, amount, spawn: false);
                    result.Mutations++; stats.TransferAllocations++;
                    result.MovedRaw = checked(result.MovedRaw + Math.Max(0, Math.Min(amount.RawValue, moved.RawValue)));
                }
            }
        }
        catch (Exception)
        {
            // Native event handlers may throw after a mutation. The request journal must prevent replay.
            result.Failure = TransferFailure.UnknownOutcome;
        }
        return result;
    }

    internal static bool OfflinePath(InventoryDescriptor source, InventoryDescriptor destination, long identity, MyDefinitionId item, bool faction)
    {
        if (!ServerInventoryScope.PrincipalAccess(source.Owner, identity, faction) || !ServerInventoryScope.PrincipalAccess(destination.Owner, identity, faction) ||
            ReferenceEquals(source.Inventory, destination.Inventory)) return false;
        if (source.Owner.CubeGrid.IsInSameLogicalGroupAs(destination.Owner.CubeGrid) &&
            (source.Owner.PositionComp.GetPosition() - destination.Owner.PositionComp.GetPosition()).LengthSquared() > 4000000d) return false;
        if (ReferenceEquals(source.Owner, destination.Owner)) return true;
        var from = TransferValidation.Endpoint(source.Owner);
        var to = TransferValidation.Endpoint(destination.Owner);
        return from != null && to != null && MyGridConveyorSystem.ComputeCanTransfer(from, to, item) &&
            MyGridConveyorSystem.Reachable(from, to, identity, item) &&
            MyGridConveyorSystem.Reachable(from.ConveyorEndpoint, to.ConveyorEndpoint);
    }
}
