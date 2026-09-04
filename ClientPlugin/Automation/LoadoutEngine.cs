using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Inventory;
using ClientPlugin.Profiles;
using ClientPlugin.Transfers;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using VRage;
using VRage.Game;

namespace ClientPlugin.Automation;

public static class LoadoutEngine
{
    public static IReadOnlyList<InventoryDescriptor> Targets(MechanicalInventoryScope scope, ScopeProfile profile,
        LoadoutRecord rule, Func<InventoryDescriptor, InventoryManagementFlags> flags, out string error,
        bool includeUnavailable = false)
    {
        var group = profile.Groups.FirstOrDefault(g => g.Id == rule.GroupId);
        var members = InventoryGroups.Resolve(scope, group, out error);
        if (error != null) return members;
        if (!MyDefinitionId.TryParse(rule.ItemDefinitionId, out var item) || rule.Amount < 0 || rule.Amount > (decimal)MyFixedPoint.MaxValue)
        {
            error = "Invalid item / amount";
            return Array.Empty<InventoryDescriptor>();
        }
        if (!InventoryGroups.Accepts(group, item))
        {
            error = "Item excluded by group";
            return Array.Empty<InventoryDescriptor>();
        }
        return members.Where(member => (includeUnavailable || Allowed(member, flags)) &&
            member.Roles.Any(role => role.Kind == rule.Role && role.Accepts(item)) &&
            (group.AllRoles || group.Role == rule.Role) &&
            (includeUnavailable || rule.IncludeNonWorking || member.Owner is not MyFunctionalBlock functional || functional.IsWorking) &&
            (rule.TargetKind switch
            {
                LoadoutTargetKind.Block => member.OwnerEntityId == rule.TargetBlockEntityId,
                LoadoutTargetKind.BlockDefinition => member.BlockDefinitionId.ToString() == rule.TargetBlockDefinitionId,
                _ => true
            })).ToArray();
    }

    public static string Status(MechanicalInventoryScope scope, ScopeProfile profile, LoadoutRecord rule,
        Func<InventoryDescriptor, InventoryManagementFlags> flags)
    {
        var members = Targets(scope, profile, rule, flags, out var error);
        if (error != null) return error;
        if (members.Count == 0) return "No eligible members";
        foreach (var id in new[] { rule.SupplyGroupId, rule.ReturnGroupId }.Where(id => !string.IsNullOrEmpty(id)))
        {
            InventoryGroups.Resolve(scope, profile.Groups.FirstOrDefault(g => g.Id == id), out error);
            if (error != null) return error;
        }
        if (profile.Loadouts.Where(other => !ReferenceEquals(other, rule) && other.ItemDefinitionId == rule.ItemDefinitionId)
            .Any(other => Targets(scope, profile, other, flags, out _)
                .Any(member => members.Any(target => target.Inventory == member.Inventory))))
            return "Conflict: overlapping targets";
        var item = MyDefinitionId.Parse(rule.ItemDefinitionId);
        var target = TransferPlanner.Normalize(item, (MyFixedPoint)rule.Amount);
        var current = members.Aggregate(MyFixedPoint.Zero, (sum, member) => sum + member.Inventory.GetItemAmount(item));
        if (rule.PerMember ? members.Any(member => member.Inventory.GetItemAmount(item) < target) : current < target)
            return "Needs supply";
        if (rule.PerMember ? members.Any(member => member.Inventory.GetItemAmount(item) > target) : current > target)
            return "Has excess";
        return "On target";
    }

    public static IReadOnlyList<TransferPlan> Plan(InventoryProjection projection, ScopeProfile profile,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags = null, bool maintainedOnly = false,
        string groupId = null)
    {
        getFlags ??= _ => InventoryManagementFlags.None;
        InventoryGroupRecord.Migrate(profile);
        var scope = projection.Scope;
        var result = new List<TransferPlan>();
        var used = new Dictionary<(MyInventory, uint), MyFixedPoint>();
        foreach (var rule in profile.Loadouts.Where(rule => (!maintainedOnly || rule.Maintain) &&
                     (groupId == null || rule.GroupId == groupId)))
        {
            var status = Status(scope, profile, rule, getFlags);
            if (status != "Needs supply" && status != "Has excess") continue;
            var members = Targets(scope, profile, rule, getFlags, out _);
            var item = MyDefinitionId.Parse(rule.ItemDefinitionId);
            var amount = TransferPlanner.Normalize(item, (MyFixedPoint)rule.Amount);
            // A rule must never borrow another loadout's target stock or return excess into it.
            IEnumerable<MyInventory> ProtectedTargets() => profile.Loadouts
                .Where(other => other.ItemDefinitionId == rule.ItemDefinitionId)
                .SelectMany(other => Targets(scope, profile, other, getFlags, out _, includeUnavailable: true)).Select(member => member.Inventory);
            var protectedTargets = new HashSet<MyInventory>(ProtectedTargets());
            InventoryDescriptor[] Storage(string id)
            {
                if (string.IsNullOrEmpty(id)) return Array.Empty<InventoryDescriptor>();
                var group = profile.Groups.FirstOrDefault(g => g.Id == id);
                return InventoryGroups.Resolve(scope, group, out _).Where(member => Allowed(member, getFlags) &&
                    !protectedTargets.Contains(member.Inventory) && InventoryGroups.Accepts(group, item) &&
                    member.Roles.Any(role => (group.AllRoles || role.Kind == group.Role) && role.Accepts(item))).ToArray();
            }
            var deficits = new List<DestinationAllocation>();
            var surplus = new List<InventoryStackReference>();
            var currentTotal = members.Aggregate(MyFixedPoint.Zero, (sum, member) => sum + member.Inventory.GetItemAmount(item));
            var remainingExcess = MyFixedPoint.Max(currentTotal - amount, MyFixedPoint.Zero);
            foreach (var member in members)
            {
                var current = member.Inventory.GetItemAmount(item);
                if (rule.PerMember && current < amount)
                    deficits.Add(new DestinationAllocation(member, MyFixedPoint.Min(amount - current, member.Inventory.ComputeAmountThatFits(item))));
                var excess = rule.PerMember ? MyFixedPoint.Max(current - amount, MyFixedPoint.Zero) : MyFixedPoint.Min(current, remainingExcess);
                if (!rule.PerMember) remainingExcess -= excess;
                surplus.AddRange(Sources(new[] { member }, excess));
            }
            if (!rule.PerMember && currentTotal < amount)
                deficits.AddRange(TransferPlanner.PlanDestinations(rule.Policy, item, amount - currentTotal,
                    TransferPlanFactory.CreateDestinationSnapshots(item, members, getFlags)));
            var guard = InventoryGroups.Guard(scope, profile, new[] { rule.GroupId, rule.SupplyGroupId, rule.ReturnGroupId });
            var snapshot = Signature(rule);
            string RelatedRules() => string.Join("\n", profile.Loadouts.Where(other => other.ItemDefinitionId == rule.ItemDefinitionId).Select(Signature));
            var related = RelatedRules();
            var targetInventories = new HashSet<MyInventory>(members.Select(member => member.Inventory));
            bool CanContinue() => profile.Loadouts.Contains(rule) && (!maintainedOnly || rule.Maintain) &&
                Signature(rule) == snapshot && RelatedRules() == related && guard() &&
                protectedTargets.SetEquals(ProtectedTargets()) &&
                targetInventories.SetEquals(Targets(scope, profile, rule, getFlags, out _).Select(member => member.Inventory)) &&
                Status(scope, profile, rule, getFlags) != "Conflict: overlapping targets";
            Add(TransferPlanner.Pair(item, deficits.Aggregate(MyFixedPoint.Zero, (sum, d) => sum + d.Amount),
                Sources(Storage(rule.SupplyGroupId), MyFixedPoint.MaxValue), deficits));
            var excessTotal = surplus.Aggregate(MyFixedPoint.Zero, (sum, s) => sum + s.SnapshotAmount);
            Add(TransferPlanner.Pair(item, excessTotal, surplus,
                TransferPlanner.PlanDestinations(rule.Policy, item, excessTotal,
                    TransferPlanFactory.CreateDestinationSnapshots(item, Storage(rule.ReturnGroupId), getFlags))));

            IReadOnlyList<InventoryStackReference> Sources(IEnumerable<InventoryDescriptor> from, MyFixedPoint maximum)
            {
                var sources = new List<InventoryStackReference>();
                foreach (var member in from)
                foreach (var stack in member.Inventory.GetItems().Where(stack => stack.Content.GetObjectId() == item))
                {
                    used.TryGetValue((member.Inventory, stack.ItemId), out var reserved);
                    var available = MyFixedPoint.Min(maximum, MyFixedPoint.Max(stack.Amount - reserved, MyFixedPoint.Zero));
                    if (available <= MyFixedPoint.Zero) continue;
                    var adjusted = stack;
                    adjusted.Amount = available;
                    sources.Add(new InventoryStackReference(member, adjusted));
                    maximum -= available;
                }
                return sources;
            }
            void Add(TransferPlan plan)
            {
                if (plan.PlannedAmount <= MyFixedPoint.Zero) return;
                plan.CanContinue = CanContinue;
                plan.LimitAmount = allocation =>
                {
                    var depositing = targetInventories.Contains(allocation.DestinationInventory);
                    var current = rule.PerMember
                        ? (depositing ? allocation.DestinationInventory : allocation.Source.Inventory).GetItemAmount(item)
                        : members.Aggregate(MyFixedPoint.Zero, (sum, member) => sum + member.Inventory.GetItemAmount(item));
                    return MyFixedPoint.Max(depositing ? amount - current : current - amount, MyFixedPoint.Zero);
                };
                plan.GuardFailureMessage = "loadout group, membership or rule changed; reapply using current members";
                foreach (var allocation in plan.Allocations)
                {
                    var key = (allocation.Source.Inventory, allocation.Source.ItemId);
                    used.TryGetValue(key, out var previous);
                    used[key] = previous + allocation.Amount;
                }
                result.Add(plan);
            }
        }
        return result;
    }

    private static string Signature(LoadoutRecord rule) => string.Join("|", rule.GroupId, rule.SupplyGroupId,
        rule.ReturnGroupId, rule.Role, rule.TargetKind, rule.TargetBlockEntityId, rule.TargetBlockDefinitionId,
        rule.ItemDefinitionId, rule.Amount, rule.PerMember, rule.IncludeNonWorking, rule.Policy);

    private static bool Allowed(InventoryDescriptor member, Func<InventoryDescriptor, InventoryManagementFlags> flags) =>
        (flags(member) & (InventoryManagementFlags.ManualBlock | InventoryManagementFlags.ReservedInventory)) == 0;
}
