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
        var center = MyGuiManager.GetScreenCoordinateFromNormalizedCoordinate(GetPositionAbsoluteCenter());
        center = new Vector2((float)Math.Round(center.X), (float)Math.Round(center.Y));
        var pixels = MyGuiManager.GetScreenSizeFromNormalizedSize(Size);
        var unit = Math.Min(pixels.X, pixels.Y);
        var side = Math.Max(12, 2 * (int)Math.Round(unit * 0.28f));
        var stroke = Math.Max(2, (int)Math.Round(unit * 0.035f));
        var origin = center - new Vector2(side / 2);
        // Integer pixels and mirrored offsets keep all four cells and margins symmetric.
        void Fill(int x, int y, int width, int height, Color color) => MyGuiManager.DrawSpriteBatch(
            MyGuiConstants.BLANK_TEXTURE, (int)origin.X + x, (int)origin.Y + y, width, height, color);
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
            var normalizedCenter = MyGuiManager.GetNormalizedCoordinateFromScreenCoordinate(center);
            void Slash(int width, Color color) => MyGuiManager.DrawSpriteBatch(MyGuiConstants.BLANK_TEXTURE,
                normalizedCenter, MyGuiManager.GetNormalizedSizeFromScreenSize(new Vector2(side * 1.5f, width)),
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
        public MyGuiControlButton Toggle;
        public Action<MyGuiControlButton> ToggleHandler;
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
        EnsureToggle(state);
        state.IsUnified = Config.Current.UnifiedByDefault;
        state.Toggle.Checked = state.IsUnified;
        UpdateToggleIcon(state);
        if (!state.IsUnified)
            return true;
        try
        {
            state.Unified.Activate(parent, user, interacted);
        }
        catch (Exception exception)
        {
            Plugin.Instance.Log.Error(exception,
                "Unified inventory initialization failed; restoring the vanilla inventory controller");
            state.Unified.Deactivate();
            state.IsUnified = false;
            state.Toggle.Checked = false;
            UpdateToggleIcon(state);
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
        DetachToggle(state);
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

    private static void EnsureToggle(State state)
    {
        const float filterButtonCenterY = -0.30925f;
        state.Toggle = state.Parent.Controls.GetControlByName("UnifiedStorageToggle") as MyGuiControlButton;
        if (state.Toggle != null)
            return;
        state.Toggle = new UnifiedModeButton
        {
            Name = "UnifiedStorageToggle",
            Position = new Vector2(0, filterButtonCenterY),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER,
            Checked = Config.Current.UnifiedByDefault
        };
        var toggleSize = MyGuiControlRadioButton
            .GetVisualStyle(MyGuiControlRadioButtonStyleEnum.FilterAll).NormalTexture.MinSizeGui;
        UpdateToggleIcon(state);
        state.ToggleHandler = _ =>
        {
            state.Toggle.Checked = !state.Toggle.Checked;
            UpdateToggleIcon(state);
            ToggleChanged(state);
        };
        state.Toggle.ButtonClicked += state.ToggleHandler;
        var label = new MyGuiControlLabel
        {
            Name = "UnifiedStorageToggleLabel",
            Position = new Vector2(0, filterButtonCenterY),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER,
            Text = "Unified Storage",
            TextScale = 0.62f
        };
        // Native selectors use left anchors, filters use right anchors. Center the
        // whole label/button pair in their real gap, including when filters are hidden.
        var gridButton = state.Parent.Controls.GetControlByName("LeftGridButton");
        var filterButton = state.Parent.Controls.GetControlByName("LeftFilterAllButton");
        var center = (gridButton.GetPositionAbsoluteTopRight().X + filterButton.GetPositionAbsoluteTopLeft().X) * 0.5f
            - state.Parent.GetPositionAbsoluteCenter().X;
        const float labelGap = 0.006f;
        var right = center + (label.Size.X + labelGap + toggleSize.X) * 0.5f;
        state.Toggle.Position = new Vector2(right, filterButtonCenterY);
        label.Position = new Vector2(right - toggleSize.X - labelGap, filterButtonCenterY);
        state.Parent.Controls.Add(label);
        state.Parent.Controls.Add(state.Toggle);
    }

    private static void UpdateToggleIcon(State state)
    {
        var style = MyGuiControlRadioButton.GetVisualStyle(MyGuiControlRadioButtonStyleEnum.FilterAll);
        state.Toggle.CustomStyle = new MyGuiControlButton.StyleDefinition
        {
            // Use the bright palette on hover, not on the focus retained after a click.
            NormalTexture = style.NormalTexture, HighlightTexture = style.FocusTexture,
            FocusTexture = style.HighlightTexture, ActiveTexture = style.ActiveTexture,
            SizeOverride = style.NormalTexture.MinSizeGui
        };
        state.Toggle.SetToolTip(state.Toggle.Checked
            ? "Unified Storage on. Click to restore vanilla inventories."
            : "Unified Storage off. Click to combine inventories.");
    }

    private static void ToggleChanged(State state)
    {
        if (state.IsUnified == state.Toggle.Checked)
            return;
        if (state.Toggle.Checked)
        {
            InvokeOriginal(CloseMethod, state.Instance);
            state.IsUnified = true;
            try
            {
                state.Unified.Activate(state.Parent, state.User, state.Interacted);
            }
            catch (Exception exception)
            {
                RestoreVanilla(state, exception);
            }
        }
        else
        {
            state.Unified.Deactivate();
            state.IsUnified = false;
            InvokeOriginal(InitMethod, state.Instance,
                state.Parent, state.User, state.Interacted, state.ColorHelper, state.Screen);
        }
    }

    private static void RestoreVanilla(State state, Exception exception)
    {
        Plugin.Instance.Log.Error(exception,
            "Unified inventory controller failed; restoring Keen's inventory UI");
        state.Unified.Deactivate();
        state.IsUnified = false;
        state.Toggle.Checked = false;
        UpdateToggleIcon(state);
        InvokeOriginal(InitMethod, state.Instance,
            state.Parent, state.User, state.Interacted, state.ColorHelper, state.Screen);
    }

    private static void DetachToggle(State state)
    {
        if (state.Toggle != null && state.ToggleHandler != null)
            state.Toggle.ButtonClicked -= state.ToggleHandler;
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
