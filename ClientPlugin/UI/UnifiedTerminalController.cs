using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ClientPlugin.Automation;
using ClientPlugin.Inventory;
using ClientPlugin.Profiles;
using ClientPlugin.Transfers;
using HarmonyLib;
using Sandbox;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Input;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin.UI;

internal sealed class GasSystemFilterOverlay : MyGuiControlBase
{
    private const string MaskTexture = "Textures\\GUI\\Icons\\OxygenIcon.dds";
    private readonly MyGuiControlRadioButton button;

    public GasSystemFilterOverlay(MyGuiControlRadioButton button)
        : base(button.Position, button.Size, isActiveControl: false, canHaveFocus: false,
            originAlign: button.OriginAlign)
    {
        this.button = button;
        Name = button.Name + "GasGlyph";
        IsHitTestVisible = false;
    }

    public override void Draw(float transitionAlpha, float backgroundTransitionAlpha)
    {
        if (!button.Visible)
            return;

        var highlighted = button.HasHighlight;
        var focused = !highlighted && button.HasFocus;
        var selected = !highlighted && !focused && button.Selected;
        var background = highlighted
            ? WithAlpha(60, 76, 82, transitionAlpha)
            : focused
                ? WithAlpha(142, 188, 206, transitionAlpha)
                : selected
                    ? WithAlpha(91, 115, 123, transitionAlpha)
                    : WithAlpha(41, 54, 62, transitionAlpha);
        var glyph = focused
            ? WithAlpha(33, 41, 45, transitionAlpha)
            : highlighted || selected
                ? WithAlpha(255, 255, 255, transitionAlpha)
                : WithAlpha(146, 154, 160, transitionAlpha);
        var center = GetPositionAbsoluteCenter();

        // Cover only the stock gear, retaining the native border and all button sizing.
        MyGuiManager.DrawSpriteBatch(MyGuiConstants.BLANK_TEXTURE, center, Size * 0.7f,
            background, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER);
        MyGuiManager.DrawSpriteBatch(MyGuiConstants.BLANK_TEXTURE, center, Size * 0.62f,
            glyph, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
            maskTexture: MaskTexture);
    }

    private static Color WithAlpha(byte red, byte green, byte blue, float alpha) =>
        new(red, green, blue, (byte)(MathHelper.Clamp(alpha, 0f, 1f) * byte.MaxValue));
}

internal sealed partial class UnifiedTerminalController : IDisposable
{
    private enum PaneFilter
    {
        All,
        Energy,
        Ship,
        Storage,
        System
    }

    private sealed class Pane
    {
        public bool IsLeft;
        public MyGuiControlList List;
        public MyGuiControlRadioButton SuitButton;
        public MyGuiControlRadioButton GridButton;
        public MyGuiControlRadioButtonGroup TypeGroup;
        public MyGuiControlRadioButtonGroup FilterGroup;
        public MyGuiControlSearchBox Search;
        public MyGuiControlCheckbox HideEmpty;
        public MyGuiControlLabel HideEmptyLabel;
        public MyGuiControlRadioButton SystemFilterButton;
        public GasSystemFilterOverlay SystemFilterOverlay;
        public ScopeTreeCombobox ScopeSelector;
        public List<ScopeChoice> ScopeChoices = new();
        public string SelectedScopeId;
        public string ScopeChoicesSignature;
        public PaneFilter Filter;
        public bool ShowGrid;
        public bool Unified = true;
        public ProjectedGridContext FocusedProjected;
        public MyGuiControlGrid FocusedReal;
        public Action<MyGuiControlRadioButtonGroup> TypeChanged;
        public MyGuiControlSearchBox.TextChangedDelegate SearchChanged;
        public Action<MyGuiControlCheckbox> HideChanged;
        public List<(MyGuiControlRadioButton Button, Action<MyGuiControlRadioButton> Handler)> FilterHandlers = new();
    }

    private sealed class ScopeChoice
    {
        public MechanicalInventorySession Session;
        public InventoryProjectionView View;
        public string Label;
        public string Tooltip;
        public bool AccessedConstruct;
        public bool AccessedNetwork;
    }

    private readonly object vanillaController;
    private static readonly System.Reflection.FieldInfo RadioSelectionHandlers =
        AccessTools.Field(typeof(MyGuiControlRadioButton), "SelectedChanged");
    private readonly List<MechanicalInventorySession> sessions = new();
    private readonly Dictionary<MechanicalInventorySession, ScopeProfile> profiles = new();
    private readonly Dictionary<string, bool> vanillaActionVisibility = new(StringComparer.Ordinal);
    private readonly Pane left = new() { IsLeft = true };
    private readonly Pane right = new();
    private IMyGuiControlsParent controlsParent;
    private MyEntity user;
    private MyEntity interacted;
    private MyGuiControlGridDragAndDrop dragAndDrop;
    private DateTime refreshAfterUtc;
    private DateTime nextScopePollUtc;
    private bool dirty;
    private bool disposed;
    private readonly List<TransferOperationResult> rebalanceOperations = new();
    private readonly Stopwatch rebalanceElapsed = new();
    private bool rebalanceFeedbackShown;

    public UnifiedTerminalController(object vanillaController)
    {
        this.vanillaController = vanillaController;
    }

    public bool Active { get; private set; }

    public void Activate(IMyGuiControlsParent parent, MyEntity userEntity, MyEntity interactedEntity,
        bool unifiedLeft = true, bool unifiedRight = true)
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(UnifiedTerminalController));
        Deactivate();
        controlsParent = parent;
        user = userEntity;
        interacted = interactedEntity;
        left.Unified = unifiedLeft;
        right.Unified = unifiedRight;
        BindPane(left, "Left");
        BindPane(right, "Right");
        // Never reuse vanilla-owned grids: Close leaves their input delegates attached.
        left.List.InitControls(Array.Empty<MyGuiControlBase>());
        right.List.InitControls(Array.Empty<MyGuiControlBase>());
        left.ShowGrid = false;
        right.ShowGrid = true;
        left.TypeGroup.SelectByIndex(0);
        right.TypeGroup.SelectByIndex(1);
        DisableVanillaCenterActions();
        CreateDragAndDrop();
        CreateSessions();
        Plugin.Instance.Transfers.OperationFinished += TransferFinished;
        Active = true;
        Refresh();
    }

    public void SetUnified(bool isLeft, bool enabled)
    {
        var pane = isLeft ? left : right;
        if (pane.Unified == enabled)
            return;
        dragAndDrop?.Stop();
        pane.Unified = enabled;
        RebuildPane(pane);
    }

    public void Deactivate()
    {
        Plugin.Instance?.Transfers?.Cancel(rebalanceOperations);
        rebalanceOperations.Clear();
        if (Plugin.Instance?.Transfers != null)
            Plugin.Instance.Transfers.OperationFinished -= TransferFinished;
        foreach (var session in sessions)
        {
            session.Changed -= SessionChanged;
            session.Dispose();
        }
        sessions.Clear();
        profiles.Clear();
        left.List?.InitControls(Array.Empty<MyGuiControlBase>());
        right.List?.InitControls(Array.Empty<MyGuiControlBase>());
        if (dragAndDrop != null)
        {
            dragAndDrop.ItemDropped -= ItemDropped;
            controlsParent?.Controls.Remove(dragAndDrop);
            dragAndDrop = null;
        }
        RestoreVanillaCenterActions();
        ClearPane(left);
        ClearPane(right);
        Active = false;
        controlsParent = null;
        user = null;
        interacted = null;
    }

    public void Refresh()
    {
        if (!Active || dragAndDrop?.IsActive() == true)
            return;
        RebuildPane(left, contentsOnly: true);
        RebuildPane(right, contentsOnly: true);
        dirty = false;
    }

    public void UpdateBeforeDraw()
    {
        if (!Active)
            return;
        UpdateRebalanceFeedback();
        if (DateTime.UtcNow >= nextScopePollUtc)
        {
            nextScopePollUtc = DateTime.UtcNow.AddSeconds(1);
            PollScopes();
        }
        if (!dirty || DateTime.UtcNow < refreshAfterUtc || dragAndDrop.IsActive())
            return;
        try
        {
            Refresh();
        }
        catch (Exception exception)
        {
            dirty = false;
            throw new InvalidOperationException("Unified terminal refresh failed", exception);
        }
    }

    public MyGuiControlGrid GetDefaultFocus() =>
        left.FocusedReal ?? left.FocusedProjected?.Grid ??
        right.FocusedReal ?? right.FocusedProjected?.Grid;

    public void SetSearch(string text, bool interactedSide)
    {
        var pane = interactedSide ? right : left;
        if (pane.Search != null)
            pane.Search.SearchText = text ?? string.Empty;
        pane.FilterGroup?.SelectByIndex(0);
    }

    private void BindPane(Pane pane, string prefix)
    {
        pane.List = Get<MyGuiControlList>(prefix + "Inventory");
        pane.SuitButton = Get<MyGuiControlRadioButton>(prefix + "SuitButton");
        pane.GridButton = Get<MyGuiControlRadioButton>(prefix + "GridButton");
        // Clear() detaches a radio group but leaves its buttons selected. A new
        // group must reset them, otherwise clicking an already-selected stale
        // button raises no event after a vanilla/unified transition.
        ResetRadioButton(pane.SuitButton);
        ResetRadioButton(pane.GridButton);
        pane.Search = Get<MyGuiControlSearchBox>("BlockSearch" + prefix);
        pane.Search.TextBox.SetToolTip("Filter this column by block or item name. Changes only the displayed results, not stored items.");
        pane.Search.Controls.GetControlByName("SearchBoxClear")?.SetToolTip("Clear this column's search filter. Keeps the selected layout, scope and stored items unchanged.");
        pane.HideEmpty = Get<MyGuiControlCheckbox>("CheckboxHideEmpty" + prefix);
        pane.HideEmpty.SetToolTip("Hide empty inventory sections in this column. Does not exclude them from transfers or automation.");
        pane.HideEmptyLabel = Get<MyGuiControlLabel>("LabelHideEmpty" + prefix);
        pane.ScopeSelector = new ScopeTreeCombobox(
            new Vector2(pane.IsLeft ? -0.46f : 0.0225f, -0.225f),
            new Vector2(0.437f, 0.035f))
        {
            Name = prefix + "UnifiedScope",
            Visible = false
        };
        pane.ScopeSelector.ItemSelected += () =>
        {
            var index = (int)pane.ScopeSelector.GetSelectedKey();
            if (index < 0 || index >= pane.ScopeChoices.Count)
                return;
            pane.SelectedScopeId = pane.ScopeChoices[index].View.Id;
            pane.List.SetScrollBarPage();
            RebuildPane(pane);
        };
        controlsParent.Controls.Add(pane.ScopeSelector);
        pane.TypeGroup = new MyGuiControlRadioButtonGroup();
        pane.TypeGroup.Add(pane.SuitButton);
        pane.TypeGroup.Add(pane.GridButton);
        pane.TypeChanged = _ =>
        {
            pane.ShowGrid = pane.TypeGroup.SelectedIndex == 1;
            ApplyPaneLayout(pane);
            RebuildPane(pane);
        };
        pane.TypeGroup.SelectedChanged += pane.TypeChanged;
        pane.FilterGroup = new MyGuiControlRadioButtonGroup();
        var filters = new[]
        {
            ("FilterAllButton", PaneFilter.All),
            ("FilterEnergyButton", PaneFilter.Energy),
            ("FilterShipButton", PaneFilter.Ship),
            ("FilterStorageButton", PaneFilter.Storage),
            ("FilterSystemButton", PaneFilter.System)
        };
        foreach (var (suffix, filter) in filters)
        {
            var button = Get<MyGuiControlRadioButton>(prefix + suffix);
            ResetRadioButton(button);
            if (filter == PaneFilter.System)
                pane.SystemFilterButton = button;
            pane.FilterGroup.Add(button);
            Action<MyGuiControlRadioButton> handler = selected =>
            {
                if (!selected.Selected)
                    return;
                pane.Filter = filter;
                RebuildPane(pane);
            };
            button.SelectedChanged += handler;
            pane.FilterHandlers.Add((button, handler));
        }
        pane.FilterGroup.SelectByIndex(0);
        pane.SearchChanged = _ => RebuildPane(pane);
        pane.Search.OnTextChanged += pane.SearchChanged;
        pane.HideChanged = _ => RebuildPane(pane);
        pane.HideEmpty.IsCheckedChanged += pane.HideChanged;
    }

    private void ResetRadioButton(MyGuiControlRadioButton button)
    {
        // Keen's Close() leaves its anonymous filter callbacks attached to reused
        // controls. Remove only that closed controller's handlers, not other plugins'.
        if (RadioSelectionHandlers.GetValue(button) is Delegate handlers)
            foreach (var handler in handlers.GetInvocationList())
                if (ReferenceEquals(handler.Target, vanillaController))
                    button.SelectedChanged -= (Action<MyGuiControlRadioButton>)handler;
        button.Selected = false;
    }

    private static void ApplyPaneLayout(Pane pane)
    {
        pane.Search.Visible = pane.ShowGrid;
        pane.HideEmpty.Visible = pane.ShowGrid;
        pane.HideEmptyLabel.Visible = pane.ShowGrid;
        if (pane.SystemFilterOverlay != null)
            pane.SystemFilterOverlay.Visible = pane.ShowGrid && pane.Unified;
        pane.SystemFilterButton?.SetToolTip(pane.Unified ? "Show gas and production systems" : "Show system inventories");
        foreach (var (button, _) in pane.FilterHandlers)
        {
            button.Enabled = pane.ShowGrid;
            button.Visible = pane.ShowGrid;
            if (button.VisualStyle == MyGuiControlRadioButtonStyleEnum.FilterShip)
                button.SetToolTip(pane.Unified ? "Show weapons, ship tools and safety systems" :
                    "Show inventories on the accessed ship's mechanical construct, excluding connector-docked ships.");
        }
        var scopeHeight = pane.Unified && pane.ShowGrid && pane.ScopeChoices.Count > 1 ? 0.045f : 0f;
        if (pane.ScopeSelector != null)
            pane.ScopeSelector.Visible = scopeHeight > 0;
        pane.List.Position = new Vector2(pane.IsLeft ? -0.46f : 0.4595f,
            pane.ShowGrid ? -0.227f + scopeHeight : -0.276f);
        pane.List.Size = pane.ShowGrid
            ? new Vector2(0.437f, 0.569f - scopeHeight)
            : new Vector2(0.437f, 0.618f);
    }

    private void ClearPane(Pane pane)
    {
        if (pane.ScopeSelector != null)
            controlsParent?.Controls.Remove(pane.ScopeSelector);
        pane.ScopeSelector = null;
        pane.ScopeChoices.Clear();
        pane.SelectedScopeId = null;
        pane.ScopeChoicesSignature = null;
        if (pane.Search != null && pane.SearchChanged != null)
            pane.Search.OnTextChanged -= pane.SearchChanged;
        if (pane.HideEmpty != null && pane.HideChanged != null)
            pane.HideEmpty.IsCheckedChanged -= pane.HideChanged;
        foreach (var (button, handler) in pane.FilterHandlers)
        {
            button.SelectedChanged -= handler;
            button.Selected = false;
        }
        pane.FilterHandlers.Clear();
        if (pane.TypeGroup != null && pane.TypeChanged != null)
            pane.TypeGroup.SelectedChanged -= pane.TypeChanged;
        pane.TypeGroup?.Clear();
        pane.FilterGroup?.Clear();
        if (pane.SuitButton != null)
            pane.SuitButton.Selected = false;
        if (pane.GridButton != null)
            pane.GridButton.Selected = false;
        pane.List = null;
        pane.SuitButton = null;
        pane.GridButton = null;
        pane.TypeGroup = null;
        pane.FilterGroup = null;
        pane.Search = null;
        pane.HideEmpty = null;
        pane.HideEmptyLabel = null;
        if (pane.SystemFilterOverlay != null)
            controlsParent?.Controls.Remove(pane.SystemFilterOverlay);
        pane.SystemFilterButton = null;
        pane.SystemFilterOverlay = null;
        pane.FocusedProjected = null;
        pane.FocusedReal = null;
        pane.TypeChanged = null;
        pane.SearchChanged = null;
        pane.HideChanged = null;
    }

    private T Get<T>(string name) where T : MyGuiControlBase =>
        controlsParent.Controls.GetControlByName(name) as T ??
        throw new InvalidOperationException($"Terminal inventory control '{name}' is missing.");

    private void DisableVanillaCenterActions()
    {
        foreach (var name in new[]
                 {
                     "ThrowOutButton", "WithdrawButton", "DepositAllButton",
                     "AddToProductionButton", "SelectedToProductionButton"
                 })
        {
            var control = controlsParent.Controls.GetControlByName(name);
            if (control != null)
            {
                vanillaActionVisibility[name] = control.Visible;
                control.Visible = false;
            }
        }
    }

    private void RestoreVanillaCenterActions()
    {
        if (controlsParent != null)
            foreach (var pair in vanillaActionVisibility)
            {
                var control = controlsParent.Controls.GetControlByName(pair.Key);
                if (control != null)
                    control.Visible = pair.Value;
            }
        vanillaActionVisibility.Clear();
    }

    private void CreateDragAndDrop()
    {
        dragAndDrop = new MyGuiControlGridDragAndDrop(
            MyGuiConstants.DRAG_AND_DROP_BACKGROUND_COLOR,
            MyGuiConstants.DRAG_AND_DROP_TEXT_COLOR,
            0.7f,
            MyGuiConstants.DRAG_AND_DROP_TEXT_OFFSET,
            supportIcon: true)
        {
            DrawBackgroundTexture = false,
            Name = "UnifiedDragAndDrop"
        };
        dragAndDrop.ItemDropped += ItemDropped;
        controlsParent.Controls.Add(dragAndDrop);
    }

    private void CreateSessions()
    {
        var plugin = Plugin.Instance;
        var identity = MySession.Static?.LocalPlayerId ?? 0L;
        foreach (var anchor in plugin.InventoryScopes.GetConnectedMechanicalAnchors(interacted))
        {
            var session = new MechanicalInventorySession(plugin.InventoryScopes, interacted, identity, anchor);
            session.Changed += SessionChanged;
            sessions.Add(session);
            var scope = session.Refresh().Scope;
            profiles[session] = plugin.Profiles.GetOrCreate(ProfileIdentity.CurrentWorld, scope);
            plugin.Automation.Register(scope, profiles[session]);
        }
        ApplyGasSystemFilterIcon(left);
        ApplyGasSystemFilterIcon(right);
    }

    private void ApplyGasSystemFilterIcon(Pane pane)
    {
        if (pane.SystemFilterButton == null)
            return;
        if (pane.SystemFilterOverlay != null)
            controlsParent.Controls.Remove(pane.SystemFilterOverlay);
        pane.SystemFilterButton.VisualStyle = MyGuiControlRadioButtonStyleEnum.FilterSystem;
        pane.SystemFilterButton.Icon = null;
        pane.SystemFilterOverlay = new GasSystemFilterOverlay(pane.SystemFilterButton);
        controlsParent.Controls.Add(pane.SystemFilterOverlay);
        pane.SystemFilterButton.SetToolTip("Show gas systems");
    }

    private void PollScopes()
    {
        var scanner = Plugin.Instance.InventoryScopes;
        var desired = scanner.GetConnectedMechanicalAnchors(interacted)
            .Select(scanner.GetMechanicalGroupKey)
            .OrderBy(id => id)
            .ToArray();
        var current = sessions.Where(session => session.Scope != null)
            .Select(session => session.Scope.Grids.Min(grid => grid.EntityId))
            .OrderBy(id => id)
            .ToArray();
        if (!desired.SequenceEqual(current))
        {
            foreach (var session in sessions)
            {
                session.Changed -= SessionChanged;
                session.Dispose();
            }
            sessions.Clear();
            profiles.Clear();
            CreateSessions();
            SessionChanged();
            return;
        }
        foreach (var session in sessions)
            session.PollStructure();
    }

    private void SessionChanged()
    {
        if (dirty) return;
        dirty = true;
        refreshAfterUtc = DateTime.UtcNow.AddMilliseconds(
            Math.Min(50, Math.Max(0, Config.Current.RefreshDebounceMilliseconds)));
    }

    private void RebuildPane(Pane pane, bool contentsOnly = false)
    {
        if (!Active && controlsParent == null)
            return;
        if (!pane.ShowGrid)
        {
            ApplyPaneLayout(pane);
            var entity = pane.IsLeft ? user : interacted;
            RebuildRealPane(pane, entity?.HasInventory == true ? new[] { entity } : Array.Empty<MyEntity>());
            return;
        }

        var search = pane.Search?.SearchText ?? string.Empty;
        var started = Stopwatch.StartNew();
        var ownerControls = new List<MyGuiControlBase>();
        var gridCount = 0;
        var stackCount = 0;
        if (!pane.Unified)
        {
            ApplyPaneLayout(pane);
            var accessedGrid = (interacted as MyCubeBlock)?.CubeGrid ?? interacted as MyCubeGrid ?? interacted?.Parent as MyCubeGrid;
            var members = sessions.Where(session => pane.Filter != PaneFilter.Ship || session.Scope.Grids.Contains(accessedGrid))
                .SelectMany(session => session.Refresh().Roles)
                .SelectMany(role => role.Members).Select(member => (MyEntity)member.Owner)
                .Where(owner => NativeOwnerMatchesFilter(owner, pane.Filter))
                .Distinct().OrderByDescending(owner => owner == interacted)
                .ThenBy(owner => owner.DisplayNameText, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(owner => owner.EntityId);
            RebuildRealPane(pane, members.Where(owner => RealOwnerVisible(pane, owner, search)));
            return;
        }
        var choice = UpdateScopeSelector(pane);
        if (choice != null)
        {
            var session = choice.Session;
            var profile = profiles[session];
            var view = choice.View;
            {
                var projection = InventoryDisplayOrder.Apply(view.Projection, profile, view.Id, interacted?.EntityId ?? 0);
                if (projection.Roles.Any(role => role.Members.Any(member => member.Owner is Sandbox.Game.Entities.Cube.MyRefinery)))
                    projection = ProjectionOrdering.ApplyRefineryPriority(
                        projection,
                        RefineryPriorityEngine.Build(session.Scope, profile, GetFlags));
                bool Visible(InventoryRoleProjection role) => RoleVisible(role, pane.Filter) &&
                    (!pane.HideEmpty.IsChecked || role.Stacks.Count > 0);
                var existing = pane.List.Controls.OfType<UnifiedInventoryOwnerControl>().FirstOrDefault();
                if (contentsOnly && existing != null && existing.Session == session && existing.ViewId == view.Id &&
                    existing.TryRefresh(projection, search, Visible, GetFlags))
                    return;
                pane.FocusedProjected = null;
                pane.FocusedReal = null;
                UnifiedInventoryOwnerControl owner = null;
                owner = new UnifiedInventoryOwnerControl(
                    session,
                    view.Id,
                    projection,
                    profile.Policy,
                    search,
                    Visible,
                    GetFlags,
                    (_, policy) =>
                    {
                        profile.Policy = policy;
                        Plugin.Instance.Profiles.Save();
                    },
                    (context, args) =>
                    {
                        pane.FocusedProjected = context;
                        StartDragging(context.Grid, args);
                    },
                    (context, args) =>
                    {
                        pane.FocusedProjected = context;
                        ProjectedItemDoubleClicked(pane, context, args);
                    },
                    roles => Rebalance(session, roles, view.Id),
                    roles => MyGuiSandbox.AddScreen(new MemberManagementScreen(session, roles, profile)),
                    section => ConfigureSection(session, owner.Projection, section),
                    section => RunUtility(session, owner.Projection, section),
                    () => MyGuiSandbox.AddScreen(new InventoryGroupsScreen(session, profile)),
                    () => MyGuiSandbox.AddScreen(new LoadoutScreen(session, owner.Projection, profile, default, GetFlags, plan => Queue(plan))));
                foreach (var grid in owner.Grids)
                {
                    grid.ItemSelected += (_, _) => pane.FocusedProjected = grid.UserData as ProjectedGridContext;
                    grid.ItemControllerAction = (sender, index, action, pressed) =>
                        GamepadTransfer(pane, sender, index, action, pressed);
                    grid.GamepadHelpText = "A: transfer amount";
                }
                gridCount += owner.Grids.Count;
                stackCount += projection.Roles.Sum(role => role.Stacks.Count);
                ownerControls.Add(owner);
                pane.FocusedProjected ??= owner.Grids.FirstOrDefault()?.UserData as ProjectedGridContext;
            }
        }
        if (ownerControls.Count == 0)
        {
            pane.FocusedProjected = null;
            pane.FocusedReal = null;
        }
        pane.List.InitControls(ownerControls);
        started.Stop();
        Plugin.Instance.Log.Debug(
            "Unified {0} pane rebuilt: {1} views, {2} grids, {3} projected stacks in {4:F2} ms",
            pane.IsLeft ? "left" : "right",
            ownerControls.Count,
            gridCount,
            stackCount,
            started.Elapsed.TotalMilliseconds);
    }

    private void RebuildRealPane(Pane pane, IEnumerable<MyEntity> entities)
    {
        var owners = entities.ToArray();
        // Native owners subscribe to inventory changes; keep their controls and selection stable.
        var existing = pane.List.Controls.OfType<MyGuiControlInventoryOwner>().ToArray();
        if (existing.Length == pane.List.Controls.Count &&
            existing.Select(owner => owner.InventoryOwner).SequenceEqual(owners))
            return;
        var focusedInventory = pane.FocusedReal?.UserData as MyInventory;
        pane.FocusedProjected = null;
        pane.FocusedReal = null;
        var controls = new List<MyGuiControlBase>();
        foreach (var entity in owners)
        {
            var owner = new MyGuiControlInventoryOwner(entity, Vector4.One)
            {
                Size = new Vector2(pane.List.Size.X - 0.05f, 0.1f),
                OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER
            };
            foreach (var grid in owner.ContentGrids)
            {
                grid.ItemDragged += (sender, args) => StartDragging(sender, args);
                grid.ItemDoubleClicked += (sender, args) => RealItemDoubleClicked(pane, sender, args);
                grid.ItemSelected += (sender, _) => pane.FocusedReal = sender;
                grid.FocusChanged += (_, focused) => { if (focused) pane.FocusedReal = grid; };
                grid.ItemControllerAction = (sender, index, action, pressed) =>
                    GamepadTransfer(pane, sender, index, action, pressed);
                grid.GamepadHelpText = "A: transfer amount";
                if (pane.FocusedReal == null || ReferenceEquals(grid.UserData, focusedInventory))
                    pane.FocusedReal = grid;
            }
            controls.Add(owner);
        }
        pane.List.InitControls(controls);
    }

    private static bool RealOwnerVisible(Pane pane, MyEntity owner, string search)
    {
        var inventories = Enumerable.Range(0, owner.InventoryCount)
            .Select(index => owner.GetInventory(index)).ToArray();
        if (pane.HideEmpty.IsChecked && inventories.All(inventory => inventory.GetItems().Count == 0))
            return false;
        var words = search.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        bool Matches(string text) => words.All(word =>
            (text ?? string.Empty).IndexOf(word, StringComparison.CurrentCultureIgnoreCase) >= 0);
        return Matches(owner.DisplayNameText) || inventories.Any(inventory => inventory.GetItems().Any(item =>
            Matches(Sandbox.Definitions.MyDefinitionManager.Static.GetPhysicalItemDefinition(item.Content)?.DisplayNameText)));
    }

    private ScopeChoice UpdateScopeSelector(Pane pane)
    {
        var choices = new List<ScopeChoice>();
        var accessedGrid = (interacted as MyCubeBlock)?.CubeGrid ?? interacted as MyCubeGrid ?? interacted?.Parent as MyCubeGrid;
        foreach (var session in sessions)
        {
            var snapshot = session.Refresh();
            var projection = pane.Unified ? InventoryGroups.Build(snapshot, profiles[session]) : snapshot;
            var mechanical = ProjectionViewBuilder.Build(session, projection, InventoryScopeMode.MechanicalGroups)[0];
            var accessed = session.Scope.Grids.Contains(accessedGrid);
            var gridId = session.Scope.Grids.Min(grid => grid.EntityId);
            var name = session.Scope.AnchorGrid.DisplayName;
            var duplicate = sessions.Count(other => other.Scope.AnchorGrid.DisplayName == name) > 1;
            var shipNumber = sessions.OrderBy(other => other.Scope.Grids.Min(grid => grid.EntityId)).ToList().IndexOf(session) + 1;
            void Add(InventoryProjectionView view, string detail, bool network = false)
            {
                var members = view.Projection.Roles.SelectMany(role => role.Members)
                    .GroupBy(member => member.OwnerEntityId).Select(group => group.First()).ToArray();
                var atHatch = network && members.Any(member => member.OwnerEntityId == interacted?.EntityId);
                choices.Add(new ScopeChoice
                {
                    Session = session,
                    View = view,
                    Label = view == mechanical
                        ? name + (duplicate ? $" · Ship {shipNumber}" : "") + (accessed ? " [Local]" : "")
                        : detail + (network ? $" ({members.Length} blocks)" : "") + (atHatch ? " [Accessed]" : ""),
                    Tooltip = $"{name}\nConstruct ID: {gridId}\n{detail}: {members.Length} inventory blocks\n" +
                              (atHatch ? "Contains the accessed hatch.\n" : "") +
                              "Network grouping is not a transfer guarantee: access, sorters and tube sizes still apply.",
                    AccessedConstruct = accessed,
                    AccessedNetwork = atHatch
                });
            }
            Add(mechanical, "Whole construct");
            var networks = ProjectionViewBuilder.Build(session, projection, InventoryScopeMode.ConveyorComponents);
            if (networks.Count > 1)
                foreach (var network in networks)
                    Add(network, network.Name, network: true);
            if (Config.Current.ScopeMode == InventoryScopeMode.BlockGroups)
                foreach (var group in ProjectionViewBuilder.Build(session, projection, InventoryScopeMode.BlockGroups)
                             .Where(group => group.Id != mechanical.Id))
                    Add(group, "Group · " + group.Name);
        }
        choices = choices.OrderByDescending(choice => choice.AccessedConstruct)
            .ThenBy(choice => choice.Session.Scope.Grids.Min(grid => grid.EntityId)).ToList();
        foreach (var shipChoices in choices.GroupBy(choice => choice.Session))
        {
            var children = shipChoices.Skip(1).ToArray();
            for (var index = 0; index < children.Length; index++)
                children[index].Label = (index == children.Length - 1 ? "  └─ " : "  ├─ ") + children[index].Label;
        }
        pane.ScopeChoices = choices;
        var selected = choices.FindIndex(choice => choice.View.Id == pane.SelectedScopeId);
        if (selected < 0)
        {
            selected = choices.FindIndex(choice => choice.AccessedNetwork);
            if (selected < 0)
                selected = 0;
            pane.SelectedScopeId = choices.ElementAtOrDefault(selected)?.View.Id;
            pane.List.SetScrollBarPage();
        }
        var signature = string.Join("\n", choices.Select(choice => choice.View.Id + ":" + choice.Label));
        if (signature != pane.ScopeChoicesSignature)
        {
            pane.ScopeChoicesSignature = signature;
            pane.ScopeSelector.ClearItems();
            for (var index = 0; index < choices.Count; index++)
                pane.ScopeSelector.AddTreeItem(index, choices[index].Label, toolTip: choices[index].Tooltip);
        }
        pane.ScopeSelector.SelectItemByKey(selected, sendEvent: false);
        var result = choices.ElementAtOrDefault(selected);
        pane.ScopeSelector.SetToolTip(result?.Tooltip ?? "No available inventories");
        ApplyPaneLayout(pane);
        return result;
    }

    private void StartDragging(MyGuiControlGrid grid, MyGuiControlGrid.EventArgs args)
    {
        if (args.ItemIndex < 0 || !grid.IsValidIndex(args.ItemIndex))
            return;
        var item = grid.GetItemAt(args.ItemIndex);
        if (item == null)
            return;
        dragAndDrop.StartDragging(
            MyDropHandleType.MouseRelease,
            args.Button,
            item,
            new MyDragAndDropInfo { Grid = grid, ItemIndex = args.ItemIndex },
            includeTooltip: false);
    }

    private void ItemDropped(object sender, MyDragAndDropEventArgs args)
    {
        if (args.DragFrom?.Grid == null || args.DropTo?.Grid == null)
            return;
        if (ReferenceEquals(args.DragFrom.Grid, args.DropTo.Grid) &&
            args.DragFrom.Grid.UserData is ProjectedGridContext context)
        {
            if (InventoryDisplayOrder.IsPriorityDriven(context.Role))
                MyAPIGateway.Utilities?.ShowNotification("Refinery input order is controlled by Ore Priority.", 3000);
            else
                InventoryDisplayOrder.Move(profiles[context.Owner.Session], context.Owner.ViewId, context.Role,
                    args.DragFrom.Grid.GetItemAt(args.DragFrom.ItemIndex)?.UserData as ProjectedInventoryStack,
                    args.DropTo.Grid.IsValidIndex(args.DropTo.ItemIndex)
                        ? args.DropTo.Grid.GetItemAt(args.DropTo.ItemIndex)?.UserData as ProjectedInventoryStack : null);
            SessionChanged();
            Refresh();
            return;
        }
        var amount = GetAmount(args.DragFrom.Grid, args.DragFrom.ItemIndex);
        if (args.DragButton == MySharedButtonsEnum.Secondary)
            ShowAmountDialog(amount, GetDefinition(args.DragFrom.Grid, args.DragFrom.ItemIndex), value =>
                ExecuteTransfer(args.DragFrom.Grid, args.DragFrom.ItemIndex, args.DropTo.Grid, value, args.DropTo.ItemIndex));
        else
            ExecuteTransfer(args.DragFrom.Grid, args.DragFrom.ItemIndex, args.DropTo.Grid, amount, args.DropTo.ItemIndex);
    }

    private void ExecuteTransfer(
        MyGuiControlGrid sourceGrid,
        int sourceIndex,
        MyGuiControlGrid destinationGrid,
        MyFixedPoint requestedAmount,
        int destinationIndex = -1)
    {
        var item = sourceGrid.GetItemAt(sourceIndex);
        if (item?.UserData is ProjectedInventoryStack projected)
        {
            if (destinationGrid.UserData is MyInventory realDestination)
            {
                if (TryCompanionTransfer(projected, sourceGrid.UserData as ProjectedGridContext,
                        null, default, realDestination, null, requestedAmount)) return;
                QueueProjected(TransferPlanFactory.Withdraw(projected, realDestination, requestedAmount, GetFlags),
                    sourceGrid.UserData as ProjectedGridContext);
                return;
            }
            if (destinationGrid.UserData is ProjectedGridContext projectedDestination)
            {
                if (TryCompanionTransfer(projected, sourceGrid.UserData as ProjectedGridContext,
                        null, default, null, projectedDestination, requestedAmount)) return;
                QueueProjected(TransferPlanFactory.BetweenScopes(
                    projected,
                    requestedAmount,
                    Destinations(projectedDestination, projected.DefinitionId),
                    profiles[projectedDestination.Owner.Session].Policy,
                    GetFlags), sourceGrid.UserData as ProjectedGridContext, projectedDestination);
            }
            return;
        }

        if (item?.UserData is not MyPhysicalInventoryItem realItem ||
            sourceGrid.UserData is not MyInventory realSource)
            return;
        if (destinationGrid.UserData is MyInventory physicalDestination)
        {
            if (ReferenceEquals(realSource, physicalDestination))
            {
                // Same-inventory rearrangement has no quantity delta to acknowledge.
                var current = realSource.GetItemByID(realItem.ItemId);
                if (realSource.Owner == null || realSource.Owner.Closed || !current.HasValue ||
                    realSource.Owner is Sandbox.Game.Entities.Cube.MyTerminalBlock block &&
                    !block.HasPlayerAccess(MySession.Static?.LocalPlayerId ?? 0L))
                    return;
                var amount = TransferPlanner.Normalize(current.Value.Content.GetObjectId(),
                    MyFixedPoint.Min(requestedAmount, current.Value.Amount));
                if (amount > MyFixedPoint.Zero && destinationIndex >= 0)
                    MyInventory.TransferByUser(realSource, realSource, realItem.ItemId, destinationIndex, amount);
                return;
            }
            // Concrete-to-concrete moves use vanilla requests; companion intents require a projected side.
            Queue(new TransferPlan(realItem.Content.GetObjectId(), requestedAmount, new[]
            {
                new PhysicalTransferAllocation(new InventoryStackReference(realSource, realItem),
                    physicalDestination, requestedAmount)
            }));
            return;
        }
        if (destinationGrid.UserData is ProjectedGridContext destination)
        {
            if (TryCompanionTransfer(null, null, realSource, realItem, null, destination, requestedAmount)) return;
            QueueProjected(TransferPlanFactory.Deposit(
                realSource,
                realItem,
                requestedAmount,
                Destinations(destination, realItem.Content.GetObjectId()),
                profiles[destination.Owner.Session].Policy,
                GetFlags), destination);
        }
    }

    private bool GamepadTransfer(
        Pane sourcePane,
        MyGuiControlGrid sourceGrid,
        int index,
        MyGridItemAction action,
        bool pressed)
    {
        if (action != MyGridItemAction.Button_A || !pressed || !sourceGrid.IsValidIndex(index))
            return false;
        var targetPane = sourcePane.IsLeft ? right : left;
        var destination = targetPane.ShowGrid && targetPane.Unified
            ? targetPane.FocusedProjected?.Grid
            : targetPane.FocusedReal;
        if (destination == null)
            return false;
        var max = GetAmount(sourceGrid, index);
        ShowAmountDialog(max, GetDefinition(sourceGrid, index),
            amount => ExecuteTransfer(sourceGrid, index, destination, amount));
        return true;
    }

    private static MyFixedPoint GetAmount(MyGuiControlGrid grid, int index)
    {
        var data = grid.GetItemAt(index)?.UserData;
        return data switch
        {
            ProjectedInventoryStack projected => projected.Amount,
            MyPhysicalInventoryItem item => item.Amount,
            _ => MyFixedPoint.Zero
        };
    }

    private static MyDefinitionId GetDefinition(MyGuiControlGrid grid, int index)
    {
        var data = grid.GetItemAt(index)?.UserData;
        return data switch
        {
            ProjectedInventoryStack projected => projected.DefinitionId,
            MyPhysicalInventoryItem item => item.Content.GetObjectId(),
            _ => default
        };
    }

    private static void ShowAmountDialog(
        MyFixedPoint maximum,
        MyDefinitionId definition,
        Action<MyFixedPoint> confirmed)
    {
        if (maximum <= MyFixedPoint.Zero)
            return;
        var dialog = new MyGuiScreenDialogAmount(
            0f,
            (float)maximum,
            MyCommonTexts.DialogAmount_AddAmountCaption,
            definition.TypeId == typeof(MyObjectBuilder_Ore) || definition.TypeId == typeof(MyObjectBuilder_Ingot) ? 3 : 0,
            definition.TypeId != typeof(MyObjectBuilder_Ore) && definition.TypeId != typeof(MyObjectBuilder_Ingot),
            null,
            MySandboxGame.Config.UIBkOpacity,
            MySandboxGame.Config.UIOpacity);
        dialog.OnConfirmed += amount =>
        {
            if (amount > 0)
                confirmed((MyFixedPoint)amount);
        };
        MyGuiSandbox.AddScreen(dialog);
    }

    private void ProjectedItemDoubleClicked(
        Pane sourcePane,
        ProjectedGridContext source,
        MyGuiControlGrid.EventArgs args)
    {
        var targetPane = sourcePane.IsLeft ? right : left;
        if (!source.Grid.IsValidIndex(args.ItemIndex) ||
            source.Grid.GetItemAt(args.ItemIndex)?.UserData is not ProjectedInventoryStack projected)
            return;
        if ((!targetPane.ShowGrid || !targetPane.Unified) && targetPane.FocusedReal?.UserData is MyInventory destination)
        {
            if (TryCompanionTransfer(projected, source, null, default, destination, null, projected.Amount)) return;
            QueueProjected(TransferPlanFactory.Withdraw(projected, destination, projected.Amount, GetFlags), source);
            return;
        }
        var target = targetPane.FocusedProjected;
        if (target != null && TryCompanionTransfer(projected, source, null, default, null, target, projected.Amount)) return;
        if (target != null)
            QueueProjected(TransferPlanFactory.BetweenScopes(
                projected,
                projected.Amount,
                Destinations(target, projected.DefinitionId),
                profiles[target.Owner.Session].Policy,
                GetFlags), source, target);
    }

    private void RealItemDoubleClicked(Pane sourcePane, MyGuiControlGrid grid, MyGuiControlGrid.EventArgs args)
    {
        var targetPane = sourcePane.IsLeft ? right : left;
        var target = targetPane.ShowGrid && targetPane.Unified
            ? targetPane.FocusedProjected?.Grid : targetPane.FocusedReal;
        if (target != null && grid.IsValidIndex(args.ItemIndex))
            ExecuteTransfer(grid, args.ItemIndex, target, GetAmount(grid, args.ItemIndex));
    }

    private void Rebalance(
        MechanicalInventorySession session,
        IReadOnlyList<InventoryRoleProjection> roles, string viewId)
    {
        try
        {
            if (CompanionActions.TryRun(session.Scope, profiles[session], Shared.Companion.ShipAction.Rebalance,
                roles.Select(role => Selection(session.Scope, role, viewId)).ToList(),
                canContinue: () => Active && !disposed)) return;
            RebalanceSection(session, roles);
        }
        catch (Exception exception)
        {
            Plugin.Instance.Transfers.Cancel(rebalanceOperations);
            Plugin.Instance.Log.Error(exception, "Unified Storage rebalance failed");
            Sandbox.ModAPI.MyAPIGateway.Utilities?.ShowNotification(
                "Unified Storage: rebalance failed. See the log for details.", 5000, "Red");
        }
    }

    private void RebalanceSection(
        MechanicalInventorySession session,
        IReadOnlyList<InventoryRoleProjection> roles)
    {
        if (Plugin.Instance.Transfers.PendingCount != 0)
        {
            MyAPIGateway.Utilities?.ShowNotification("Unified Storage: wait for the current transfer to finish.", 3000);
            return;
        }
        var profile = profiles[session];
        var plans = roles.SelectMany(role => TransferPlanFactory.Rebalance(role, profile.Policy, GetFlags))
            .Where(plan => plan.PlannedAmount > MyFixedPoint.Zero).ToArray();
        var sortRefineries = roles.Any(role => role.Members.Any(member => member.Owner is Sandbox.Game.Entities.Cube.MyRefinery));
        var guard = InventoryGroups.Guard(session.Scope, profile, roles.Select(role => role.Section.GroupId));
        rebalanceOperations.Clear();
        for (var index = 0; index < plans.Length; index++)
        {
            var isLast = index == plans.Length - 1;
            var operation = Queue(plans[index], () => Active && guard(), "window closed or group membership changed; reapply rebalance",
                sortRefineries && isLast ? result =>
                {
                    if (Active && result.Status == TransferOperationStatus.Complete &&
                        rebalanceOperations.All(item => item.Status == TransferOperationStatus.Complete))
                        SortRefineries(session.Scope, profile, roles.SelectMany(role => role.Members));
                } : null);
            operation.Quiet = true;
            rebalanceOperations.Add(operation);
        }
        rebalanceFeedbackShown = false;
        rebalanceElapsed.Restart();
        if (rebalanceOperations.Count == 0)
            MyAPIGateway.Utilities?.ShowNotification("Unified Storage: already balanced; no transfers needed.", 3000);
        if (sortRefineries && plans.Length == 0)
            SortRefineries(session.Scope, profile, roles.SelectMany(role => role.Members));
    }

    private void UpdateRebalanceFeedback()
    {
        if (rebalanceFeedbackShown || rebalanceOperations.Count == 0) return;
        if (rebalanceOperations.All(item => item.Status is not (TransferOperationStatus.Queued or TransferOperationStatus.Running)))
        {
            rebalanceFeedbackShown = true;
            var complete = rebalanceOperations.Count(item => item.Status == TransferOperationStatus.Complete);
            MyAPIGateway.Utilities?.ShowNotification(complete == rebalanceOperations.Count
                ? "Unified Storage: rebalance complete."
                : $"Unified Storage: {complete}/{rebalanceOperations.Count} balanced. " +
                  rebalanceOperations.First(item => item.Status != TransferOperationStatus.Complete).Message, 5000);
        }
        else if (rebalanceElapsed.Elapsed.TotalSeconds >= 5)
        {
            rebalanceFeedbackShown = true;
            MyGuiSandbox.AddScreen(new RebalanceJobScreen(rebalanceOperations.ToArray(), rebalanceElapsed));
        }
    }

    private void ConfigureSection(
        MechanicalInventorySession session,
        InventoryProjection projection,
        InventorySectionKey section)
    {
        var profile = profiles[session];
        if (section.Kind == InventorySectionKind.DefinitionFallback)
        {
            var members = projection.Roles.Where(role => role.Section.Equals(section)).SelectMany(role => role.Members).ToArray();
            var actions = new List<(string Label, Action Run)>
            {
                ("Group loadouts", () => MyGuiSandbox.AddScreen(new LoadoutScreen(session, projection, profile, section, GetFlags, plan => Queue(plan))))
            };
            if (members.Any(member => member.Owner is Sandbox.Game.Entities.Cube.MyRefinery))
                actions.Add(("Ship ore priority", () => MyGuiSandbox.AddScreen(new RefineryPriorityScreen(session, profile, GetFlags, () => SortRefineries(session)))));
            if (members.Any(member => member.Owner is Sandbox.Game.Entities.Cube.MyAssembler))
                actions.Add(("Crafting targets", () => MyGuiSandbox.AddScreen(new CraftingTargetsScreen(session, profile, GetFlags))));
            MyGuiSandbox.AddScreen(new InventoryGroupActionsScreen(actions));
            return;
        }
        switch (section.Kind)
        {
            case InventorySectionKind.Refineries:
                MyGuiSandbox.AddScreen(new RefineryPriorityScreen(session, profile, GetFlags,
                    () => SortRefineries(session)));
                break;
            case InventorySectionKind.Assemblers:
                MyGuiSandbox.AddScreen(new CraftingTargetsScreen(session, profile, GetFlags));
                break;
            default:
                MyGuiSandbox.AddScreen(new LoadoutScreen(session, projection, profile, section, GetFlags,
                    plan => Queue(plan)));
                break;
        }
    }

    private void RunUtility(
        MechanicalInventorySession session,
        InventoryProjection projection,
        InventorySectionKey section)
    {
        var profile = profiles[session];
        // Utilities are explicitly ship-wide, not actions on possibly overlapping display rows.
        projection = session.Refresh();
        if (section.Kind == InventorySectionKind.Refineries)
        {
            // Uses bounded vanilla requests, including with older companions that lack this utility.
            foreach (var plan in DrainRefineryEngine.Plan(projection, profile,
                         descriptor => profile.GetFlags(descriptor.OwnerEntityId, descriptor.InventoryIndex)))
                Queue(plan);
            return;
        }
        if (section.Kind == InventorySectionKind.Assemblers &&
            CompanionActions.TryRun(session.Scope, profile, Shared.Companion.ShipAction.DrainAssemblers)) return;
        if (section.Kind == InventorySectionKind.Assemblers)
        {
            foreach (var operation in DrainAssemblerEngine.Plan(projection, profile, GetFlags))
                Queue(
                    operation.Plan,
                    () => operation.CanContinue,
                    "assembler is no longer idle in assembly mode");
        }
    }

    private void SortRefineries(MechanicalInventorySession session)
    {
        if (!CompanionActions.TryRun(session.Scope, profiles[session], Shared.Companion.ShipAction.SortRefineries))
            SortRefineries(session.Scope, profiles[session]);
    }

    private static void SortRefineries(MechanicalInventoryScope scope, ScopeProfile profile,
        IEnumerable<InventoryDescriptor> selected = null)
    {
        InventoryManagementFlags Flags(InventoryDescriptor descriptor) =>
            profile.GetFlags(descriptor.OwnerEntityId, descriptor.InventoryIndex);
        var model = RefineryPriorityEngine.Build(scope, profile, Flags);
        foreach (var refinery in (selected ?? scope.Inventories).Select(descriptor => descriptor.Owner)
                     .OfType<Sandbox.Game.Entities.Cube.MyRefinery>().Distinct()
                     .Where(refinery => !RefineryPriorityEngine.IsExcludedFromSorting(
                         refinery, scope, Flags)))
            Plugin.Instance.RefinerySorts.Enqueue(refinery,
                RefineryPriorityEngine.ForRefinery(model, refinery),
                MySession.Static?.LocalPlayerId ?? 0L,
                () => !RefineryPriorityEngine.IsExcludedFromSorting(refinery, scope, Flags));
    }

    private void Queue(TransferPlan plan)
        => Queue(plan, null, null);

    private static IEnumerable<InventoryDescriptor> Destinations(ProjectedGridContext context, MyDefinitionId item) =>
        context.Role.Members.Where(member => context.Role.Accepts(member, item));

    private void QueueProjected(TransferPlan plan, params ProjectedGridContext[] contexts)
    {
        var guards = contexts.Where(context => context != null).Select(context =>
            InventoryGroups.Guard(context.Owner.Session.Scope, profiles[context.Owner.Session],
                new[] { context.Role.Section.GroupId })).ToArray();
        Queue(plan, () => guards.All(guard => guard()), "group membership changed; retry transfer");
        if (plan.PlannedAmount > MyFixedPoint.Zero)
            MyAPIGateway.Utilities?.ShowNotification("Unified Storage: transfer pending…", 750);
    }

    private TransferOperationResult Queue(
        TransferPlan plan,
        Func<bool> canContinue,
        string guardFailureMessage,
        Action<TransferOperationResult> completed = null)
    {
        if (plan.PlannedAmount <= MyFixedPoint.Zero)
            return null;
        // Retain profile objects, not the UI controller's session dictionary: jobs can
        // outlive the terminal, and exclusion edits must still take effect immediately.
        var flagProfiles = plan.Allocations.SelectMany(allocation => new[]
            { allocation.Source.Descriptor, allocation.DestinationDescriptor })
            .Where(descriptor => descriptor != null).Distinct().ToDictionary(descriptor => descriptor,
                descriptor => profiles.FirstOrDefault(pair => pair.Key.Scope.Grids.Any(grid =>
                    grid.EntityId == descriptor.Owner.CubeGrid.EntityId)).Value);
        InventoryManagementFlags Flags(InventoryDescriptor descriptor) =>
            flagProfiles.TryGetValue(descriptor, out var profile) && profile != null
                ? profile.GetFlags(descriptor.OwnerEntityId, descriptor.InventoryIndex)
                : InventoryManagementFlags.None;
        return Plugin.Instance.Transfers.Enqueue(
            plan,
            interacted,
            MySession.Static?.LocalPlayerId ?? 0L,
            Flags,
            canContinue,
            guardFailureMessage,
            completed);
    }

    private InventoryManagementFlags GetFlags(InventoryDescriptor descriptor)
    {
        foreach (var pair in profiles)
        {
            if (pair.Key.Scope?.Grids.Any(grid => grid.EntityId == descriptor.Owner.CubeGrid.EntityId) == true)
                return pair.Value.GetFlags(descriptor.OwnerEntityId, descriptor.InventoryIndex);
        }
        return InventoryManagementFlags.None;
    }

#pragma warning disable CS0618 // Required only as the documented safe display-filter fallback for unknown blocks.
    private static bool NativeOwnerMatchesFilter(MyEntity owner, PaneFilter filter) =>
        filter is PaneFilter.All or PaneFilter.Ship || owner.InventoryOwnerType() == FilterOwnerType(filter);

    private static bool RoleVisible(InventoryRoleProjection role, PaneFilter filter)
    {
        if (filter == PaneFilter.All)
            return true;
        return role.Members.Any(member => member.Section.Kind switch
        {
            InventorySectionKind.UnifiedCargo or InventorySectionKind.Connectors => filter == PaneFilter.Storage,
            InventorySectionKind.PowerProducers => filter == PaneFilter.Energy,
            InventorySectionKind.Weapons or InventorySectionKind.ShipTools or InventorySectionKind.SafetySystems =>
                filter == PaneFilter.Ship,
            InventorySectionKind.Refineries or InventorySectionKind.Assemblers or InventorySectionKind.GasSystems =>
                filter == PaneFilter.System,
            InventorySectionKind.DefinitionFallback => member.Owner.InventoryOwnerType() == FilterOwnerType(filter),
            _ => false
        });
    }

    private static MyInventoryOwnerTypeEnum FilterOwnerType(PaneFilter filter) => filter switch
    {
        PaneFilter.Energy => MyInventoryOwnerTypeEnum.Energy,
        PaneFilter.Storage => MyInventoryOwnerTypeEnum.Storage,
        PaneFilter.System => MyInventoryOwnerTypeEnum.System,
        _ => MyInventoryOwnerTypeEnum.Storage
    };
#pragma warning restore CS0618

    private void TransferFinished(TransferOperationResult result)
    {
        Plugin.Instance.Log.Info("Unified transfer {0}: {1}", result.Status, result.Message);
        if (!result.Quiet && result.Status != TransferOperationStatus.Complete)
            Sandbox.ModAPI.MyAPIGateway.Utilities?.ShowNotification(
                "Unified Storage: " + result.Message, 3500, "Red");
        SessionChanged();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        Deactivate();
        disposed = true;
    }
}
