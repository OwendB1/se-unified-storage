using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClientPlugin.Inventory;
using ClientPlugin.Profiles;
using HarmonyLib;
using Sandbox;
using Sandbox.Game;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Game.Entity;
using VRage;
using VRage.Input;

namespace ClientPlugin.UI;

internal sealed partial class UnifiedTerminalController
{
    private readonly List<MyGuiControlButton> plannerButtons = new();

    private void CreatePlannerButtons()
    {
        foreach (var (name, help) in new[]
                 {
                     ("DepositAllButton", "Deposit eligible items from the left column into the right column. Uses the native bulk-transfer action, not the loadout policy."),
                     ("WithdrawButton", "Withdraw Build Planner components from the right column into the left. Shift keeps the plan; Ctrl repeats it 10 times; Ctrl+Shift repeats it 100 times."),
                     ("AddToProductionButton", "Queue Build Planner components in the accessed ship's assemblers. Ctrl: 10 plans. Ctrl+Shift: 100 plans."),
                     ("SelectedToProductionButton", "Queue the selected item in the accessed ship's assemblers. Ctrl: 10 items. Ctrl+Shift: 100 items.")
                 })
        {
            if (controlsParent.Controls.GetControlByName(name) is not MyGuiControlButton native) continue;
            var button = new MyGuiControlButton(native.Position, native.VisualStyle, native.Size,
                originAlign: native.OriginAlign, toolTip: UnifiedStorageHelp.Wrap(help),
                onButtonClick: _ => RunPlannerAction(name))
            { Name = "Unified" + name, UserData = name, Icon = native.Icon, IconScale = native.IconScale };
            plannerButtons.Add(button);
            controlsParent.Controls.Add(button);
        }
        UpdatePlannerButtons();
    }

    private void UpdatePlannerButtons()
    {
        foreach (var button in plannerButtons)
        {
            var name = (string)button.UserData;
            button.Enabled = CanRunPlannerAction(name);
            button.Visible = !MyInput.Static.IsJoystickLastUsed || name is "WithdrawButton" or "AddToProductionButton";
        }
        if (throwOut != null) throwOut.Visible = !MyInput.Static.IsJoystickLastUsed;
    }

    private bool CanRunPlannerAction(string name) => interacted is { Closed: false } &&
        MySession.Static?.LocalCharacter != null && Plugin.Instance.Transfers.PendingCount == 0 &&
        (name == "DepositAllButton" || (name == "SelectedToProductionButton"
            ? selectedInputGrid?.SelectedIndex is int index && selectedInputGrid.GetItemAt(index) != null
            : MySession.Static.LocalCharacter.BuildPlanner.Count > 0));

    private void RunPlannerAction(string name)
    {
        if (!Active || !CanRunPlannerAction(name)) return;
        try
        {
            var type = vanillaController.GetType();
            var ctrl = MyInput.Static.IsAnyCtrlKeyPressed();
            var shift = MyInput.Static.IsAnyShiftKeyPressed();
            var lb = MyControllerHelper.IsControl(MyControllerHelper.CX_GUI, MyControlsGUI.SHIFT_LEFT, MyControlStateType.PRESSED);
            var rb = MyControllerHelper.IsControl(MyControllerHelper.CX_GUI, MyControlsGUI.SHIFT_RIGHT, MyControlStateType.PRESSED);
            var multiple = MyInput.Static.IsJoystickLastUsed ? (lb ? (rb ? 1000 : 10) : (rb ? 100 : 1)) : ctrl ? (shift ? 100 : 10) : 1;
            if (name is "DepositAllButton" or "WithdrawButton")
            {
                var withdraw = name == "WithdrawButton";
                var available = AccessTools.Method(type, "GetAvailableInventoriesStatic");
                Action<MyEntity, MyDefinitionId, List<MyInventory>, MyEntity, bool> find = (owner, id, result, access, requireAmount) =>
                {
                    available.Invoke(null, new object[] { owner, id, result, access, requireAmount });
                    var allowed = PaneInventories(right, id, destination: !withdraw);
                    result.RemoveAll(inventory => !allowed.Contains(inventory));
                };
                var inventories = PaneInventories(left, null, destination: withdraw).ToArray();
                if (withdraw)
                {
                    int? keep = MyInput.Static.IsJoystickLastUsed ? (lb ? 1 : rb ? 10 : (int?)null)
                        : ctrl || shift ? multiple : (int?)null;
                    var missing = AccessTools.Method(type, "WithdrawToInventories").Invoke(null,
                        new object[] { inventories, find, interacted, new HashSet<MyInventory>(), keep });
                    if (!keep.HasValue) MySession.Static.LocalCharacter.CleanFinishedBuildPlanner();
                    var message = (string)AccessTools.Method(type, "GetMissingComponentsText").Invoke(null, new[] { missing });
                    if (!string.IsNullOrEmpty(message)) NotifyPlanner(message);
                }
                else
                {
                    var failed = (int)AccessTools.Method(type, "depositAllFrom").Invoke(null,
                        new object[] { inventories, interacted, find });
                    if (failed > 0) NotifyPlanner($"{failed} item types could not be deposited. Check space, filters and conveyor access.");
                }
            }
            else
            {
                int failed;
                if (name == "AddToProductionButton")
                {
                    if (MyInput.Static.IsJoystickLastUsed) multiple = rb ? 10 : 1;
                    failed = (int)AccessTools.Method(type, "AddComponentsToProduction", new[] { typeof(MyEntity), typeof(int?) })
                        .Invoke(null, new object[] { interacted, multiple });
                }
                else
                {
                    // Reuse Keen's recipe/assembler selection without reviving its closed UI controller.
                    var componentType = type.GetNestedType("QueueComponent", BindingFlags.Public | BindingFlags.NonPublic);
                    var component = Activator.CreateInstance(componentType);
                    componentType.GetField("Id").SetValue(component, GetDefinition(selectedInputGrid, selectedInputGrid.SelectedIndex.Value));
                    componentType.GetField("Count").SetValue(component, multiple);
                    var queueType = typeof(Queue<>).MakeGenericType(componentType);
                    var queue = Activator.CreateInstance(queueType);
                    queueType.GetMethod("Enqueue").Invoke(queue, new[] { component });
                    failed = (int)AccessTools.Method(type, "AddComponentsToProduction", new[] { queueType, typeof(MyEntity) })
                        .Invoke(null, new[] { queue, interacted });
                }
                if (failed > 0) NotifyPlanner($"{failed} production entries could not be queued. Check assembler access and supported recipes.");
            }
            SessionChanged();
        }
        catch (Exception exception)
        {
            Plugin.Instance.Log.Error(exception, "Unified Storage native inventory action failed");
            NotifyPlanner("Inventory action failed. See the Unified Storage log for details.");
        }
    }

    private HashSet<MyInventory> PaneInventories(Pane pane, MyDefinitionId? item, bool destination)
    {
        var result = new HashSet<MyInventory>();
        foreach (var control in pane.List.Controls.Where(control => control.Visible && control.Enabled))
        {
            if (control is UnifiedInventoryOwnerControl unified)
            {
                foreach (var context in unified.Grids.Select(grid => (ProjectedGridContext)grid.UserData))
                    foreach (var member in context.Role.Members)
                        if ((!item.HasValue || context.Role.Accepts(member, item.Value)) && Allowed(GetFlags(member)))
                            result.Add(member.Inventory);
            }
            else if (control is MyGuiControlInventoryOwner real)
            {
                foreach (var grid in real.ContentGrids)
                    if (grid.UserData is MyInventory inventory && inventory.Owner is { Closed: false })
                    {
                        var member = sessions.SelectMany(session => session.Refresh().Roles).SelectMany(role => role.Members)
                            .FirstOrDefault(candidate => candidate.Inventory == inventory);
                        if (member == null || Allowed(GetFlags(member))) result.Add(inventory);
                    }
            }
        }
        return result;

        bool Allowed(InventoryManagementFlags flags) => (flags & (InventoryManagementFlags.ManualBlock |
            InventoryManagementFlags.ReservedInventory | (destination ? InventoryManagementFlags.NoUnifiedCargoDestination : 0))) == 0;
    }

    private static void NotifyPlanner(string message) => Sandbox.ModAPI.MyAPIGateway.Utilities?.ShowNotification(message, 5000);
}
