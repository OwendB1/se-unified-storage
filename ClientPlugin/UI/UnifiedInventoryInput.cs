using System;
using System.Linq;
using Sandbox.Common.ObjectBuilders.Definitions;
using ClientPlugin.Inventory;
using Sandbox;
using Sandbox.Game;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Input;

namespace ClientPlugin.UI;

internal sealed partial class UnifiedTerminalController
{
    private MyGuiControlGrid controllerHeldGrid;
    private Action controllerTransfer, controllerAmount;
    private int controllerPressedAt;
    private MyGuiControlGrid selectedInputGrid;
    private MyGuiControlButton throwOut;
    private MyGuiControlGrid ctrlClickGrid;
    private int ctrlClickIndex;
    private Action ctrlClickTransfer;
    private bool dragAmountRequested;

    private void BindInventoryInput(Pane pane, MyGuiControlGrid grid)
    {
        void Focus()
        {
            selectedInputGrid = grid;
            if (grid.UserData is ProjectedGridContext projected) pane.FocusedProjected = projected;
            else pane.FocusedReal = grid;
        }
        grid.FocusChanged += (_, focused) => { if (focused) Focus(); };
        grid.ReleasedWithoutItem += _ => Focus();
        grid.ItemClicked += (_, args) =>
        {
            Focus();
            var ctrl = MyInput.Static.IsAnyCtrlKeyPressed();
            var shift = MyInput.Static.IsAnyShiftKeyPressed();
            if (ctrl && !shift && args.Button == MySharedButtonsEnum.Primary)
            {
                // Wait until release so Ctrl-drag does not also move ten items.
                ctrlClickGrid = grid;
                ctrlClickIndex = args.ItemIndex;
                var item = grid.GetItemAt(args.ItemIndex)?.UserData;
                var amount = MyFixedPoint.Min(GetAmount(grid, args.ItemIndex), 10);
                ctrlClickTransfer = () => TransferOpposite(pane, grid, args.ItemIndex, amount, item);
                return;
            }
            // Grab Single Item patches vanilla handlers, which these grids replace.
            if (ctrl || shift || MyInput.Static.IsAnyAltKeyPressed())
                TransferOpposite(pane, grid, args.ItemIndex,
                    MyFixedPoint.Min(GetAmount(grid, args.ItemIndex), (shift ? 100 : 1) * (ctrl ? 10 : 1)));
        };
        grid.ItemAccepted += (_, args) => RealItemDoubleClicked(pane, grid, args);
        grid.ItemReleased += (_, args) =>
        {
            if (ctrlClickTransfer != null && ctrlClickGrid == grid) return;
            if (MyInput.Static.IsAnyCtrlKeyPressed() || MyInput.Static.IsAnyShiftKeyPressed()) return;
            if (TryResolveUsableItem(grid, args.ItemIndex, out var inventory, out var item))
                MyUsableItemHelper.ItemActivatedGridKeyboard(item, inventory, inventory.Owner as MyCharacter, args.Button);
        };
        grid.ItemControllerAction = (sender, index, action, pressed) => GamepadTransfer(pane, sender, index, action, pressed);
        grid.GamepadHelpText = "A: transfer · Hold A: amount · LB/RB: 10/100 · Y: use";
    }

    private void TransferOpposite(Pane sourcePane, MyGuiControlGrid grid, int index, MyFixedPoint amount,
        object originalItem = null)
    {
        var other = sourcePane.IsLeft ? right : left;
        var destination = other.ShowGrid && other.Unified ? other.FocusedProjected?.Grid : other.FocusedReal;
        if (destination != null && amount > MyFixedPoint.Zero)
            ExecuteTransfer(grid, index, destination, amount, originalItem: originalItem);
    }

    private bool GamepadTransfer(Pane pane, MyGuiControlGrid grid, int index, MyGridItemAction action, bool pressed)
    {
        if (action != MyGridItemAction.Button_A)
            return pressed && TryResolveUsableItem(grid, index, out var inventory, out var usable) &&
                inventory.Owner is MyCharacter character &&
                MyUsableItemHelper.ItemActivatedGridGamepad(usable, inventory, character, action);
        if (!pressed)
        {
            var transfer = controllerTransfer;
            controllerHeldGrid = null;
            controllerTransfer = controllerAmount = null;
            transfer?.Invoke();
            return transfer != null;
        }
        var item = grid.GetItemAt(index)?.UserData;
        var amount = GetAmount(grid, index);
        if (item == null || amount <= MyFixedPoint.Zero) return false;
        var lb = MyControllerHelper.IsControl(MyControllerHelper.CX_GUI, MyControlsGUI.SHIFT_LEFT, MyControlStateType.PRESSED);
        var rb = MyControllerHelper.IsControl(MyControllerHelper.CX_GUI, MyControlsGUI.SHIFT_RIGHT, MyControlStateType.PRESSED);
        if (lb || rb) amount = MyFixedPoint.Min(amount, lb ? (rb ? 1000 : 10) : 100);
        controllerHeldGrid = grid;
        controllerPressedAt = MySandboxGame.TotalGamePlayTimeInMilliseconds;
        controllerTransfer = () => TransferOpposite(pane, grid, index, amount, item);
        var definition = GetDefinition(grid, index);
        controllerAmount = lb || rb ? null : () => ShowAmountDialog(amount, definition,
            value => TransferOpposite(pane, grid, index, value, item));
        return true;
    }

    private void UpdateInventoryInput()
    {
        if (ctrlClickTransfer != null && !MyInput.Static.IsPrimaryButtonPressed())
        {
            var transfer = ctrlClickTransfer;
            var clicked = ctrlClickGrid.IsMouseOver && ctrlClickGrid.MouseOverIndex == ctrlClickIndex;
            ctrlClickTransfer = null;
            ctrlClickGrid = null;
            if (clicked && MyScreenManager.GetScreenWithFocus() is Sandbox.Game.Gui.MyGuiScreenTerminal)
                transfer();
        }
        var focused = MyScreenManager.FocusedControl as MyGuiControlGrid;
        if (focused != null && (focused.UserData is ProjectedGridContext || focused.UserData is MyInventory))
            selectedInputGrid = focused;
        if (throwOut != null) throwOut.Enabled = CanThrowOut(selectedInputGrid);
        UpdatePlannerButtons();
        if (MyScreenManager.GetScreenWithFocus() is not Sandbox.Game.Gui.MyGuiScreenTerminal) return;
        if (MyControllerHelper.IsControl(MyControllerHelper.CX_GUI, MyControlsGUI.LEFT_STICK_BUTTON)) CycleFilter(left);
        if (MyControllerHelper.IsControl(MyControllerHelper.CX_GUI, MyControlsGUI.RIGHT_STICK_BUTTON)) CycleFilter(right);
        if (MyControllerHelper.IsControl(MyControllerHelper.CX_GUI, MyControlsGUI.MENU)) ThrowOutSelected();
        if (MyControllerHelper.IsControl(MyControllerHelper.CX_GUI, MyControlsGUI.VIEW)) RunPlannerAction("DepositAllButton");
        if (MyControllerHelper.IsControl(MyControllerHelper.CX_GUI, MyControlsGUI.BUTTON_X)) RunPlannerAction("SelectedToProductionButton");
        if (focused?.SelectedIndex is int index && focused.GetItemAt(index) != null)
        {
            // Keen's grid accept event only follows controller bindings on this build.
            if (MyInput.Static.IsNewKeyReleased(MyKeys.Enter) &&
                !MyInput.Static.IsAnyCtrlKeyPressed() && !MyInput.Static.IsAnyShiftKeyPressed() &&
                !MyControllerHelper.IsControl(MyControllerHelper.CX_GUI, MyControlsGUI.ACCEPT, MyControlStateType.NEW_RELEASED) &&
                !MyControllerHelper.IsControl(MyControllerHelper.CX_GUI, MyControlsGUI.ACCEPT_MOD1, MyControlStateType.NEW_RELEASED))
            {
                var pane = ReferenceEquals((focused.Owner as MyGuiControlBase)?.Owner, left.List) ? left : right;
                TransferOpposite(pane, focused, index, GetAmount(focused, index));
            }
            int? offset = null;
            if (MyControllerHelper.IsControl(MyControllerHelper.CX_GUI, MyControlsGUI.MOVE_ITEM_UP)) offset = -focused.ColumnsCount;
            else if (MyControllerHelper.IsControl(MyControllerHelper.CX_GUI, MyControlsGUI.MOVE_ITEM_DOWN)) offset = focused.ColumnsCount;
            else if (MyControllerHelper.IsControl(MyControllerHelper.CX_GUI, MyControlsGUI.MOVE_ITEM_LEFT)) offset = -1;
            else if (MyControllerHelper.IsControl(MyControllerHelper.CX_GUI, MyControlsGUI.MOVE_ITEM_RIGHT)) offset = 1;
            if (offset.HasValue)
            {
                var target = Math.Max(0, Math.Min(focused.RowsCount * focused.ColumnsCount - 1, index + offset.Value));
                if (focused.UserData is ProjectedGridContext context) ReorderProjected(context, index, target);
                else ExecuteTransfer(focused, index, focused, GetAmount(focused, index), target);
                focused.SelectedIndex = target;
            }
        }
        if (controllerHeldGrid == null) return;
        if (!controllerHeldGrid.HasFocus)
        {
            controllerHeldGrid = null;
            controllerTransfer = controllerAmount = null;
            return;
        }
        if (controllerAmount == null || MySandboxGame.TotalGamePlayTimeInMilliseconds - controllerPressedAt < 600) return;
        var show = controllerAmount;
        controllerHeldGrid = null;
        controllerTransfer = controllerAmount = null;
        show();
    }

    private void CycleFilter(Pane pane)
    {
        if (!pane.ShowGrid && interacted != null) pane.TypeGroup.SelectByIndex(1);
        else if ((pane.FilterGroup.SelectedIndex ?? 0) < pane.FilterGroup.Count - 1)
            pane.FilterGroup.SelectByIndex((pane.FilterGroup.SelectedIndex ?? 0) + 1);
        else { pane.FilterGroup.SelectByIndex(0); pane.TypeGroup.SelectByIndex(0); }
    }

    private void ReorderProjected(ProjectedGridContext context, int from, int to)
    {
        if (InventoryDisplayOrder.IsPriorityDriven(context.Role))
            Sandbox.ModAPI.MyAPIGateway.Utilities?.ShowNotification("Refinery input order is controlled by Ore Priority.", 3000);
        else InventoryDisplayOrder.Move(profiles[context.Owner.Session], context.Owner.ViewId, context.Role,
            context.Grid.GetItemAt(from)?.UserData as ProjectedInventoryStack,
            context.Grid.IsValidIndex(to) ? context.Grid.GetItemAt(to)?.UserData as ProjectedInventoryStack : null);
        SessionChanged();
    }

    private static bool CanThrowOut(MyGuiControlGrid grid) => grid?.UserData is MyInventory inventory &&
        ReferenceEquals(inventory.Owner, MySession.Static?.LocalCharacter) &&
        grid.SelectedIndex is int index && grid.GetItemAt(index)?.UserData is MyPhysicalInventoryItem;

    private void ThrowOutSelected()
    {
        if (CanThrowOut(selectedInputGrid)) DropCharacterItem(selectedInputGrid,
            (MyPhysicalInventoryItem)selectedInputGrid.GetItemAt(selectedInputGrid.SelectedIndex.Value).UserData);
    }

    private static void DropCharacterItem(MyGuiControlGrid grid, MyPhysicalInventoryItem item)
    {
        if (grid?.UserData is not MyInventory inventory || inventory.Owner == null ||
            !ReferenceEquals(inventory.Owner, MySession.Static?.LocalCharacter)) return;
        var index = inventory.GetItems().FindIndex(current => current.ItemId == item.ItemId);
        if (index >= 0) inventory.DropItem(index, inventory.GetItems()[index].Amount);
    }

    private void CreateThrowOutButton()
    {
        if (controlsParent.Controls.GetControlByName("ThrowOutButton") is not MyGuiControlButton native) return;
        throwOut = new MyGuiControlButton(native.Position, native.VisualStyle, native.Size,
            originAlign: native.OriginAlign, toolTip: "Drop the selected item from your character inventory.",
            onButtonClick: _ => ThrowOutSelected())
        { Name = "UnifiedThrowOut", Icon = native.Icon, IconScale = native.IconScale, Enabled = false };
        controlsParent.Controls.Add(throwOut);
    }

    private static bool TryResolveUsableItem(MyGuiControlGrid grid, int index,
        out MyInventory inventory, out MyPhysicalInventoryItem item)
    {
        inventory = null;
        item = default;
        var data = grid.GetItemAt(index)?.UserData;
        if (data is ProjectedInventoryStack projected && projected.Sources.Count == 1)
        {
            var source = projected.Sources[0];
            inventory = source.Inventory;
            var current = inventory.GetItemByID(source.ItemId);
            if (!current.HasValue) return false;
            item = current.Value;
        }
        else if (data is MyPhysicalInventoryItem physical && grid.UserData is MyInventory real)
        {
            inventory = real;
            var current = real.GetItemByID(physical.ItemId);
            if (!current.HasValue) return false;
            item = current.Value;
        }
        if (inventory?.Owner == null || inventory.Owner.Closed) return false;
        if (inventory.Owner is Sandbox.Game.Entities.Cube.MyTerminalBlock block &&
            !block.HasPlayerAccess(MySession.Static?.LocalPlayerId ?? 0L)) return false;
        return ReferenceEquals(inventory.Owner, MySession.Static?.LocalCharacter) ||
            item.Content is MyObjectBuilder_Datapad;
    }
}
