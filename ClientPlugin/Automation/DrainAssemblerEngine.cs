using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Inventory;
using ClientPlugin.Profiles;
using ClientPlugin.Transfers;
using Sandbox.Game.Entities.Cube;
using VRage.Game;

namespace ClientPlugin.Automation;

public sealed class DrainAssemblerOperation
{
    public DrainAssemblerOperation(MyAssembler assembler, TransferPlan plan)
    {
        Assembler = assembler;
        Plan = plan;
    }

    public MyAssembler Assembler { get; }
    public TransferPlan Plan { get; }
    public bool CanContinue => IsIdleAssembly(Assembler);

    internal static bool IsIdleAssembly(MyAssembler assembler) =>
        assembler != null && !assembler.Closed && !assembler.DisassembleEnabled &&
        assembler.IsQueueEmpty && !assembler.IsProducing;
}

public static class DrainAssemblerEngine
{
    public static IReadOnlyList<DrainAssemblerOperation> Plan(
        InventoryProjection projection,
        ScopeProfile profile,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags = null)
    {
        getFlags ??= _ => InventoryManagementFlags.None;
        var cargo = projection.Roles.FirstOrDefault(role =>
            role.Section.Kind == InventorySectionKind.UnifiedCargo &&
            role.Role == InventoryRoleKind.GeneralCargo);
        if (cargo == null)
            return Array.Empty<DrainAssemblerOperation>();
        var plans = new List<DrainAssemblerOperation>();
        var assemblers = projection.Scope.Inventories.Select(descriptor => descriptor.Owner)
            .OfType<MyAssembler>().Distinct().Where(DrainAssemblerOperation.IsIdleAssembly)
            .ToArray();
        foreach (var assembler in assemblers)
        foreach (var descriptor in projection.Scope.Inventories.Where(descriptor =>
                     ReferenceEquals(descriptor.Owner, assembler) &&
                     (getFlags(descriptor) & (InventoryManagementFlags.ManualBlock |
                                              InventoryManagementFlags.ReservedInventory)) == 0))
        foreach (var item in descriptor.Inventory.GetItems().ToArray())
        {
            if (!DrainAssemblerOperation.IsIdleAssembly(assembler))
                break;
            var plan = TransferPlanFactory.Deposit(
                descriptor.Inventory,
                item,
                item.Amount,
                cargo.Members,
                profile.Policy,
                getFlags);
            if (plan.PlannedAmount > 0)
                plans.Add(new DrainAssemblerOperation(assembler, plan));
        }
        return plans;
    }
}
