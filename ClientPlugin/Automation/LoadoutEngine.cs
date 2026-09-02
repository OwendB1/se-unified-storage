using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Inventory;
using ClientPlugin.Profiles;
using ClientPlugin.Transfers;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using VRage;
using VRage.Game;

namespace ClientPlugin.Automation;

public static class LoadoutEngine
{
    public static IReadOnlyList<TransferPlan> Plan(
        InventoryProjection projection,
        ScopeProfile profile,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags = null,
        bool maintainedOnly = false)
    {
        if (projection == null)
            throw new ArgumentNullException(nameof(projection));
        if (profile == null)
            throw new ArgumentNullException(nameof(profile));
        getFlags ??= _ => InventoryManagementFlags.None;
        var cargo = projection.Roles.FirstOrDefault(role =>
            role.Section.Kind == InventorySectionKind.UnifiedCargo &&
            role.Role == InventoryRoleKind.GeneralCargo);
        if (cargo == null)
            return Array.Empty<TransferPlan>();
        var result = new List<TransferPlan>();
        foreach (var rule in profile.Loadouts)
        {
            if (maintainedOnly && !rule.Maintain)
                continue;
            if (!MyDefinitionId.TryParse(rule.ItemDefinitionId, out var itemId) || rule.Amount < 0)
                continue;
            var role = projection.Roles.FirstOrDefault(candidate =>
                candidate.Section.Kind == rule.Section && candidate.Role == rule.Role);
            if (role == null)
                continue;
            var members = role.Members.Where(member =>
            {
                var flags = getFlags(member);
                return (flags & (InventoryManagementFlags.ManualBlock |
                                 InventoryManagementFlags.ReservedInventory)) == 0 &&
                       MatchesTarget(member, rule) &&
                       member.Roles.Any(candidate => candidate.Kind == role.Role && candidate.Accepts(itemId)) &&
                       (rule.IncludeNonWorking || member.Owner is not MyFunctionalBlock functional || functional.IsWorking);
            }).ToArray();
            if (members.Length == 0)
                continue;
            var sourceStack = cargo.Stacks.FirstOrDefault(stack => stack.DefinitionId == itemId);
            var cargoSources = sourceStack?.Sources.Where(source => source.Descriptor != null &&
                    (getFlags(source.Descriptor) & (InventoryManagementFlags.ManualBlock |
                                                    InventoryManagementFlags.ReservedInventory)) == 0)
                .ToArray() ?? Array.Empty<InventoryStackReference>();
            var targetAmount = TransferPlanner.Normalize(itemId, (MyFixedPoint)rule.Amount);
            if (rule.PerMember)
            {
                foreach (var member in members)
                {
                    var current = member.Inventory.GetItemAmount(itemId);
                    if (current < targetAmount && cargoSources.Length > 0)
                    {
                        var plan = TransferPlanner.Pair(
                            itemId,
                            targetAmount - current,
                            cargoSources,
                            new[] { new DestinationAllocation(member, targetAmount - current) });
                        if (plan.PlannedAmount > MyFixedPoint.Zero)
                            result.Add(plan);
                    }
                    else if (current > targetAmount)
                        AddExcessPlan(result, member, itemId, current - targetAmount, cargo, profile.Policy, getFlags);
                }
            }
            else
            {
                var current = members.Aggregate(MyFixedPoint.Zero,
                    (sum, member) => sum + member.Inventory.GetItemAmount(itemId));
                if (current < targetAmount && cargoSources.Length > 0)
                {
                    var destinations = TransferPlanFactory.CreateDestinationSnapshots(itemId, members, getFlags);
                    var allocations = TransferPlanner.PlanDestinations(
                        rule.Policy,
                        itemId,
                        targetAmount - current,
                        destinations);
                    var plan = TransferPlanner.Pair(
                        itemId,
                        targetAmount - current,
                        cargoSources,
                        allocations);
                    if (plan.PlannedAmount > MyFixedPoint.Zero)
                        result.Add(plan);
                }
                else if (current > targetAmount)
                {
                    var excess = current - targetAmount;
                    foreach (var member in members.OrderByDescending(candidate => candidate.Inventory.GetItemAmount(itemId)))
                    {
                        if (excess <= MyFixedPoint.Zero)
                            break;
                        var available = MyFixedPoint.Min(excess, member.Inventory.GetItemAmount(itemId));
                        AddExcessPlan(result, member, itemId, available, cargo, profile.Policy, getFlags);
                        excess -= available;
                    }
                }
            }
        }
        return result;
    }

    private static bool MatchesTarget(InventoryDescriptor member, LoadoutRecord rule) =>
        rule.TargetKind switch
        {
            LoadoutTargetKind.Block => member.OwnerEntityId == rule.TargetBlockEntityId,
            LoadoutTargetKind.BlockDefinition =>
                string.Equals(member.BlockDefinitionId.ToString(), rule.TargetBlockDefinitionId,
                    StringComparison.Ordinal),
            _ => true
        };

    private static void AddExcessPlan(
        ICollection<TransferPlan> plans,
        InventoryDescriptor member,
        MyDefinitionId itemId,
        MyFixedPoint amount,
        InventoryRoleProjection cargo,
        DistributionPolicy policy,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags)
    {
        var remaining = amount;
        foreach (var item in member.Inventory.GetItems().Where(candidate =>
                     candidate.Content.GetObjectId() == itemId))
        {
            if (remaining <= MyFixedPoint.Zero)
                break;
            var transferAmount = MyFixedPoint.Min(remaining, item.Amount);
            var plan = TransferPlanFactory.Deposit(
                member.Inventory,
                item,
                transferAmount,
                cargo.Members,
                policy,
                getFlags);
            if (plan.PlannedAmount <= MyFixedPoint.Zero)
                continue;
            plans.Add(plan);
            remaining -= plan.RequestedAmount;
        }
    }
}
