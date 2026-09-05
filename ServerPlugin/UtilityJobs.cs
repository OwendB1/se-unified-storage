using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ClientPlugin.Automation;
using ClientPlugin.Inventory;
using ClientPlugin.Profiles;
using ClientPlugin.Transfers;
using Sandbox.Game;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Shared.Companion;
using VRage;

namespace ServerPlugin;

internal sealed class UtilityJobs
{
    private readonly CompanionConfig config;
    private readonly ScopeProfileStore store;
    private readonly ProfilePermissions permissions;
    private readonly TransferValidation validation;
    private readonly Dictionary<Guid, Job> jobs = new();
    private long next;
    private int cursor;
    public int ActiveCount => jobs.Values.Count(job => job.Receipt.State == UtilityJobState.Running);

    public UtilityJobs(CompanionConfig config, ScopeProfileStore store, ProfilePermissions permissions)
    { this.config = config; this.store = store; this.permissions = permissions; validation = new TransferValidation(permissions); }

    public ActionReceipt Start(ulong sender, long identity, MyCubeGrid anchor, MyTerminalBlock terminal, ShipActionIntent intent)
    {
        intent.Validate();
        if (!Enabled(intent.Action)) return new ActionReceipt { Failure = TransferFailure.PolicyDisabled };
        if (intent.Action != ShipAction.DrainAssemblers) throw new InvalidOperationException("Not a utility job.");
        if (jobs.Count >= 32 || jobs.Values.Any(job => job.Receipt.State == UtilityJobState.Running &&
            (job.Sender == sender || job.Scope.Grids.Any(grid => grid.EntityId == anchor.EntityId))))
            return new ActionReceipt { Failure = TransferFailure.WorkLimit, Detail = "A utility job is already active for this player or ship." };
        var scope = ServerInventoryScope.Capture(anchor, block => permissions.HasAccess(block, sender), config.InventoriesPerIntent);
        var shared = store.InScope(new HashSet<long>(scope.Grids.Select(grid => grid.EntityId)));
        if (shared.Length > 1) return new ActionReceipt { Failure = TransferFailure.ScopeChanged };
        var settings = ProfileCodec.Clone(intent.Settings);
        ServerInventoryScope.Restrict(settings, shared.SingleOrDefault()?.Settings);
        var job = new Job
        {
            Sender = sender, Identity = identity, Scope = scope, Terminal = terminal, Settings = settings, Action = intent.Action,
            Deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 120,
            Guard = InventoryGroups.Guard(scope, settings, settings.Groups.Select(group => group.Id))
        };
        InventoryManagementFlags Flags(InventoryDescriptor member) => ServerInventoryScope.Flags(settings, member);
        if (intent.Action == ShipAction.DrainAssemblers)
        {
            if (!PolicyEnabled(settings.Policy)) return new ActionReceipt { Failure = TransferFailure.PolicyDisabled };
            foreach (var operation in DrainAssemblerEngine.Plan(new InventoryProjectionBuilder().Build(scope), settings, Flags))
            {
                job.DrainRemaining[operation] = operation.Plan.RequestedAmount;
                foreach (var allocation in operation.Plan.Allocations)
                {
                    if (job.Drain.Count >= Math.Max(1, Math.Min(128, config.AllocationsPerIntent)))
                    { job.Truncated = true; break; }
                    job.Drain.Enqueue((operation, allocation));
                }
                if (job.Truncated) break;
            }
        }
        jobs.Add(job.Receipt.Id, job);
        return new ActionReceipt { JobId = job.Receipt.Id, Detail = "Utility job started; cancellation stops future steps, not accepted transfers." };
    }

    public UtilityJobReceipt Status(Guid id, ulong sender, bool cancel)
    {
        if (!jobs.TryGetValue(id, out var job) || job.Sender != sender) return null;
        if (cancel && job.Receipt.State == UtilityJobState.Running) Finish(job, UtilityJobState.Cancelled, "Cancelled. " + Location(job));
        return job.Receipt;
    }

    public void Update()
    {
        var now = Stopwatch.GetTimestamp();
        if (now < next) return;
        next = now + Stopwatch.Frequency / 10;
        foreach (var id in jobs.Where(pair => pair.Value.Receipt.State != UtilityJobState.Running && now > pair.Value.Deadline).Select(pair => pair.Key).ToArray()) jobs.Remove(id);
        var active = jobs.Values.Where(job => job.Receipt.State == UtilityJobState.Running).ToArray();
        if (active.Length == 0) return;
        if (cursor == int.MaxValue) cursor = 0;
        var job = active[cursor++ % active.Length];
        if (now > job.Deadline || !Enabled(job.Action) || !store.Available || !job.Guard() ||
            (Online(job) ? !permissions.HasAccess(job.Terminal, job.Sender) : !job.Scope.AnchorGrid.BigOwners.Contains(job.Identity)))
        { Finish(job, UtilityJobState.Interrupted, "Job expired, scope changed, or terminal access lost. " + Location(job)); return; }
        if (job.Receipt.Mutations >= Math.Max(1, Math.Min(128, config.AllocationsPerIntent)))
        { job.Receipt.Failure = TransferFailure.WorkLimit; Finish(job, UtilityJobState.Partial, Location(job)); return; }
        try
        {
            if (job.Drain.Count > 0)
            {
                var work = job.Drain.Dequeue();
                var remaining = job.DrainRemaining[work.Operation];
                if (remaining <= 0) return;
                if (!work.Operation.CanContinue || !PolicyEnabled(job.Settings.Policy))
                { job.Receipt.Failure = TransferFailure.ScopeChanged; return; }
                var source = job.Scope.Inventories.FirstOrDefault(member => ReferenceEquals(member.Inventory, work.Allocation.Source.Inventory));
                var destination = work.Allocation.DestinationDescriptor;
                if (source?.Inventory.GetItemByID(work.Allocation.Source.ItemId)?.Content.GetObjectId() != work.Allocation.Source.DefinitionId)
                { job.Receipt.Failure = TransferFailure.StackChanged; return; }
                var moved = Move(job, source, destination, work.Allocation.Source.ItemId,
                    MyFixedPoint.Min(remaining, work.Allocation.Amount));
                if (moved > 0)
                {
                    job.DrainRemaining[work.Operation] -= moved;
                    job.Receipt.CompletedItems++;
                }
                return;
            }
            if (job.Truncated) job.Receipt.Failure = TransferFailure.WorkLimit;
            Finish(job, job.Receipt.Failure == TransferFailure.None ? UtilityJobState.Complete : UtilityJobState.Partial, "Finished bounded utility pass.");
        }
        catch (Exception)
        { job.Receipt.Failure = TransferFailure.UnknownOutcome; Finish(job, UtilityJobState.Interrupted, "Native outcome uncertain; no retry. " + Location(job)); }
    }

    private bool Allowed(Job job, InventoryDescriptor member, bool destination)
    {
        if (member == null || (Online(job) ? !permissions.HasAccess(member.Owner, job.Sender) :
            !ServerInventoryScope.PrincipalAccess(member.Owner, job.Identity, config.AutomationFactionAccess)) ||
            ServerInventoryScope.Excluded(job.Settings, member, destination)) return false;
        var profiles = store.InScope(new HashSet<long>(job.Scope.Grids.Select(grid => grid.EntityId)));
        return profiles.Length <= 1 && profiles.All(profile => !ServerInventoryScope.Excluded(profile.Settings, member, destination));
    }

    private MyFixedPoint Move(Job job, InventoryDescriptor source, InventoryDescriptor destination, uint id, MyFixedPoint wanted)
    {
        if (!Allowed(job, source, false) || !Allowed(job, destination, true)) { job.Receipt.Failure = TransferFailure.Excluded; return 0; }
        var live = source.Inventory.GetItemByID(id);
        if (!live.HasValue) { job.Receipt.Failure = TransferFailure.StackChanged; return 0; }
        var item = live.Value.Content.GetObjectId();
        var amount = TransferPlanner.Normalize(item, MyFixedPoint.Min(wanted, MyFixedPoint.Min(live.Value.Amount, destination.Inventory.ComputeAmountThatFits(item))));
        if (amount <= 0 || !destination.Inventory.CanItemsBeAdded(amount, item)) { job.Receipt.Failure = TransferFailure.DestinationFull; return 0; }
        var failure = TransferFailure.NoConveyorPath;
        if (!(Online(job) ? validation.CanTransfer(source.Inventory, destination.Inventory, job.Terminal, job.Sender, job.Identity, item, out failure) :
            AuthoritativeActions.OfflinePath(source, destination, job.Identity, item, config.AutomationFactionAccess)))
        { job.Receipt.Failure = failure; return 0; }
        var moved = MyInventory.Transfer(source.Inventory, destination.Inventory, id, -1, amount, spawn: false);
        job.Receipt.Mutations++;
        return moved;
    }

    private bool PolicyEnabled(ClientPlugin.DistributionPolicy policy) => policy switch
    {
        ClientPlugin.DistributionPolicy.ExistingStackFirst => config.ExistingStackFirst,
        ClientPlugin.DistributionPolicy.FillFirst => config.FillFirst,
        ClientPlugin.DistributionPolicy.EvenByItem => config.EvenByItem,
        _ => false
    };
    private bool Enabled(ShipAction action) => config.Enabled && config.UtilityJobs &&
        action == ShipAction.DrainAssemblers && config.AssemblerDrainJobs;
    private static bool Online(Job job) => MySession.Static.Players.TryGetPlayerBySteamId(job.Sender, out _);
    private static string Location(Job job) => "Accepted transfers are not rolled back.";
    private static void Finish(Job job, UtilityJobState state, string detail)
    { job.Receipt.State = state; job.Receipt.Detail = detail; job.Deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60; }

    private sealed class Job
    {
        public ulong Sender; public long Identity, Deadline;
        public ShipAction Action;
        public MechanicalInventoryScope Scope; public MyTerminalBlock Terminal; public ScopeProfile Settings;
        public Func<bool> Guard; public bool Truncated;
        public readonly UtilityJobReceipt Receipt = new() { Id = Guid.NewGuid() };
        public readonly Queue<(DrainAssemblerOperation Operation, PhysicalTransferAllocation Allocation)> Drain = new();
        public readonly Dictionary<DrainAssemblerOperation, MyFixedPoint> DrainRemaining = new();
    }
}
