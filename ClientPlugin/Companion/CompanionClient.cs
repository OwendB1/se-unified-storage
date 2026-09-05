using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Shared.Companion;
using VRage.Game.ModAPI;

namespace ClientPlugin.Companion;

public sealed partial class CompanionClient : IDisposable
{
    private readonly Queue<byte[]> inbound = new();
    private MySession session;
    private IMyMultiplayer transport;
    private Guid epoch;
    private Guid helloId;
    private ulong serverId;
    private Guid pendingId;
    private Action<CompanionMessage> pending;
    private long pendingUntil;
    private long nextHello;
    private long lastHello;
    private long serverUtc;
    private AutomationManifest ownership;
    private bool coordinationSeen;
    private MySession ownershipSession;
    private long discoveryStarted;
    public CompanionCapabilities Capabilities { get; private set; }
    public bool Available => Supports(CompanionCapabilities.SharedProfiles);
    public bool Supports(CompanionCapabilities capability) => !Config.Current.ForceClientOnly && (Capabilities & capability) == capability;
    public bool Busy => pending != null || profileSequence;
    public event Action<CompanionMessage> ProfileChanged;

    public bool AllowsLocal(MyCubeGrid grid, CompanionCapabilities mode)
    {
        if (Config.Current.ForceClientOnly) return true;
        if (Sync.IsServer || grid == null) return true;
        // Give discovery time before starting remembered maintainers on a newly joined server.
        if (!coordinationSeen) return discoveryStarted != 0 &&
            (lastHello != 0 || Stopwatch.GetTimestamp() - discoveryStarted >= Stopwatch.Frequency * 45);
        if (ownership == null || lastHello == 0 || Stopwatch.GetTimestamp() - lastHello > Stopwatch.Frequency * 45) return false;
        var grids = MyCubeGridGroups.Static?.Mechanical.GetGroupNodes(grid) ?? new List<MyCubeGrid> { grid };
        return !ownership.Claims.Any(claim => (claim.Modes & mode) != 0 && grids.Any(member => member.EntityId == claim.Anchor));
    }

    public CompanionClient() => MySession.OnUnloading += Reset;

    public void ApplyPrivacySetting()
    {
        if (Config.Current.ForceClientOnly) Reset();
    }

    public void Update()
    {
        try { UpdateCore(); }
        catch (Exception exception)
        {
            Plugin.Instance?.Log.Error(exception, "Companion unavailable; standalone inventory remains active");
            Reset();
        }
    }

    private void UpdateCore()
    {
        if (Config.Current.ForceClientOnly)
        {
            if (transport != null || session != null || pending != null || profileSequence) Reset();
            return;
        }
        var current = MySession.Static;
        if (current == null || !current.Ready || Sync.IsServer) return;
        if (!ReferenceEquals(session, current) || serverId != Sync.ServerId)
        {
            Reset(); session = current;
            discoveryStarted = Stopwatch.GetTimestamp();
            if (!ReferenceEquals(ownershipSession, current)) { coordinationSeen = false; ownershipSession = current; }
            transport = MyModAPIHelper.MyMultiplayer.Static;
            serverId = Sync.ServerId;
            transport.RegisterSecureMessageHandler(CompanionProtocol.Channel, Receive);
        }
        var now = Stopwatch.GetTimestamp();
        for (var i = 0; i < 4; i++)
        {
            byte[] bytes;
            lock (inbound) { if (inbound.Count == 0) break; bytes = inbound.Dequeue(); }
            if (!CompanionProtocol.TryDecode(bytes, out var message)) continue;
            if (message.Kind == MessageKind.HelloAck && helloId != Guid.Empty && message.RequestId == helloId &&
                now <= nextHello && message.Epoch != Guid.Empty &&
                message.DeadlineUtcTicks > 0 && message.DeadlineUtcTicks < DateTime.MaxValue.Ticks - TimeSpan.TicksPerDay)
            {
                helloId = Guid.Empty;
                if (epoch != Guid.Empty && epoch != message.Epoch) FinishUnknown();
                epoch = message.Epoch; Capabilities = message.Capabilities;
                // Older companions would ignore Rules in selections. Keep coordination/status/cancel,
                // but use standalone transfers and disable profile exchange until the server is updated.
                if ((Capabilities & CompanionCapabilities.GroupRules) == 0)
                    Capabilities &= CompanionCapabilities.Coordination;
                if ((message.Capabilities & CompanionCapabilities.Coordination) != 0 || message.Body.Length != 0)
                {
                    coordinationSeen = true; ownership = null;
                    var manifest = AutomationManifest.Decode(message.Body);
                    if (manifest.Claims == null || manifest.Claims.Count > 256 || manifest.Claims.Any(claim =>
                        claim == null || claim.Anchor == 0 || (claim.Modes & ~AutomationManifest.Modes) != 0))
                        throw new InvalidOperationException("Invalid automation manifest.");
                    ownership = manifest;
                }
                serverUtc = message.DeadlineUtcTicks; lastHello = now;
            }
            else if (message.Kind == MessageKind.Result && epoch != Guid.Empty && pendingId != Guid.Empty && message.Epoch == epoch && message.RequestId == pendingId)
                Finish(message);
            else if (message.Kind == MessageKind.ProfileChanged && epoch != Guid.Empty && message.Epoch == epoch && message.Body.Length == 0)
                ProfileChanged?.Invoke(message);
        }
        if (pending != null && now >= pendingUntil) FinishUnknown();
        if (profileStep != null && pending == null && now >= profileStepAt)
        { var step = profileStep; profileStep = null; step(); }
        if (lastHello != 0 && now - lastHello > Stopwatch.Frequency * 45) Capabilities = CompanionCapabilities.None;
        if (now < nextHello) return;
        nextHello = now + Stopwatch.Frequency * 20;
        helloId = Guid.NewGuid();
        transport.SendMessageToServer(CompanionProtocol.Channel, CompanionProtocol.Encode(new CompanionMessage
        { Kind = MessageKind.Hello, RequestId = helloId }));
    }

    private void Receive(ushort channel, byte[] bytes, ulong sender, bool fromServer)
    {
        if (Config.Current.ForceClientOnly || transport == null || channel != CompanionProtocol.Channel ||
            !fromServer || serverId == 0 || sender != serverId || sender != Sync.ServerId ||
            bytes == null || bytes.Length < CompanionProtocol.HeaderBytes || bytes.Length > CompanionProtocol.MaxPacketBytes) return;
        lock (inbound) { if (inbound.Count < 16) inbound.Enqueue((byte[])bytes.Clone()); }
    }

    public bool Request(MessageKind kind, long anchor, long terminal, SharedScopeProfile snapshot, byte[] body,
        Action<CompanionMessage> completed)
    {
        var required = kind == MessageKind.Transfer ? CompanionCapabilities.Transfers :
            kind == MessageKind.Action || kind == MessageKind.JobStatus || kind == MessageKind.CancelJob || kind == MessageKind.AutomationStatus ? CompanionCapabilities.Coordination : CompanionCapabilities.SharedProfiles;
        if (kind == MessageKind.Action) required |= CompanionCapabilities.GroupRules;
        if (!Supports(required) || pending != null || profileSequence && !sendingProfilePage || transport == null || completed == null) return false;
        var now = Stopwatch.GetTimestamp();
        var message = new CompanionMessage
        {
            Kind = kind, Epoch = epoch, RequestId = Guid.NewGuid(), AnchorEntityId = anchor, TerminalEntityId = terminal,
            ProfileId = snapshot?.Id ?? Guid.Empty, Revision = snapshot?.Revision ?? 0, Body = body ?? Array.Empty<byte>(),
            DeadlineUtcTicks = serverUtc + (long)((now - lastHello) * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency) + TimeSpan.FromSeconds(30).Ticks
        };
        var bytes = CompanionProtocol.Encode(message);
        pendingId = message.RequestId; pending = completed; pendingUntil = now + Stopwatch.Frequency * 15;
        // A failed send is not proof that a mutation did not arrive. Never replay through another path.
        try { if (!transport.SendMessageToServer(CompanionProtocol.Channel, bytes)) FinishUnknown(); }
        catch (Exception exception)
        {
            Plugin.Instance?.Log.Error(exception, "Companion send failed; outcome unknown");
            FinishUnknown();
        }
        return true;
    }

    private void Finish(CompanionMessage message)
    {
        var callback = pending; pending = null; pendingId = Guid.Empty;
        callback?.Invoke(message);
    }
    private void FinishUnknown() => Finish(new CompanionMessage { Kind = MessageKind.Result, Code = ResultCode.UnknownOutcome });
    private void Reset()
    {
        transport?.UnregisterSecureMessageHandler(CompanionProtocol.Channel, Receive);
        transport = null; session = null; epoch = Guid.Empty;
        serverId = 0; helloId = Guid.Empty;
        Capabilities = CompanionCapabilities.None; nextHello = lastHello = 0;
        ownership = null;
        if (MySession.Static == null || !ReferenceEquals(MySession.Static, ownershipSession)) coordinationSeen = false;
        lock (inbound) inbound.Clear();
        FinishUnknown();
        var interrupted = profileInterrupted; profileInterrupted = null; profileStep = null; profileSequence = false;
        interrupted?.Invoke();
    }
    public void Dispose() { MySession.OnUnloading -= Reset; Reset(); }
}
