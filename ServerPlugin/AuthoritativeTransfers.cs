using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClientPlugin;
using ClientPlugin.Inventory;
using ClientPlugin.Profiles;
using ClientPlugin.Transfers;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using Shared.Companion;
using VRage;
using VRage.Game;
using VRage.Game.Entity;

namespace ServerPlugin;

internal sealed class AuthoritativeTransfers
{
    private readonly CompanionConfig config;
    private readonly ProfilePermissions permissions;
    private readonly TransferValidation validation;
    private readonly ScopeProfileStore store;
    private readonly CompanionStats stats;

    public AuthoritativeTransfers(CompanionConfig config, ProfilePermissions permissions, ScopeProfileStore store, CompanionStats stats)
    {
        this.config = config; this.permissions = permissions; this.store = store; this.stats = stats;
        validation = new TransferValidation(permissions);
    }

    public TransferReceipt Execute(ulong sender, long identity, MyTerminalBlock terminal, TransferIntent intent)
    {
        intent.Validate();
        var receipt = new TransferReceipt { RequestedRaw = intent.AmountRaw };
        if (intent.Destination != null && !PolicyEnabled(intent.Policy)) { receipt.Failure = TransferFailure.PolicyDisabled; return receipt; }
        if (!MyDefinitionId.TryParse(intent.ItemDefinition, out var itemId)) throw new InvalidDataException("Invalid item definition.");
        var requested = Raw(intent.AmountRaw);
        if (TransferPlanner.Normalize(itemId, requested) != requested) throw new InvalidDataException("Non-integral item quantity.");
        var seed = TransferValidation.Resolve(intent.Seed);
        if (!validation.HasAccess(seed, sender, identity)) { receipt.Failure = TransferFailure.AccessDenied; return receipt; }
        var seedItem = seed.GetItemByID(intent.SeedItemId);
        if (!seedItem.HasValue || seedItem.Value.Content.GetObjectId() != itemId)
        { receipt.Failure = TransferFailure.StackChanged; return receipt; }
        var prototype = (MyObjectBuilder_PhysicalObject)seedItem.Value.Content.Clone();
        var guards = new List<Func<bool>>();
        InventoryDescriptor[] sourceMembers;
        InventoryDescriptor[] destinations;
        try
        {
            sourceMembers = Select(intent.Source, terminal, sender, itemId, guards);
            destinations = Select(intent.Destination, terminal, sender, itemId, guards);
        }
        catch (SelectionRejected exception) { receipt.Failure = exception.Failure; return receipt; }
        var concrete = intent.Destination == null ? TransferValidation.Resolve(intent.ConcreteDestination) : null;
        if (intent.Destination == null && !validation.HasAccess(concrete, sender, identity))
        { receipt.Failure = TransferFailure.AccessDenied; return receipt; }
        if (intent.Source != null && !sourceMembers.Any(member => ReferenceEquals(member.Inventory, seed)))
        { receipt.Failure = TransferFailure.ScopeChanged; return receipt; }
        var sourceInventories = intent.Source == null ? new[] { seed } : sourceMembers.Select(m => m.Inventory).ToArray();
        // Stop before mutation if the selected stock cannot be scanned within a bounded budget.
        var sources = sourceInventories.SelectMany(inventory => inventory.GetItems().Select(item => (inventory, item)))
            .Take(8193).ToArray();
        if (sources.Length > 8192) { receipt.Failure = TransferFailure.WorkLimit; return receipt; }
        var local = new ScopeProfile { InventoryManagement = intent.Exclusions };
        var pairs = 0;
        var maxPairs = Math.Max(1, Math.Min(128, config.TransferPairsPerIntent));
        var maxAllocations = Math.Max(1, Math.Min(128, config.AllocationsPerIntent));
        bool Matches(MyInventory inventory, MyPhysicalInventoryItem item) => item.Content.GetObjectId() == itemId &&
            (ReferenceEquals(inventory, seed) && item.ItemId == intent.SeedItemId ||
             prototype.CanStack(item.Content) && item.Content.CanStack(prototype));
        InventoryManagementFlags Flags(MyInventory inventory)
        {
            var index = Index(inventory);
            InventoryManagementFlags FromProfile(ScopeProfile profile) => profile.GetFlags(inventory.Owner.EntityId, index) |
                (profile.InventoryManagement.Any(record => record.BlockEntityId == inventory.Owner.EntityId &&
                    (record.Flags & InventoryManagementFlags.ManualBlock) != 0) ? InventoryManagementFlags.ManualBlock : InventoryManagementFlags.None);
            var value = FromProfile(local);
            if (inventory.Owner is MyCubeBlock block)
            {
                var grids = MyCubeGridGroups.Static.Mechanical.GetGroupNodes(block.CubeGrid) ?? new List<MyCubeGrid> { block.CubeGrid };
                foreach (var profile in store.InScope(new HashSet<long>(grids.Select(g => g.EntityId))))
                    value |= FromProfile(profile.Settings);
            }
            return value;
        }
        bool Excluded(MyInventory inventory, bool destination) =>
            (Flags(inventory) & (InventoryManagementFlags.ManualBlock | InventoryManagementFlags.ReservedInventory)) != 0 ||
            destination && inventory.Owner is MyCubeBlock block &&
            InventoryDescriptorFactory.Create(block, Index(inventory), inventory).Section.Kind == InventorySectionKind.UnifiedCargo &&
            (Flags(inventory) & InventoryManagementFlags.NoUnifiedCargoDestination) != 0;

        try
        {
            foreach (var source in sources.OrderByDescending(s => s.item.Amount.RawValue)
                         .ThenBy(s => s.inventory.Owner.EntityId).ThenBy(s => s.item.ItemId))
            {
                if (receipt.MovedRaw >= intent.AmountRaw) break;
                if (intent.Source == null && source.item.ItemId != intent.SeedItemId || !Matches(source.inventory, source.item)) continue;
                if (Excluded(source.inventory, false)) { receipt.Failure = TransferFailure.Excluded; continue; }
                var choices = new List<(MyInventory Inventory, MyFixedPoint Amount)>();
                if (concrete != null) choices.Add((concrete, requested - Raw(receipt.MovedRaw)));
                else
                {
                    var reachable = new List<DestinationSnapshot>();
                    foreach (var member in destinations)
                    {
                        if (ReferenceEquals(source.inventory, member.Inventory) || Excluded(member.Inventory, true)) continue;
                        // Keep part of the budget for immediately-before-mutation validation.
                        if (pairs >= Math.Max(1, maxPairs / 2)) { receipt.Failure = TransferFailure.WorkLimit; break; }
                        pairs++;
                        if (!validation.CanTransfer(source.inventory, member.Inventory, terminal, sender, identity, itemId, out var failure))
                        { receipt.Failure = failure; continue; }
                        reachable.Add(new DestinationSnapshot(member, member.Inventory.GetItemAmount(itemId),
                            member.Inventory.ComputeAmountThatFits(itemId)));
                    }
                    choices.AddRange(TransferPlanner.PlanDestinations(intent.Policy, itemId,
                        MyFixedPoint.Min(requested - Raw(receipt.MovedRaw), source.item.Amount), reachable)
                        .Select(allocation => (allocation.Destination.Inventory, allocation.Amount)));
                    if (reachable.Count > 0 && choices.Count == 0) receipt.Failure = TransferFailure.DestinationFull;
                }
                foreach (var choice in choices)
                {
                    if (receipt.MovedRaw >= intent.AmountRaw) break;
                    if (receipt.Allocations >= maxAllocations || ++pairs > maxPairs)
                    { receipt.Failure = TransferFailure.WorkLimit; return receipt; }
                    if (!config.Enabled || !config.Transfers || intent.Destination != null && !PolicyEnabled(intent.Policy) || guards.Any(guard => !guard()))
                    { receipt.Failure = TransferFailure.ScopeChanged; return receipt; }
                    var destination = choice.Inventory;
                    if (Excluded(source.inventory, false) || Excluded(destination, true))
                    { receipt.Failure = TransferFailure.Excluded; continue; }
                    var live = source.inventory.GetItemByID(source.item.ItemId);
                    if (!live.HasValue || !Matches(source.inventory, live.Value))
                    { receipt.Failure = TransferFailure.StackChanged; continue; }
                    if (!destination.CheckConstraint(itemId)) { receipt.Failure = TransferFailure.Constraint; continue; }
                    var amount = TransferPlanner.Normalize(itemId, MyFixedPoint.Min(choice.Amount,
                        MyFixedPoint.Min(requested - Raw(receipt.MovedRaw), MyFixedPoint.Min(live.Value.Amount,
                            destination.ComputeAmountThatFits(itemId)))));
                    if (amount <= MyFixedPoint.Zero || !destination.CanItemsBeAdded(amount, itemId))
                    { receipt.Failure = TransferFailure.DestinationFull; continue; }
                    if (!validation.CanTransfer(source.inventory, destination, terminal, sender, identity, itemId, out var failure))
                    { receipt.Failure = failure; continue; }
                    // Native return value is the authoritative removed amount, not a replicated observation.
                    var moved = MyInventory.Transfer(source.inventory, destination, live.Value.ItemId, -1, amount, spawn: false);
                    receipt.Allocations++; stats.TransferAllocations++;
                    receipt.MovedRaw = checked(receipt.MovedRaw + Math.Max(0, Math.Min(amount.RawValue, moved.RawValue)));
                    if (moved <= MyFixedPoint.Zero) receipt.Failure = TransferFailure.DestinationFull;
                }
                if (pairs >= maxPairs) { receipt.Failure = TransferFailure.WorkLimit; break; }
            }
        }
        catch (Exception)
        {
            // An inventory event subscriber may throw after mutation. Never describe that as a safe rejection/retry.
            receipt.Failure = TransferFailure.UnknownOutcome;
            return receipt;
        }
        if (receipt.MovedRaw >= intent.AmountRaw) receipt.Failure = TransferFailure.None;
        else if (receipt.Failure == TransferFailure.None) receipt.Failure = TransferFailure.InsufficientStock;
        return receipt;
    }

    internal InventoryDescriptor[] Select(InventorySelection selection, MyTerminalBlock terminal, ulong sender,
        MyDefinitionId item, List<Func<bool>> guards)
    {
        if (selection == null) return Array.Empty<InventoryDescriptor>();
        if (!MyEntities.TryGetEntityById(selection.AnchorId, out var entity) || entity is not MyCubeGrid anchor || anchor.MarkedForClose)
            throw new SelectionRejected(TransferFailure.ScopeChanged);
        var grids = MyCubeGridGroups.Static.Mechanical.GetGroupNodes(anchor) ?? new List<MyCubeGrid> { anchor };
        if (grids.Count > 128) throw new SelectionRejected(TransferFailure.WorkLimit);
        var scopeIds = new HashSet<long>(grids.Select(g => g.EntityId));
        if (store.InScope(scopeIds).Length > 1) throw new SelectionRejected(TransferFailure.ScopeChanged);
        var blocks = grids.SelectMany(grid => grid.GetFatBlocks()).Take(8193).ToArray();
        if (blocks.Length > 8192) throw new SelectionRejected(TransferFailure.WorkLimit);
        var members = new List<InventoryDescriptor>();
        foreach (var block in blocks.Where(block => block.HasInventory && permissions.HasAccess(block, sender)))
        for (var index = 0; index < block.InventoryCount; index++)
        {
            if (block.GetInventoryBase(index) is MyInventory inventory)
                members.Add(InventoryDescriptorFactory.Create(block, index, inventory));
            if (members.Count > Math.Max(1, Math.Min(1024, config.InventoriesPerIntent)))
                throw new SelectionRejected(TransferFailure.WorkLimit);
        }
        var scope = new MechanicalInventoryScope(terminal, anchor, grids.ToArray(), members);
        var profile = new ScopeProfile { GroupSchemaVersion = 1, Groups = new List<InventoryGroupRecord> { selection.Group } };
        guards.Add(InventoryGroups.Guard(scope, profile, new[] { selection.Group.Id }));
        var selected = InventoryGroups.Resolve(scope, selection.Group, out var error, item, selection.Role);
        if (error != null) throw new SelectionRejected(TransferFailure.ScopeChanged);
        HashSet<long> named = null;
        if (!string.IsNullOrEmpty(selection.TerminalGroup) &&
            !InventoryGroups.NamedGroups(scope).TryGetValue(selection.TerminalGroup, out named))
            throw new SelectionRejected(TransferFailure.ScopeChanged);
        if (named != null)
        {
            var viewGroup = new InventoryGroupRecord
            { Id = "view", Selector = InventoryGroupSelector.TerminalGroup, Value = selection.TerminalGroup };
            guards.Add(InventoryGroups.Guard(scope, new ScopeProfile
                { GroupSchemaVersion = 1, Groups = new List<InventoryGroupRecord> { viewGroup } }, new[] { viewGroup.Id }));
        }
        HashSet<long> network = null;
        if (selection.NetworkRootId != 0)
        {
            var root = members.FirstOrDefault(member => member.OwnerEntityId == selection.NetworkRootId);
            var endpoint = root == null ? null : TransferValidation.Endpoint(root.Owner);
            if (endpoint?.ConveyorEndpoint?.GetLineCount() <= 0 || endpoint == null)
                throw new SelectionRejected(TransferFailure.ScopeChanged);
            try
            {
                network = ConveyorNetworkResolver.Find(scope, Math.Max(2, Math.Min(128, config.TransferPairsPerIntent)))
                    .FirstOrDefault(component => component.Contains(selection.NetworkRootId));
            }
            catch (InvalidOperationException) { throw new SelectionRejected(TransferFailure.WorkLimit); }
            if (network == null) throw new SelectionRejected(TransferFailure.ScopeChanged);
            guards.Add(() => !root.Owner.MarkedForClose);
        }
        return selected.Where(member => (named == null || named.Contains(member.OwnerEntityId)) &&
            (network == null || network.Contains(member.OwnerEntityId)) &&
            (string.IsNullOrEmpty(selection.BlockDefinition) || member.BlockDefinitionId.ToString() == selection.BlockDefinition) &&
            (selection.InventoryIndex < 0 || member.InventoryIndex == selection.InventoryIndex))
            .OrderBy(member => member.OwnerEntityId).ThenBy(member => member.InventoryIndex).ToArray();
    }

    internal bool PolicyEnabled(DistributionPolicy policy) => policy switch
    {
        DistributionPolicy.ExistingStackFirst => config.ExistingStackFirst,
        DistributionPolicy.FillFirst => config.FillFirst,
        DistributionPolicy.EvenByItem => config.EvenByItem,
        _ => false
    };
    private static int Index(MyInventory inventory)
    {
        for (var i = 0; i < inventory.Owner.InventoryCount; i++)
            if (ReferenceEquals(inventory.Owner.GetInventoryBase(i), inventory)) return i;
        return -1;
    }
    internal static MyFixedPoint Raw(long value) => new() { RawValue = value };

    private sealed class SelectionRejected : Exception
    {
        public SelectionRejected(TransferFailure failure) => Failure = failure;
        public TransferFailure Failure { get; }
    }
}
