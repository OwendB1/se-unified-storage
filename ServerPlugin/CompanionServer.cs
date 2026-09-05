using System;
using ClientPlugin.Profiles;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using PluginSdk.Logging;
using PluginSdk.Stats;
using Sandbox;
using Sandbox.Game.World;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI;
using Shared.Companion;
using VRage.Game;
using VRage.Game.ModAPI;

namespace ServerPlugin;

internal sealed class CompanionServer : IDisposable
{
    private sealed class Inbound { public ulong Sender; public byte[] Bytes; }
    private sealed class Rate { public long Window; public int Count; }
    private readonly object gate = new();
    private readonly Queue<Inbound> inbound = new();
    private readonly Dictionary<ulong, int> queued = new();
    private readonly Dictionary<ulong, Rate> rates = new();
    private readonly CompanionConfig config;
    private readonly Logger log;
    private readonly CompanionStats stats;
    private readonly ProfilePermissions permissions = new();
    private readonly RequestJournal journal = new();
    private readonly MySession session;
    private readonly IMyMultiplayer transport;
    private readonly ScopeProfileStore store;
    private readonly AuthoritativeTransfers transfers;
    private readonly AuthoritativeActions actions;
    private readonly AutomationScheduler automation;
    private readonly UtilityJobs utilityJobs;
    private readonly ProfileOperations profileOperations;
    private readonly Guid epoch = Guid.NewGuid();
    private readonly long utcOrigin = DateTime.UtcNow.Ticks;
    private readonly long clockOrigin = Stopwatch.GetTimestamp();
    private readonly Dictionary<ulong, CompanionMessage> subscriptions = new();
    private readonly Queue<ulong> notifications = new();
    private readonly HashSet<ulong> notificationPending = new();
    private long nextMaintenance;
    private bool disposed;

    public CompanionServer(MySession session, CompanionConfig config, Logger log, CompanionStats stats)
    {
        this.session = session; this.config = config; this.log = log; this.stats = stats;
        transport = MyModAPIHelper.MyMultiplayer.Static;
        store = new ScopeProfileStore(session.CurrentPath, log);
        transfers = new AuthoritativeTransfers(config, permissions, store, stats);
        actions = new AuthoritativeActions(config, permissions, store, transfers, stats);
        automation = new AutomationScheduler(store, config, actions, log);
        utilityJobs = new UtilityJobs(config, store, permissions);
        profileOperations = new ProfileOperations(store, config);
        session.OnSavingCheckpoint += Saving;
        session.Players.PlayersChanged += PlayersChanged;
        transport.RegisterSecureMessageHandler(CompanionProtocol.Channel, Receive);
        foreach (var player in session.Players.GetOnlinePlayers()) Advertise(player.Id.SteamId, Guid.Empty);
    }

    private CompanionCapabilities Capabilities => !config.Enabled || !store.Available ? CompanionCapabilities.None :
        (config.SharedProfiles ? CompanionCapabilities.SharedProfiles | CompanionCapabilities.ProfileOperations : CompanionCapabilities.None) |
        (config.Transfers ? CompanionCapabilities.Transfers : CompanionCapabilities.None) |
        (config.RefineryAutomation ? CompanionCapabilities.RefineryAutomation : CompanionCapabilities.None) |
        (config.ComponentAutomation ? CompanionCapabilities.ComponentAutomation : CompanionCapabilities.None) |
        (config.LoadoutAutomation ? CompanionCapabilities.LoadoutAutomation : CompanionCapabilities.None) |
        (config.UtilityJobs ? CompanionCapabilities.UtilityJobs : CompanionCapabilities.None) |
        CompanionCapabilities.Coordination | CompanionCapabilities.GroupRules | CompanionCapabilities.CraftingTargets;
    // An OS clock correction must not make a pruned request executable again.
    private long ServerNow => utcOrigin + (long)((Stopwatch.GetTimestamp() - clockOrigin) *
        (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency);

    private void Receive(ushort channel, byte[] bytes, ulong sender, bool fromServer)
    {
        if (fromServer || sender == 0 || bytes == null || bytes.Length < CompanionProtocol.HeaderBytes ||
            bytes.Length > CompanionProtocol.MaxPacketBytes) return;
        lock (gate)
        {
            if (disposed || inbound.Count >= 64 || queued.TryGetValue(sender, out var count) && count >= 4) return;
            var window = Stopwatch.GetTimestamp() / (Stopwatch.Frequency * 10);
            if (!rates.TryGetValue(sender, out var rate))
            {
                if (rates.Count >= 256) return;
                rates[sender] = rate = new Rate { Window = window };
            }
            if (rate.Window != window) { rate.Window = window; rate.Count = 0; }
            if (++rate.Count > Math.Max(1, Math.Min(60, config.RequestsPerWindow))) return;
            inbound.Enqueue(new Inbound { Sender = sender, Bytes = (byte[])bytes.Clone() });
            queued[sender] = count + 1;
        }
    }

    public void Update()
    {
        for (var index = 0; index < Math.Max(1, Math.Min(16, config.MessagesPerUpdate)); index++)
        {
            Inbound request;
            lock (gate)
            {
                if (inbound.Count == 0) break;
                request = inbound.Dequeue();
                if (--queued[request.Sender] == 0) queued.Remove(request.Sender);
            }
            try { Process(request); }
            catch (Exception exception)
            {
                stats.Rejected++;
                log.Error("Companion request failed; other requests remain available", exception);
            }
        }
        ProcessNotifications();
        automation.Update();
        utilityJobs.Update();
        store.Update();
        if (Stopwatch.GetTimestamp() < nextMaintenance) return;
        nextMaintenance = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        if (Stopwatch.GetTimestamp() - clockOrigin > Stopwatch.Frequency * 60)
            store.AuditOrphans(id => MyEntities.TryGetEntityById<MyCubeGrid>(id, out var grid) && !grid.MarkedForClose, config.OrphanRetentionDays);
        journal.Prune(ServerNow);
        lock (gate)
        {
            var window = Stopwatch.GetTimestamp() / (Stopwatch.Frequency * 10);
            foreach (var id in rates.Where(p => p.Value.Window < window - 1).Select(p => p.Key).ToArray()) rates.Remove(id);
            stats.QueueDepth = inbound.Count;
        }
        stats.Profiles = store.Profiles.Count; stats.CachedResults = journal.Count;
        stats.AutomationProfiles = store.Profiles.Count(profile => profile.Automation != 0);
        stats.UtilityJobs = utilityJobs.ActiveCount;
        PluginStats.Publish("UnifiedStorage", new StatsSnapshot
        {
            UtcTimestamp = DateTime.UtcNow, Groups = { StatsSchema.Build(typeof(CompanionStats)).CaptureGroup(new[] { stats }) }
        });
    }

    private void Process(Inbound incoming)
    {
        var started = Stopwatch.GetTimestamp();
        if (!CompanionProtocol.TryDecode(incoming.Bytes, out var request) ||
            !session.Players.TryGetPlayerBySteamId(incoming.Sender, out _)) return;
        if (request.Kind == MessageKind.Hello)
        {
            if (request.Body.Length != 0) return;
            Advertise(incoming.Sender, request.RequestId);
            return;
        }
        if (request.Kind != MessageKind.GetProfile && request.Kind != MessageKind.PublishProfile && request.Kind != MessageKind.Transfer &&
            request.Kind != MessageKind.Action && request.Kind != MessageKind.SetAutomation && request.Kind != MessageKind.JobStatus && request.Kind != MessageKind.CancelJob && request.Kind != MessageKind.ProfileOperation && request.Kind != MessageKind.AutomationStatus) return;
        var response = new CompanionMessage
        {
            Kind = MessageKind.Result, Epoch = epoch, RequestId = request.RequestId,
            AnchorEntityId = request.AnchorEntityId, Code = ResultCode.Unavailable
        };
        if (request.Epoch != epoch) { Send(incoming.Sender, response); return; }
        // Revalidate access even for cached settings: ownership may have changed since the first request.
        if (!permissions.TryResolve(incoming.Sender, request, out var identity, out var anchor, out var grids))
        { response.Code = ResultCode.Denied; Send(incoming.Sender, response); return; }
        var required = request.Kind == MessageKind.Transfer ? CompanionCapabilities.Transfers :
            request.Kind == MessageKind.Action || request.Kind == MessageKind.JobStatus || request.Kind == MessageKind.CancelJob || request.Kind == MessageKind.AutomationStatus ? CompanionCapabilities.Coordination : CompanionCapabilities.SharedProfiles;
        if ((Capabilities & required) == 0) { Send(incoming.Sender, response); return; }
        var matches = store.InScope(grids);
        if ((request.Kind == MessageKind.GetProfile || request.Kind == MessageKind.PublishProfile || request.Kind == MessageKind.SetAutomation || request.Kind == MessageKind.AutomationStatus) && matches.Any(profile => !ProfilePermissions.CanRead(profile, identity, config.AllowFactionRead)))
        { response.Code = ResultCode.Denied; Send(incoming.Sender, response); return; }
        if (journal.TryFind(incoming.Sender, request.RequestId, incoming.Bytes, out var cached, out var conflict))
        {
            if (!conflict && cached != null) { stats.CacheHits++; transport.SendMessageTo(CompanionProtocol.Channel, cached, incoming.Sender); }
            else { response.Code = conflict ? ResultCode.Invalid : ResultCode.UnknownOutcome; Send(incoming.Sender, response); }
            return;
        }
        var now = ServerNow;
        if (request.RequestId == Guid.Empty || request.DeadlineUtcTicks <= now ||
            request.DeadlineUtcTicks > now + TimeSpan.FromSeconds(CompanionProtocol.RequestLifetimeSeconds).Ticks)
        { response.Code = ResultCode.Expired; Send(incoming.Sender, response); return; }
        if (!journal.TryReserve(incoming.Sender, request, incoming.Bytes, now, 256))
        { response.Code = ResultCode.Busy; Send(incoming.Sender, response); return; }
        try
        {
            if (request.Kind == MessageKind.ProfileOperation)
                profileOperations.Execute(incoming.Sender, identity, anchor, grids, request, response);
            else if (matches.Length > 1) response.Code = ResultCode.Conflict;
            else if (request.Kind == MessageKind.AutomationStatus)
            {
                response.Code = matches.Length == 0 ? ResultCode.NotFound : ResultCode.Ok;
                if (matches.Length != 0) response.Body = ProfileCodec.Encode(automation.Status(matches[0]));
            }
            else if (request.Kind == MessageKind.JobStatus || request.Kind == MessageKind.CancelJob)
            {
                var query = ProfileCodec.Decode<UtilityJobReceipt>(request.Body);
                var receipt = utilityJobs.Status(query.Id, incoming.Sender, request.Kind == MessageKind.CancelJob);
                response.Code = receipt == null ? ResultCode.NotFound : ResultCode.Ok;
                if (receipt != null) response.Body = ProfileCodec.Encode(receipt);
            }
            else if (request.Kind == MessageKind.Action)
            {
                var intent = ProfileCodec.Decode<ShipActionIntent>(request.Body);
                intent.Validate();
                var existing = matches.SingleOrDefault();
                if (intent.UseSharedSettings && (existing == null || existing.OwnerIdentityId != identity ||
                    !ProfilePermissions.CanRead(existing, identity, false)))
                    response.Code = ResultCode.Denied;
                else if (intent.UseSharedSettings && (existing.Id != request.ProfileId || existing.Revision != request.Revision))
                    response.Code = ResultCode.Conflict;
                else if (intent.UseSharedSettings && ((existing.Automation & ShipActionIntent.Capability(intent.Action)) == 0 || !automation.Ready(existing)))
                    response.Code = ResultCode.Busy;
                else if (!intent.UseSharedSettings && existing != null && (existing.Automation & ShipActionIntent.Capability(intent.Action)) != 0)
                    response.Code = ResultCode.Busy;
                else
                {
                    if (intent.UseSharedSettings) intent.Settings = existing.Settings;
                    var terminal = MyEntities.GetEntityById(request.TerminalEntityId) as MyTerminalBlock;
                    var receipt = intent.Action is ShipAction.RefillBottles or ShipAction.DrainAssemblers ?
                        utilityJobs.Start(incoming.Sender, identity, anchor, terminal, intent) :
                        actions.Execute(anchor, terminal, incoming.Sender, identity, intent);
                    response.Body = ProfileCodec.Encode(receipt);
                    response.Code = receipt.Failure == TransferFailure.UnknownOutcome ? ResultCode.UnknownOutcome : ResultCode.Ok;
                }
            }
            else if (request.Kind == MessageKind.SetAutomation)
            {
                var existing = matches.SingleOrDefault();
                if (existing == null) response.Code = ResultCode.NotFound;
                else if (existing.OwnerIdentityId != identity || !anchor.BigOwners.Contains(identity)) response.Code = ResultCode.Denied;
                else if (existing.Id != request.ProfileId || existing.Revision != request.Revision)
                { response.Code = ResultCode.Conflict; Snapshot(response, existing); }
                else
                {
                    var modes = ProfileCodec.Decode<AutomationClaim>(request.Body).Modes;
                    if ((modes & ~AutomationManifest.Modes) != 0) throw new System.IO.InvalidDataException("Unknown automation mode.");
                    var updated = ProfileCodec.DecodeDocument<SharedScopeProfile>(ProfileCodec.EncodeDocument(existing));
                    updated.Automation = modes; updated.Revision = checked(updated.Revision + 1);
                    response.ProfileId = updated.Id; response.Revision = updated.Revision;
                    store.Put(updated); response.Code = ResultCode.Ok;
                    foreach (var player in session.Players.GetOnlinePlayers()) Advertise(player.Id.SteamId, Guid.Empty);
                }
            }
            else if (request.Kind == MessageKind.Transfer)
            {
                var intent = ProfileCodec.Decode<TransferIntent>(request.Body);
                var receipt = transfers.Execute(incoming.Sender, identity,
                    MyEntities.GetEntityById(request.TerminalEntityId) as MyTerminalBlock, intent);
                if (receipt.RejectedRaw > 0 || receipt.Failure == TransferFailure.UnknownOutcome) stats.PartialTransfers++;
                response.Body = ProfileCodec.Encode(receipt);
                response.Code = receipt.Failure == TransferFailure.UnknownOutcome ? ResultCode.UnknownOutcome : ResultCode.Ok;
            }
            else if (request.Kind == MessageKind.GetProfile)
            {
                response.Code = matches.Length == 0 ? ResultCode.NotFound : ResultCode.Ok;
                if (matches.Length != 0) Snapshot(response, matches[0]);
            }
            else
            {
                var existing = matches.SingleOrDefault();
                if (!anchor.BigOwners.Contains(identity) || existing != null && existing.OwnerIdentityId != identity)
                    response.Code = ResultCode.Denied;
                else if (existing == null ? request.ProfileId != Guid.Empty || request.Revision != 0 :
                         request.ProfileId != existing.Id || request.Revision != existing.Revision)
                {
                    response.Code = ResultCode.Conflict;
                    if (existing != null) Snapshot(response, existing);
                }
                else if (existing == null && store.Profiles.Count >= Math.Max(1, Math.Min(256, config.MaxProfiles)))
                    response.Code = ResultCode.Busy;
                else
                {
                    var submitted = ProfileCodec.Decode<SharedScopeProfile>(request.Body);
                    if (submitted.SchemaVersion != 1) throw new System.IO.InvalidDataException("Unsupported shared profile schema.");
                    ProfileCodec.Validate(submitted.Settings);
                    InventoryGroupRecord.Migrate(submitted.Settings);
                    submitted.Settings.WorldId = string.Empty;
                    submitted.Settings.ScopeAnchorEntityId = existing?.AnchorEntityId ?? anchor.EntityId;
                    var value = new SharedScopeProfile
                    {
                        Id = existing?.Id ?? Guid.NewGuid(), Revision = checked((existing?.Revision ?? 0) + 1),
                        AnchorEntityId = submitted.Settings.ScopeAnchorEntityId, OwnerIdentityId = identity,
                        FactionShared = config.AllowFactionRead && submitted.FactionShared, Settings = submitted.Settings,
                        Automation = existing?.Automation ?? CompanionCapabilities.None
                    };
                    Snapshot(response, value); // Encode before committing, so an encoding failure never mutates settings.
                    store.Put(value);
                    response.Code = ResultCode.Ok;
                }
            }
        }
        catch (Exception exception)
        {
            response.Body = Array.Empty<byte>(); response.Code = ResultCode.Invalid;
            log.Warning("Companion request rejected", new { reason = exception.GetType().Name });
        }
        var bytes = CompanionProtocol.Encode(response);
        stats.LastRequestMilliseconds = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
        journal.Complete(incoming.Sender, request.RequestId, bytes);
        if ((request.Kind == MessageKind.GetProfile || request.Kind == MessageKind.PublishProfile || request.Kind == MessageKind.SetAutomation || request.Kind == MessageKind.ProfileOperation) && (response.Code == ResultCode.Ok || response.Code == ResultCode.NotFound) &&
            (subscriptions.ContainsKey(incoming.Sender) || subscriptions.Count < 256))
            subscriptions[incoming.Sender] = new CompanionMessage
            { AnchorEntityId = request.AnchorEntityId, TerminalEntityId = request.TerminalEntityId };
        if (response.Code == ResultCode.Ok) stats.Completed++; else stats.Rejected++;
        if (response.Code == ResultCode.Conflict) stats.RevisionConflicts++;
        if (config.LogRequests) log.Info("Companion request", new { incoming.Sender, request.RequestId, request.Kind, response.Code });
        transport.SendMessageTo(CompanionProtocol.Channel, bytes, incoming.Sender);
        if ((request.Kind == MessageKind.PublishProfile || request.Kind == MessageKind.SetAutomation || request.Kind == MessageKind.ProfileOperation) && response.Code == ResultCode.Ok)
            foreach (var subscriber in subscriptions.Keys)
                if (subscriber != incoming.Sender && notificationPending.Add(subscriber)) notifications.Enqueue(subscriber);
    }

    private void ProcessNotifications()
    {
        for (var i = 0; i < 2 && notifications.Count != 0; i++)
        {
            var recipient = notifications.Dequeue(); notificationPending.Remove(recipient);
            if (!subscriptions.TryGetValue(recipient, out var request) || Capabilities == CompanionCapabilities.None) continue;
            if (!permissions.TryResolve(recipient, request, out var identity, out _, out var grids))
            { subscriptions.Remove(recipient); continue; }
            var profiles = store.InScope(grids);
            if (profiles.Length != 1) continue;
            var profile = profiles[0];
            if (!ProfilePermissions.CanRead(profile, identity, config.AllowFactionRead))
            { subscriptions.Remove(recipient); continue; }
            Send(recipient, new CompanionMessage
            {
                Kind = MessageKind.ProfileChanged, Epoch = epoch, ProfileId = profile.Id,
                AnchorEntityId = profile.AnchorEntityId, Revision = profile.Revision
            });
        }
    }

    private static void Snapshot(CompanionMessage response, SharedScopeProfile profile)
    {
        response.ProfileId = profile.Id; response.Revision = profile.Revision;
        response.Body = ProfileCodec.Encode(profile);
    }
    private void Advertise(ulong sender, Guid requestId) => Send(sender, new CompanionMessage
    {
        Kind = MessageKind.HelloAck, Epoch = epoch, RequestId = requestId,
        Capabilities = Capabilities, DeadlineUtcTicks = ServerNow,
        Body = store.Available ? automation.Manifest().Encode() : Array.Empty<byte>()
    });
    private void Send(ulong sender, CompanionMessage message)
    {
        if (message.Kind == MessageKind.Result && message.Code != ResultCode.Ok) stats.Rejected++;
        try { transport.SendMessageTo(CompanionProtocol.Channel, CompanionProtocol.Encode(message), sender); }
        catch (Exception exception) { log.Warning("Companion reply could not be sent", new { reason = exception.GetType().Name }); }
    }
    private void PlayersChanged(bool added, Sandbox.Game.World.MyPlayer.PlayerId id)
    {
        if (added) Advertise(id.SteamId, Guid.Empty);
        else subscriptions.Remove(id.SteamId);
    }
    private void Saving(MyObjectBuilder_Checkpoint checkpoint) => store.Flush();
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; inbound.Clear(); queued.Clear(); rates.Clear();
        }
        // Magnetar may dispose plugins off-thread. Do not let handler cleanup skip the final profile flush.
        var game = MySandboxGame.Static;
        if (game != null)
        {
            if (Thread.CurrentThread == game.UpdateThread)
                transport.UnregisterSecureMessageHandler(CompanionProtocol.Channel, Receive);
            else
                game.Invoke(() => transport.UnregisterSecureMessageHandler(CompanionProtocol.Channel, Receive),
                    "UnifiedStorage.UnregisterHandler");
        }
        session.OnSavingCheckpoint -= Saving;
        session.Players.PlayersChanged -= PlayersChanged;
        automation.Dispose();
        store.Flush();
        PluginStats.Clear("UnifiedStorage");
    }
}
