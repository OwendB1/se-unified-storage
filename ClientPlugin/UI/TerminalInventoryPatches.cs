using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Graphics.GUI;
using VRage.Game.Entity;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin.UI;

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
        public MyGuiControlCheckbox Toggle;
        public Action<MyGuiControlCheckbox> ToggleHandler;
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
        state.Toggle.IsChecked = state.IsUnified;
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
            state.Toggle.IsChecked = false;
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
        state.Toggle = state.Parent.Controls.GetControlByName("UnifiedStorageToggle") as MyGuiControlCheckbox;
        if (state.Toggle != null)
            return;
        state.Toggle = new MyGuiControlCheckbox
        {
            Name = "UnifiedStorageToggle",
            Position = new Vector2(-0.018f, -0.315f),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER,
            IsChecked = Config.Current.UnifiedByDefault
        };
        state.Toggle.SetToolTip("Use Unified Storage. Turn this off at any time to restore Keen's original inventory UI.");
        state.ToggleHandler = _ => ToggleChanged(state);
        state.Toggle.IsCheckedChanged += state.ToggleHandler;
        state.Parent.Controls.Add(state.Toggle);
    }

    private static void ToggleChanged(State state)
    {
        if (state.IsUnified == state.Toggle.IsChecked)
            return;
        if (state.Toggle.IsChecked)
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
        state.Toggle.IsChecked = false;
        InvokeOriginal(InitMethod, state.Instance,
            state.Parent, state.User, state.Interacted, state.ColorHelper, state.Screen);
    }

    private static void DetachToggle(State state)
    {
        if (state.Toggle != null && state.ToggleHandler != null)
            state.Toggle.IsCheckedChanged -= state.ToggleHandler;
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
