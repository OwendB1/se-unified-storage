using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Inventory;
using ClientPlugin.Profiles;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage;

namespace ClientPlugin.Automation;

public sealed class LocalAutomationService : IDisposable
{
    private readonly MechanicalInventoryScopeScanner scanner;
    private readonly LocalProfileStore profiles;
    private readonly Dictionary<long, ScopeAutomation> scopes = new();
    private string worldId = string.Empty;
    private long nextUpdateTick;

    public LocalAutomationService(MechanicalInventoryScopeScanner scanner, LocalProfileStore profiles)
    {
        this.scanner = scanner;
        this.profiles = profiles;
    }

    public void Register(MechanicalInventoryScope scope, ScopeProfile profile)
    {
        if (scope == null || profile == null || scopes.ContainsKey(profile.ScopeAnchorEntityId))
            return;
        var anchor = scope.Grids.FirstOrDefault(grid => grid.EntityId == profile.ScopeAnchorEntityId) ?? scope.AnchorGrid;
        Register(anchor, profile);
    }

    public void Update(long tick)
    {
        if (MySession.Static == null)
        {
            Clear();
            worldId = string.Empty;
            return;
        }
        var currentWorld = ProfileIdentity.CurrentWorld;
        if (!string.Equals(worldId, currentWorld, StringComparison.Ordinal))
        {
            Clear();
            worldId = currentWorld;
            DiscoverProfiles();
        }
        if (tick < nextUpdateTick)
            return;
        nextUpdateTick = tick + 30;
        foreach (var pair in scopes.ToArray())
        {
            var automation = pair.Value;
            if (automation.Session.Scope?.AnchorGrid.Closed == true)
            {
                automation.Session.Changed -= automation.MarkDirty;
                automation.Session.Dispose();
                scopes.Remove(pair.Key);
                continue;
            }
            automation.Session.PollStructure();
            Update(automation);
        }
    }

    private void DiscoverProfiles()
    {
        foreach (var profile in profiles.Profiles.Where(profile =>
                     string.Equals(profile.WorldId, worldId, StringComparison.Ordinal)))
            if (MyEntities.TryGetEntityById<MyCubeGrid>(profile.ScopeAnchorEntityId, out var grid))
                Register(grid, profile);
    }

    private void Register(MyCubeGrid anchor, ScopeProfile profile)
    {
        if (anchor == null || anchor.Closed || scopes.ContainsKey(profile.ScopeAnchorEntityId))
            return;
        var session = new MechanicalInventorySession(
            scanner,
            anchor,
            MySession.Static.LocalPlayerId,
            anchor);
        var automation = new ScopeAutomation(session, profile);
        session.Changed += automation.MarkDirty;
        scopes.Add(profile.ScopeAnchorEntityId, automation);
    }

    private static void Update(ScopeAutomation automation)
    {
        if (automation.Session.Scope?.AnchorGrid.Closed == true)
            return;
        if (!automation.Dirty && DateTime.UtcNow < automation.NextPollUtc)
            return;
        automation.Dirty = false;
        automation.NextPollUtc = DateTime.UtcNow.AddSeconds(2);
        InventoryProjection projection;
        try
        {
            projection = automation.Session.Refresh();
        }
        catch (Exception exception)
        {
            Plugin.Instance.Log.Warning(exception, "Local automation scope refresh failed");
            return;
        }
        InventoryManagementFlags Flags(InventoryDescriptor descriptor) =>
            automation.Profile.GetFlags(descriptor.OwnerEntityId, descriptor.InventoryIndex);

        if (automation.Profile.RefineryPriority.AutoSortInputs)
        {
            var priority = RefineryPriorityEngine.Build(automation.Session.Scope, automation.Profile, Flags);
            foreach (var refinery in automation.Session.Scope.Inventories.Select(descriptor => descriptor.Owner)
                         .OfType<MyRefinery>().Distinct()
                         .Where(refinery => !RefineryPriorityEngine.IsExcludedFromSorting(
                             refinery, automation.Session.Scope, Flags)))
                Plugin.Instance.RefinerySorts.Enqueue(refinery,
                    RefineryPriorityEngine.ForRefinery(priority, refinery),
                    MySession.Static.LocalPlayerId,
                    () => automation.Profile.RefineryPriority.AutoSortInputs &&
                          !RefineryPriorityEngine.IsExcludedFromSorting(
                              refinery, automation.Session.Scope, Flags));
        }
        if (automation.Profile.MaintainComponentTargets && Plugin.Instance.ProductionQueue.PendingCount == 0)
        {
            var statuses = ComponentTargetEngine.Evaluate(automation.Session.Scope, automation.Profile, Flags);
            if (statuses.Any(status => status.Target > MyFixedPoint.Zero &&
                                      (decimal)(status.Stock + status.Queued) <
                                      (decimal)status.Target * automation.Profile.ComponentStartThreshold))
                Plugin.Instance.ProductionQueue.Enqueue(ComponentTargetEngine.PlanDeficits(statuses));
        }
        if (automation.Profile.Loadouts.Any(rule => rule.Maintain) && Plugin.Instance.Transfers.PendingCount == 0)
            foreach (var plan in LoadoutEngine.Plan(projection, automation.Profile, Flags, maintainedOnly: true))
                Plugin.Instance.Transfers.Enqueue(
                    plan,
                    automation.Session.Scope.AnchorGrid,
                    MySession.Static.LocalPlayerId,
                    Flags);
    }

    private void Clear()
    {
        foreach (var automation in scopes.Values)
        {
            automation.Session.Changed -= automation.MarkDirty;
            automation.Session.Dispose();
        }
        scopes.Clear();
    }

    public void Dispose() => Clear();

    private sealed class ScopeAutomation
    {
        public ScopeAutomation(MechanicalInventorySession session, ScopeProfile profile)
        {
            Session = session;
            Profile = profile;
            Dirty = true;
        }

        public MechanicalInventorySession Session { get; }
        public ScopeProfile Profile { get; }
        public bool Dirty { get; set; }
        public DateTime NextPollUtc { get; set; }
        public void MarkDirty() => Dirty = true;
    }
}
