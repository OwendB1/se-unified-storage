using System;
using System.Collections.Generic;
using System.Diagnostics;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Shared.Companion;
using VRage.Game.ModAPI;

namespace ClientPlugin.Companion;

public sealed class CompanionClient : IDisposable
{
    private readonly Queue<byte[]> inbound = new();
    private MySession session;
    private IMyMultiplayer transport;
    private Guid epoch;
    private Guid pendingId;
    private Action<CompanionMessage> pending;
    private long pendingUntil;
    private long nextHello;
    private long lastHello;
    private long serverUtc;
    public CompanionCapabilities Capabilities { get; private set; }
    public bool Available => (Capabilities & CompanionCapabilities.SharedProfiles) != 0;
    public bool Busy => pending != null;
    public event Action<CompanionMessage> ProfileChanged;

    public CompanionClient() => MySession.OnUnloading += Reset;

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
        var current = MySession.Static;
        if (current == null || !current.Ready || Sync.IsServer) return;
        if (!ReferenceEquals(session, current))
        {
            Reset(); session = current;
            transport = MyModAPIHelper.MyMultiplayer.Static;
            transport.RegisterSecureMessageHandler(CompanionProtocol.Channel, Receive);
        }
        var now = Stopwatch.GetTimestamp();
        for (var i = 0; i < 4; i++)
        {
            byte[] bytes;
            lock (inbound) { if (inbound.Count == 0) break; bytes = inbound.Dequeue(); }
            if (!CompanionProtocol.TryDecode(bytes, out var message)) continue;
            if (message.Kind == MessageKind.HelloAck && message.Epoch != Guid.Empty &&
                message.DeadlineUtcTicks > 0 && message.DeadlineUtcTicks < DateTime.MaxValue.Ticks - TimeSpan.TicksPerDay)
            {
                if (epoch != Guid.Empty && epoch != message.Epoch) FinishUnknown();
                epoch = message.Epoch; Capabilities = message.Capabilities;
                serverUtc = message.DeadlineUtcTicks; lastHello = now;
            }
            else if (message.Kind == MessageKind.Result && message.Epoch == epoch && message.RequestId == pendingId)
                Finish(message);
            else if (message.Kind == MessageKind.ProfileChanged && message.Epoch == epoch && message.Body.Length == 0)
                ProfileChanged?.Invoke(message);
        }
        if (pending != null && now >= pendingUntil) FinishUnknown();
        if (lastHello != 0 && now - lastHello > Stopwatch.Frequency * 45) Capabilities = CompanionCapabilities.None;
        if (now < nextHello) return;
        nextHello = now + Stopwatch.Frequency * 20;
        transport.SendMessageToServer(CompanionProtocol.Channel, CompanionProtocol.Encode(new CompanionMessage
        { Kind = MessageKind.Hello, RequestId = Guid.NewGuid() }));
    }

    private void Receive(ushort channel, byte[] bytes, ulong sender, bool fromServer)
    {
        if (!fromServer || bytes == null || bytes.Length < CompanionProtocol.HeaderBytes || bytes.Length > CompanionProtocol.MaxPacketBytes) return;
        lock (inbound) { if (inbound.Count < 16) inbound.Enqueue((byte[])bytes.Clone()); }
    }

    public bool Request(MessageKind kind, long anchor, long terminal, SharedScopeProfile snapshot, byte[] body,
        Action<CompanionMessage> completed)
    {
        if (!Available || Busy || transport == null || completed == null) return false;
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
        Capabilities = CompanionCapabilities.None; nextHello = lastHello = 0;
        lock (inbound) inbound.Clear();
        FinishUnknown();
    }
    public void Dispose() { MySession.OnUnloading -= Reset; Reset(); }
}
