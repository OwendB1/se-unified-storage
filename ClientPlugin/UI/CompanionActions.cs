using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Inventory;
using ClientPlugin.Profiles;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI;
using Shared.Companion;

namespace ClientPlugin.UI;

internal static class CompanionActions
{
    public static bool TryRun(MechanicalInventoryScope scope, ScopeProfile profile, ShipAction action,
        List<InventorySelection> selections = null, string groupId = null, Func<bool> canContinue = null)
    {
        var client = Plugin.Instance.Companion;
        var capability = ShipActionIntent.Capability(action);
        void Notify(string text) => MyAPIGateway.Utilities?.ShowNotification("Unified Storage: " + text, 6000);
        if (client?.AllowsLocal(scope.AnchorGrid, capability) == false)
        { Notify("Server owns this service, or ownership is unknown. Check Shared profile."); return true; }
        if (client?.Supports(capability) != true) return false;
        if (client.Busy || CompanionJobScreen.HasPending || Plugin.Instance.Transfers.PendingCount != 0 || Plugin.Instance.ProductionQueue.PendingCount != 0 ||
            Plugin.Instance.RefinerySorts.PendingCount != 0)
        { Notify("Wait for the current inventory operation to finish."); return true; }
        try
        {
            var terminal = scope.InteractedEntity as MyTerminalBlock ?? scope.Inventories.Select(member => member.Owner).OfType<MyTerminalBlock>().FirstOrDefault();
            if (terminal == null) { Notify("No accessible terminal in this scope."); return true; }
            var intent = new ShipActionIntent
            { Action = action, Settings = ProfileCodec.Clone(profile), GroupId = groupId, Selections = selections ?? new() };
            intent.Validate();
            if (!client.Request(MessageKind.Action, scope.AnchorGrid.EntityId, terminal.EntityId, null, ProfileCodec.Encode(intent), response =>
            {
                try
                {
                    if (response.Code == ResultCode.UnknownOutcome)
                        Notify("Outcome unknown. Refresh before retrying; no automatic replay.");
                    else if (response.Code != ResultCode.Ok) Notify("Server returned " + response.Code);
                    else
                    {
                        var receipt = ProfileCodec.Decode<ActionReceipt>(response.Body);
                        if (receipt.JobId != Guid.Empty)
                        {
                            CompanionJobScreen.Start(scope.AnchorGrid.EntityId, terminal.EntityId, receipt.JobId,
                                action == ShipAction.Rebalance ? canContinue : null);
                            return;
                        }
                        Notify($"{action}: {receipt.Mutations} changes; {receipt.Failure}. " + receipt.Detail);
                    }
                }
                catch (Exception) { Notify("Invalid server result; outcome unknown. No automatic replay."); }
            })) Notify("Companion busy or unavailable; nothing was sent.");
        }
        catch (Exception exception)
        {
            Plugin.Instance.Log.Error(exception, "Cannot prepare server action");
            Notify("Cannot prepare this action. See plugin log.");
        }
        return true;
    }
}
