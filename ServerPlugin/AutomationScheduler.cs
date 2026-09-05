using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PluginSdk.Logging;
using ClientPlugin.Inventory;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using VRage.Game.ModAPI;
using Shared.Companion;
using VRage.Game;
using VRage.Game.Entity;

namespace ServerPlugin;

internal sealed class AutomationScheduler : IDisposable
{
    private readonly ScopeProfileStore store;
    private readonly CompanionConfig config;
    private readonly AuthoritativeActions actions;
    private readonly Logger log;
    private readonly Dictionary<Guid, Watch> watches = new();
    private long next;
    private int cursor;

    public AutomationScheduler(ScopeProfileStore store, CompanionConfig config, AuthoritativeActions actions, Logger log)
    { this.store = store; this.config = config; this.actions = actions; this.log = log; }

    public AutomationManifest Manifest() => new()
    {
        // Persisted ownership remains a claim while an operator pauses execution. Clients must not take over.
        Claims = store.Profiles.Where(profile => profile.Automation != 0)
            .Select(profile => new AutomationClaim { Anchor = profile.AnchorEntityId, Modes = profile.Automation }).ToList()
    };

    public bool Ready(SharedScopeProfile profile) => watches.TryGetValue(profile.Id, out var watch) &&
        watch.Revision == profile.Revision && Stopwatch.GetTimestamp() >= watch.Ready;
    public AutomationStatus Status(SharedScopeProfile profile) => new()
    {
        Owned = profile.Automation,
        State = profile.Automation == 0 ? "Client-owned" : !config.Enabled ? "Operator paused" : !Ready(profile) ? "Handover delay or uncertain-result pause" :
            watches.TryGetValue(profile.Id, out var watch) ? watch.State : "Waiting for scope",
        LastResult = watches.TryGetValue(profile.Id, out var current) ? current.LastResult : null
    };

    public void Update()
    {
        var now = Stopwatch.GetTimestamp();
        if (now < next) return;
        next = now + Stopwatch.Frequency / 10;
        var profiles = store.Profiles.Where(profile => profile.Automation != 0).ToArray();
        foreach (var id in watches.Keys.Where(id => profiles.All(profile => profile.Id != id)).ToArray())
        { watches[id].Dispose(); watches.Remove(id); }
        if (profiles.Length == 0 || !store.Available) return;
        var profile = profiles[cursor++ % profiles.Length];
        if (cursor == int.MaxValue) cursor = 0;
        if (!watches.TryGetValue(profile.Id, out var watch) || watch.Revision != profile.Revision)
        {
            watch?.Dispose();
            // Longer than the heartbeat interval and all in-flight client acknowledgement deadlines.
            watches[profile.Id] = watch = new Watch(profile.Revision, now + Stopwatch.Frequency * 60);
        }
        if (now < watch.Ready || now < watch.Next) return;
        watch.Next = now + Stopwatch.Frequency * Math.Max(2, Math.Min(60, config.AutomationIntervalSeconds));
        if (!config.Enabled || !MyEntities.TryGetEntityById<MyCubeGrid>(profile.AnchorEntityId, out var anchor) ||
            anchor.MarkedForClose || !anchor.BigOwners.Contains(profile.OwnerIdentityId)) { watch.State = "Anchor unavailable, ownership lost, or operator paused"; watch.Detach(); return; }
        try
        {
            if (watch.Scope == null || now >= watch.Audit)
            {
                watch.Detach();
                var scope = ServerInventoryScope.Capture(anchor,
                    block => ServerInventoryScope.PrincipalAccess(block, profile.OwnerIdentityId, config.AutomationFactionAccess), config.InventoriesPerIntent);
                if (store.InScope(new HashSet<long>(scope.Grids.Select(grid => grid.EntityId))).Length != 1)
                { watch.State = "Conflicting profile anchors after merge"; return; }
                watch.Attach(scope);
                watch.Audit = now + Stopwatch.Frequency * 15;
            }
            if (watch.Dirty) watch.Remaining |= profile.Automation;
            watch.Dirty = false;
            if (watch.Remaining == 0) return;
            // Rotate services, so a busy refinery cannot starve production or loadouts.
            var types = new[] { ShipAction.SortRefineries, ShipAction.QueueComponents, ShipAction.ApplyLoadouts };
            for (var i = 0; i < types.Length; i++)
            {
                var action = types[watch.Service % types.Length]; watch.Service = (watch.Service + 1) % types.Length;
                var capability = ShipActionIntent.Capability(action);
                if ((watch.Remaining & capability) == 0) continue;
                watch.Remaining &= ~capability;
                if (!actions.Enabled(action)) { watch.State = "Service paused by operator"; continue; }
                var receipt = actions.Execute(anchor, null, 0, profile.OwnerIdentityId,
                    new ShipActionIntent { Action = action, Settings = profile.Settings }, maintained: true);
                watch.LastResult = receipt;
                watch.State = action + ": " + receipt.Failure;
                if (receipt.Failure == TransferFailure.UnknownOutcome)
                {
                    // Never repeat an uncertain native mutation automatically. Owner must revise/re-enable the profile.
                    watch.Ready = long.MaxValue;
                    log.Warning("Automation paused after uncertain mutation; revise profile to resume", new { profile.Id, action });
                }
                else if (receipt.Mutations > 0 || receipt.Failure == TransferFailure.WorkLimit) watch.Dirty = true;
                break;
            }
        }
        catch (Exception exception)
        {
            watch.Detach(); watch.Next = now + Stopwatch.Frequency * 30;
            watch.State = "Scope unavailable or work limit exceeded";
            log.Warning("Automation scope unavailable or exceeds work limit", new { profile.Id, reason = exception.GetType().Name });
        }
    }

    public void Dispose() { foreach (var watch in watches.Values) watch.Dispose(); watches.Clear(); }

    private sealed class Watch : IDisposable
    {
        public readonly long Revision;
        public long Ready, Next, Audit;
        public int Service;
        public bool Dirty = true;
        public CompanionCapabilities Remaining;
        public string State = "Waiting for dirty scope";
        public ActionReceipt LastResult;
        public MechanicalInventoryScope Scope;
        private readonly HashSet<MyGridTerminalSystem> terminals = new();
        public Watch(long revision, long ready) { Revision = revision; Ready = ready; }
        public void Attach(MechanicalInventoryScope scope)
        {
            Scope = scope; Dirty = true;
            foreach (var member in scope.Inventories) member.Inventory.ContentsChanged += Changed;
            foreach (var production in scope.Inventories.Select(member => member.Owner).OfType<MyProductionBlock>().Distinct()) production.QueueChanged += QueueChanged;
            foreach (var grid in scope.Grids)
            {
                grid.OnBlockOwnershipChanged += GridChanged;
                grid.OnGridSplit += SplitMerge;
                grid.OnGridMerge += SplitMerge;
                grid.OnConnectionChangeCompleted += Connected;
                if (grid.GridSystems?.TerminalSystem is { } terminal && terminals.Add(terminal))
                { terminal.GroupAdded += GroupChanged; terminal.GroupRemoved += GroupChanged; }
                if (grid.GridSystems?.ConveyorSystem is { } conveyor)
                { conveyor.BlockAdded += StructureChanged; conveyor.BlockRemoved += StructureChanged; }
            }
        }
        private void Changed(MyInventoryBase _) => Dirty = true;
        private void QueueChanged(MyProductionBlock _) => Dirty = true;
        private void StructureChanged(MyCubeBlock _) { Dirty = true; Audit = 0; }
        private void GridChanged(MyCubeGrid _) { Dirty = true; Audit = 0; }
        private void SplitMerge(MyCubeGrid _, MyCubeGrid __) { Dirty = true; Audit = 0; }
        private void Connected(MyCubeGrid _, GridLinkTypeEnum __) { Dirty = true; Audit = 0; }
        private void GroupChanged(MyBlockGroup _) { Dirty = true; Audit = 0; }
        public void Detach()
        {
            if (Scope == null) return;
            foreach (var member in Scope.Inventories) member.Inventory.ContentsChanged -= Changed;
            foreach (var production in Scope.Inventories.Select(member => member.Owner).OfType<MyProductionBlock>().Distinct()) production.QueueChanged -= QueueChanged;
            foreach (var grid in Scope.Grids)
            {
                grid.OnBlockOwnershipChanged -= GridChanged;
                grid.OnGridSplit -= SplitMerge;
                grid.OnGridMerge -= SplitMerge;
                grid.OnConnectionChangeCompleted -= Connected;
                if (grid.GridSystems?.ConveyorSystem is { } conveyor)
                { conveyor.BlockAdded -= StructureChanged; conveyor.BlockRemoved -= StructureChanged; }
            }
            foreach (var terminal in terminals)
            { terminal.GroupAdded -= GroupChanged; terminal.GroupRemoved -= GroupChanged; }
            terminals.Clear();
            Scope = null;
        }
        public void Dispose() => Detach();
    }
}
