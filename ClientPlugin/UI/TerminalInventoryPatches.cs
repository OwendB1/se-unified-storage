using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin.UI;

internal sealed class UnifiedModeButton : MyGuiControlButton
{
    public override void Draw(float transitionAlpha, float backgroundTransitionAlpha)
    {
        base.Draw(transitionAlpha, backgroundTransitionAlpha);
        var style = MyGuiControlRadioButton.GetVisualStyle(MyGuiControlRadioButtonStyleEnum.FilterAll);
        var focus = ReferenceEquals(BackgroundTexture, style.FocusTexture);
        var highlight = ReferenceEquals(BackgroundTexture, style.HighlightTexture);
        var active = ReferenceEquals(BackgroundTexture, style.ActiveTexture);
        var background = focus ? new Color(142, 188, 206) : highlight ? new Color(60, 76, 82)
            : active ? new Color(91, 115, 123) : new Color(41, 54, 62);
        var glyph = focus ? new Color(33, 41, 45) : highlight || active ? Color.White : new Color(146, 154, 160);
        background.A = glyph.A = (byte)(MathHelper.Clamp(transitionAlpha, 0, 1) * 255);
        // Sprite positions are packed into HalfVector4 by Keen's renderer. Use one
        // representable pixel grid for both columns, including on ultrawide displays.
        var screenEdge = MyGuiManager.GetScreenCoordinateFromNormalizedCoordinate(Vector2.One);
        var pixelStep = 1;
        while (Math.Max(Math.Abs(screenEdge.X), Math.Abs(screenEdge.Y)) >= 2048f * pixelStep)
            pixelStep *= 2;
        var center = MyGuiManager.GetScreenCoordinateFromNormalizedCoordinate(GetPositionAbsoluteCenter());
        center = new Vector2((float)Math.Round(center.X / pixelStep), (float)Math.Round(center.Y / pixelStep));
        var pixels = MyGuiManager.GetScreenSizeFromNormalizedSize(Size);
        var unit = Math.Min(pixels.X, pixels.Y) / pixelStep;
        var side = Math.Max(12 / pixelStep, 2 * (int)Math.Round(unit * 0.28f));
        var stroke = Math.Max(1, (int)Math.Ceiling(Math.Max(2, Math.Round(unit * pixelStep * 0.035f)) / pixelStep));
        var origin = center - new Vector2(side / 2);
        // Keep the frame, cutout and four cells on the same grid, not just the center.
        void Fill(int x, int y, int width, int height, Color color) => MyGuiManager.DrawSpriteBatch(
            MyGuiConstants.BLANK_TEXTURE, ((int)origin.X + x) * pixelStep, ((int)origin.Y + y) * pixelStep,
            width * pixelStep, height * pixelStep, color);
        var mask = (int)Math.Ceiling(unit * 0.08f);
        Fill(-mask, -mask, side + mask * 2, side + mask * 2, background);
        Fill(0, 0, side, side, glyph);
        Fill(stroke, stroke, side - stroke * 2, side - stroke * 2, background);
        var cell = side / 5;
        var inset = (side - cell * 2) / 3;
        for (var x = 0; x < 2; x++)
        for (var y = 0; y < 2; y++)
            Fill(x == 0 ? inset : side - inset - cell, y == 0 ? inset : side - inset - cell, cell, cell, glyph);
        if (!Checked)
        {
            var normalizedCenter = MyGuiManager.GetNormalizedCoordinateFromScreenCoordinate(center * pixelStep);
            void Slash(int width, Color color) => MyGuiManager.DrawSpriteBatch(MyGuiConstants.BLANK_TEXTURE,
                normalizedCenter, MyGuiManager.GetNormalizedSizeFromScreenSize(new Vector2(side * 1.5f, width) * pixelStep),
                color, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER, rotation: -MathHelper.PiOver4);
            Slash(stroke * 3, background);
            Slash(stroke, glyph);
        }
    }
}

internal static class TerminalInventoryBridge
{
    private sealed class State
    {
        public object Instance;
        public IMyGuiControlsParent Parent;
        public MyEntity User;
        public MyEntity Interacted;
        public MyGuiScreenBase Screen;
        public MyGridColorHelper ColorHelper;
        public UnifiedTerminalController Unified;
        public MyGuiControlButton LeftToggle;
        public MyGuiControlButton RightToggle;
        public Dictionary<MyGuiControlBase, Vector2> FilterPositions = new();
        public bool IsUnified;
    }

    private static readonly Dictionary<object, State> States = new();
    private static readonly Type ControllerType = AccessTools.TypeByName("Sandbox.Game.Gui.MyTerminalInventoryController");
    private static readonly MethodInfo InitMethod = AccessTools.Method(ControllerType, "Init");
    private static readonly MethodInfo CloseMethod = AccessTools.Method(ControllerType, "Close");
    [ThreadStatic] private static bool bypass;

    public static bool Init(
        object instance,
        IMyGuiControlsParent parent,
        MyEntity user,
        MyEntity interacted,
        MyGridColorHelper colorHelper,
        MyGuiScreenBase screen)
    {
        if (bypass)
            return true;
        if (!States.TryGetValue(instance, out var state))
        {
            state = new State
            {
                Instance = instance,
                Unified = new UnifiedTerminalController(instance)
            };
            States.Add(instance, state);
        }
        state.Parent = parent;
        state.User = user;
        state.Interacted = interacted;
        state.ColorHelper = colorHelper;
        state.Screen = screen;
        EnsureToggles(state);
        state.IsUnified = Config.Current.UnifiedByDefault;
        state.LeftToggle.Checked = state.RightToggle.Checked = state.IsUnified;
        UpdateToggleIcon(state.LeftToggle);
        UpdateToggleIcon(state.RightToggle);
        if (!state.IsUnified)
            return true;
        try
        {
            state.Unified.Activate(parent, user, interacted, state.LeftToggle.Checked, state.RightToggle.Checked);
        }
        catch (Exception exception)
        {
            Plugin.Instance.Log.Error(exception,
                "Unified inventory initialization failed; restoring the vanilla inventory controller");
            state.Unified.Deactivate();
            state.IsUnified = false;
            state.LeftToggle.Checked = state.RightToggle.Checked = false;
            UpdateToggleIcon(state.LeftToggle);
            UpdateToggleIcon(state.RightToggle);
            return true;
        }
        return false;
    }

    public static bool Close(object instance)
    {
        if (bypass)
            return true;
        if (!States.TryGetValue(instance, out var state))
            return true;
        var runOriginal = !state.IsUnified;
        state.Parent.Controls.Remove(state.LeftToggle);
        state.Parent.Controls.Remove(state.RightToggle);
        foreach (var pair in state.FilterPositions)
            pair.Key.Position = pair.Value;
        state.Unified.Dispose();
        States.Remove(instance);
        return runOriginal;
    }

    public static bool Refresh(object instance)
    {
        if (bypass || !States.TryGetValue(instance, out var state) || !state.IsUnified)
            return true;
        try
        {
            state.Unified.Refresh();
        }
        catch (Exception exception)
        {
            RestoreVanilla(state, exception);
        }
        return false;
    }

    public static bool UpdateBeforeDraw(object instance)
    {
        if (bypass || !States.TryGetValue(instance, out var state) || !state.IsUnified)
            return true;
        try
        {
            state.Unified.UpdateBeforeDraw();
        }
        catch (Exception exception)
        {
            RestoreVanilla(state, exception);
        }
        return false;
    }

    public static bool HandleInput(object instance) =>
        bypass || !States.TryGetValue(instance, out var state) || !state.IsUnified;

    public static bool SetSearch(object instance, string text, bool interactedSide)
    {
        if (bypass || !States.TryGetValue(instance, out var state) || !state.IsUnified)
            return true;
        try
        {
            state.Unified.SetSearch(text, interactedSide);
        }
        catch (Exception exception)
        {
            RestoreVanilla(state, exception);
        }
        return false;
    }

    public static bool GetDefaultFocus(object instance, ref MyGuiControlGrid result)
    {
        if (bypass || !States.TryGetValue(instance, out var state) || !state.IsUnified)
            return true;
        result = state.Unified.GetDefaultFocus();
        return false;
    }

    private static void EnsureToggles(State state)
    {
        if (state.LeftToggle != null &&
            ReferenceEquals(state.Parent.Controls.GetControlByName(state.LeftToggle.Name), state.LeftToggle))
            return;
        state.LeftToggle = CreateToggle(state, "Left");
        state.RightToggle = CreateToggle(state, "Right");
    }

    private static MyGuiControlButton CreateToggle(State state, string prefix)
    {
        var toggle = new UnifiedModeButton
        {
            Name = prefix + "UnifiedStorageToggle",
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
            Checked = Config.Current.UnifiedByDefault
        };
        UpdateToggleIcon(toggle);
        toggle.ButtonClicked += _ =>
        {
            toggle.Checked = !toggle.Checked;
            UpdateToggleIcon(toggle);
            ToggleChanged(state, prefix == "Left");
        };
        var lastFilter = state.Parent.Controls.GetControlByName(prefix + "FilterSystemButton");
        toggle.Position = lastFilter.GetPositionAbsoluteCenter() - state.Parent.GetPositionAbsoluteCenter();
        var offset = new Vector2(toggle.Size.X + 0.004f, 0);
        foreach (var suffix in new[] { "FilterAllButton", "FilterEnergyButton", "FilterShipButton",
                     "FilterStorageButton", "FilterSystemButton" })
        {
            var filter = state.Parent.Controls.GetControlByName(prefix + suffix);
            state.FilterPositions[filter] = filter.Position;
            filter.Position -= offset;
        }
        state.Parent.Controls.Add(toggle);
        return toggle;
    }

    private static void UpdateToggleIcon(MyGuiControlButton toggle)
    {
        var style = MyGuiControlRadioButton.GetVisualStyle(MyGuiControlRadioButtonStyleEnum.FilterAll);
        toggle.CustomStyle = new MyGuiControlButton.StyleDefinition
        {
            // Use the bright palette on hover, not on the focus retained after a click.
            NormalTexture = style.NormalTexture, HighlightTexture = style.FocusTexture,
            FocusTexture = style.HighlightTexture, ActiveTexture = style.ActiveTexture,
            SizeOverride = style.NormalTexture.MinSizeGui
        };
        toggle.SetToolTip(toggle.Checked
            ? "Unified Storage on for this column. Click to show individual inventories without changing the other column. Turning both off restores the original inventory controller."
            : "Unified Storage off for this column. Click to combine this column's inventories. The other column keeps its current layout; items are not moved.");
    }

    private static void ToggleChanged(State state, bool isLeft)
    {
        var useUnified = state.LeftToggle.Checked || state.RightToggle.Checked;
        try
        {
            if (state.IsUnified && useUnified)
            {
                state.Unified.SetUnified(isLeft, isLeft ? state.LeftToggle.Checked : state.RightToggle.Checked);
            }
            else if (useUnified)
            {
                var selection = CaptureSelection(state);
                InvokeOriginal(CloseMethod, state.Instance);
                state.IsUnified = true;
                state.Unified.Activate(state.Parent, state.User, state.Interacted,
                    state.LeftToggle.Checked, state.RightToggle.Checked);
                RestoreSelection(selection);
            }
            else
            {
                var selection = CaptureSelection(state);
                state.Unified.Deactivate();
                state.IsUnified = false;
                InvokeOriginal(InitMethod, state.Instance,
                    state.Parent, state.User, state.Interacted, state.ColorHelper, state.Screen);
                RestoreSelection(selection);
            }
        }
        catch (Exception exception)
        {
            RestoreVanilla(state, exception);
        }
    }

    private static List<MyGuiControlRadioButton> CaptureSelection(State state)
    {
        var selected = new List<MyGuiControlRadioButton>();
        foreach (var prefix in new[] { "Left", "Right" })
        foreach (var suffix in new[] { "SuitButton", "GridButton", "FilterAllButton", "FilterEnergyButton",
                     "FilterShipButton", "FilterStorageButton", "FilterSystemButton" })
            if (state.Parent.Controls.GetControlByName(prefix + suffix) is MyGuiControlRadioButton { Selected: true } button)
                selected.Add(button);
        return selected;
    }

    private static void RestoreSelection(List<MyGuiControlRadioButton> selected)
    {
        foreach (var button in selected)
            button.Selected = true;
    }

    private static void RestoreVanilla(State state, Exception exception)
    {
        Plugin.Instance.Log.Error(exception,
            "Unified inventory controller failed; restoring Keen's inventory UI");
        state.Unified.Deactivate();
        state.IsUnified = false;
        state.LeftToggle.Checked = state.RightToggle.Checked = false;
        UpdateToggleIcon(state.LeftToggle);
        UpdateToggleIcon(state.RightToggle);
        InvokeOriginal(InitMethod, state.Instance,
            state.Parent, state.User, state.Interacted, state.ColorHelper, state.Screen);
    }

    private static object InvokeOriginal(MethodInfo method, object instance, params object[] arguments)
    {
        bypass = true;
        try
        {
            return method.Invoke(instance, arguments);
        }
        finally
        {
            bypass = false;
        }
    }
}

[HarmonyPatch]
internal static class TerminalInventoryInitPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("Sandbox.Game.Gui.MyTerminalInventoryController"), "Init");

    private static bool Prefix(
        object __instance,
        IMyGuiControlsParent controlsParent,
        MyEntity thisEntity,
        MyEntity interactedEntity,
        MyGridColorHelper colorHelper,
        MyGuiScreenBase screen) =>
        TerminalInventoryBridge.Init(__instance, controlsParent, thisEntity, interactedEntity, colorHelper, screen);
}

[HarmonyPatch]
internal static class TerminalInventoryClosePatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("Sandbox.Game.Gui.MyTerminalInventoryController"), "Close");
    private static bool Prefix(object __instance) => TerminalInventoryBridge.Close(__instance);
}

[HarmonyPatch]
internal static class TerminalInventoryRefreshPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("Sandbox.Game.Gui.MyTerminalInventoryController"), "Refresh");
    private static bool Prefix(object __instance) => TerminalInventoryBridge.Refresh(__instance);
}

[HarmonyPatch]
internal static class TerminalInventoryUpdateBeforeDrawPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("Sandbox.Game.Gui.MyTerminalInventoryController"), "UpdateBeforeDraw");
    private static bool Prefix(object __instance) => TerminalInventoryBridge.UpdateBeforeDraw(__instance);
}

[HarmonyPatch]
internal static class TerminalInventoryHandleInputPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("Sandbox.Game.Gui.MyTerminalInventoryController"), "HandleInput");
    private static bool Prefix(object __instance) => TerminalInventoryBridge.HandleInput(__instance);
}

// Native callbacks can outlive their controller's Close (focus transitions in particular).
// While Unified owns the page, only its own grid handlers may process input.
[HarmonyPatch]
internal static class TerminalInventoryNativeCallbacksPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var type = AccessTools.TypeByName("Sandbox.Game.Gui.MyTerminalInventoryController");
        foreach (var name in new[] { "ParentsFocusChanged", "grid_focusChanged", "grid_ItemSelected",
                     "grid_ItemDragged", "grid_ItemDoubleClicked", "grid_ItemClicked", "grid_ItemReleased",
                     "grid_ReleasedWithoutItem", "grid_ItemControllerAction" })
            yield return AccessTools.Method(type, name);
    }
    private static bool Prefix(object __instance) => TerminalInventoryBridge.HandleInput(__instance);
}

[HarmonyPatch]
internal static class TerminalInventorySetSearchPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("Sandbox.Game.Gui.MyTerminalInventoryController"), "SetSearch");
    private static bool Prefix(object __instance, string text, bool interactedSide) =>
        TerminalInventoryBridge.SetSearch(__instance, text, interactedSide);
}

[HarmonyPatch]
internal static class TerminalInventoryGetDefaultFocusPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("Sandbox.Game.Gui.MyTerminalInventoryController"), "GetDefaultFocus");
    private static bool Prefix(object __instance, ref MyGuiControlGrid __result) =>
        TerminalInventoryBridge.GetDefaultFocus(__instance, ref __result);
}
