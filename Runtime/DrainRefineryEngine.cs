using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Inventory;
using ClientPlugin.Profiles;
using ClientPlugin.Transfers;
using Sandbox.Game.Entities.Cube;
using VRage.Game;

namespace ClientPlugin.Automation;

public static class DrainRefineryEngine
{
    public static IEnumerable<TransferPlan> Plan(
        InventoryProjection projection,
        ScopeProfile profile,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags)
    {
        var cargo = projection.Roles.Where(role =>
                role.Section.Kind == InventorySectionKind.UnifiedCargo && role.Role == InventoryRoleKind.GeneralCargo)
            .SelectMany(role => role.Members).Distinct().ToArray();
        foreach (var descriptor in projection.Scope.Inventories)
        {
            if (descriptor.Owner is not MyRefinery refinery ||
                !ReferenceEquals(descriptor.Inventory, refinery.OutputInventory))
                continue;
            bool CanDrain() => !refinery.Closed && ReferenceEquals(descriptor.Inventory, refinery.OutputInventory) &&
                (getFlags(descriptor) & (InventoryManagementFlags.ManualBlock | InventoryManagementFlags.ReservedInventory)) == 0;
            if (!CanDrain())
                continue;
            foreach (var item in descriptor.Inventory.GetItems().ToArray())
            {
                if (item.Content.GetObjectId().TypeId != typeof(MyObjectBuilder_Ingot))
                    continue;
                var plan = TransferPlanFactory.Deposit(descriptor.Inventory, item, item.Amount,
                    cargo, profile.Policy, getFlags);
                plan.CanContinue = CanDrain;
                plan.GuardFailureMessage = "refinery output was removed or excluded";
                if (plan.PlannedAmount > 0)
                    yield return plan;
            }
        }
    }
}
