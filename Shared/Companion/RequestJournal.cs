using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace Shared.Companion;

// Game-thread only. Entries cannot be evicted while their request could still execute.
public sealed class RequestJournal
{
    private sealed class Entry
    {
        public byte[] Fingerprint;
        public long Expires;
        public byte[] Result;
    }

    private readonly Dictionary<(ulong, Guid), Entry> entries = new();
    public int Count => entries.Count;

    public bool TryFind(ulong sender, Guid id, byte[] request, out byte[] result, out bool conflict)
    {
        result = null; conflict = false;
        if (!entries.TryGetValue((sender, id), out var entry)) return false;
        conflict = !entry.Fingerprint.SequenceEqual(Hash(request));
        if (!conflict) result = entry.Result;
        return true;
    }

    public bool TryReserve(ulong sender, CompanionMessage message, byte[] request, long now, int capacity)
    {
        Prune(now);
        if (message.RequestId == Guid.Empty || message.DeadlineUtcTicks <= now ||
            message.DeadlineUtcTicks > now + TimeSpan.FromSeconds(CompanionProtocol.RequestLifetimeSeconds).Ticks ||
            entries.Count >= capacity || entries.ContainsKey((sender, message.RequestId))) return false;
        entries.Add((sender, message.RequestId), new Entry
        {
            Fingerprint = Hash(request), Expires = message.DeadlineUtcTicks + TimeSpan.FromSeconds(60).Ticks
        });
        return true;
    }

    public void Complete(ulong sender, Guid id, byte[] result) => entries[(sender, id)].Result = result;
    public void Prune(long now)
    {
        foreach (var key in entries.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
            entries.Remove(key);
    }
    private static byte[] Hash(byte[] value)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(value);
    }
}
