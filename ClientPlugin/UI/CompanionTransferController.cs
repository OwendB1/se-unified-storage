using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Inventory;
using ClientPlugin.Profiles;
using ClientPlugin.Transfers;
using Sandbox.Game;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI;
using Shared.Companion;
using VRage;
using VRage.Game.Entity;

namespace ClientPlugin.UI;

internal sealed partial class UnifiedTerminalController
{
    private bool TryCompanionTransfer(ProjectedInventoryStack projected, ProjectedGridContext source,
        MyInventory realSource, MyPhysicalInventoryItem realItem, MyInventory realDestination,
        ProjectedGridContext destination, MyFixedPoint amount)
    {
        var client = Plugin.Instance.Companion;
        if (client?.Supports(CompanionCapabilities.Transfers) != true) return false;
        void Notify(string text) => MyAPIGateway.Utilities?.ShowNotification("Unified Storage: " + text, 5000);
        if (client.Busy || Plugin.Instance.Transfers.PendingCount != 0)
        { Notify("Another operation is pending. Wait for its result."); return true; }
        try
        {
            if (interacted is not MyTerminalBlock terminal)
            { Notify("Open an accessible ship terminal before requesting a companion transfer."); return true; }
            var reference = projected?.Sources.FirstOrDefault();
            var seedInventory = projected != null ? reference.Value.Inventory : realSource;
            var seedId = projected != null ? reference.Value.ItemId : realItem.ItemId;
            var definition = projected?.DefinitionId ?? realItem.Content.GetObjectId();
            var contexts = new[] { source, destination }.Where(context => context != null).ToArray();
            var intent = new TransferIntent
            {
                Source = Selection(source), Destination = Selection(destination), Seed = Address(seedInventory), SeedItemId = seedId,
                ConcreteDestination = realDestination == null ? null : Address(realDestination),
                ItemDefinition = definition.ToString(), AmountRaw = TransferPlanner.Normalize(definition, amount).RawValue,
                Policy = destination == null ? DistributionPolicy.ExistingStackFirst : profiles[destination.Owner.Session].Policy,
                Exclusions = contexts.SelectMany(context => profiles[context.Owner.Session].InventoryManagement)
                    .GroupBy(record => (record.BlockEntityId, record.InventoryIndex))
                    .Select(group => new InventoryManagementRecord
                    {
                        BlockEntityId = group.Key.BlockEntityId, InventoryIndex = group.Key.InventoryIndex,
                        Flags = group.Aggregate(InventoryManagementFlags.None, (flags, record) => flags | record.Flags)
                    }).ToList()
            };
            intent.Validate();
            var body = ProfileCodec.Encode(intent);
            if (!client.Request(MessageKind.Transfer, terminal.CubeGrid.EntityId, terminal.EntityId, null, body, response =>
            {
                string text;
                try
                {
                    if (response.Code == ResultCode.UnknownOutcome) text = "Outcome unknown. Refresh inventories; this intent was not retried.";
                    else if (response.Code != ResultCode.Ok) text = "Server returned: " + response.Code;
                    else
                    {
                        var result = ProfileCodec.Decode<TransferReceipt>(response.Body);
                        if (result.RequestedRaw != intent.AmountRaw || result.MovedRaw < 0 || result.MovedRaw > intent.AmountRaw)
                            throw new InvalidOperationException("Invalid transfer receipt.");
                        text = $"{new MyFixedPoint { RawValue = result.MovedRaw }} / {amount} moved" +
                            (result.Failure == TransferFailure.None ? string.Empty : ": " + result.Failure);
                    }
                }
                catch (Exception) { text = "Invalid server result; outcome unknown. No automatic retry."; }
                Notify(text);
                if (!disposed) SessionChanged();
            })) Notify("Companion unavailable or busy. Nothing was sent; retry when ready.");
            else MyAPIGateway.Utilities?.ShowNotification("Unified Storage: transfer pending…", 750);
        }
        catch (Exception exception)
        {
            Plugin.Instance.Log.Error(exception, "Cannot prepare companion transfer");
            Notify("Cannot prepare this selection. Reopen the inventory and retry.");
        }
        return true;
    }

    private static InventoryAddress Address(MyInventory inventory)
    {
        if (inventory?.Owner == null) throw new InvalidOperationException("Inventory disappeared.");
        for (var index = 0; index < inventory.Owner.InventoryCount; index++)
            if (ReferenceEquals(inventory.Owner.GetInventoryBase(index), inventory))
                return new InventoryAddress { OwnerId = inventory.Owner.EntityId, Index = index };
        throw new InvalidOperationException("Inventory disappeared.");
    }

    private static InventorySelection Selection(ProjectedGridContext context)
    {
        if (context == null) return null;
        return Selection(context.Owner.Session.Scope, context.Role, context.Owner.ViewId);
    }

    private static InventorySelection Selection(MechanicalInventoryScope scope, InventoryRoleProjection role, string viewId)
    {
        var section = role.Section;
        var selection = new InventorySelection
        {
            AnchorId = scope.AnchorGrid.EntityId,
            Group = role.Group?.Copy() ?? new InventoryGroupRecord
            { Selector = InventoryGroupSelector.Family, Family = section.Kind },
            Role = role.Role,
            BlockDefinition = section.InventoryIndex >= 0 ? section.BlockDefinitionId.ToString() : null,
            InventoryIndex = section.InventoryIndex
        };
        var view = viewId.Split(new[] { ':' }, 3);
        if (view.Length == 3 && view[0] == "conveyor") selection.NetworkRootId = long.Parse(view[2]);
        else if (view.Length == 3 && view[0] == "group") selection.TerminalGroup = view[2];
        return selection;
    }
}
