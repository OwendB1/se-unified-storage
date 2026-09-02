using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game;
using Sandbox.Game.Entities.Cube;
using VRage.Game;

namespace ClientPlugin.Automation;

public sealed class RefinerySortExecutor
{
    private readonly Queue<SortJob> jobs = new();
    private SortJob active;

    public int PendingCount => jobs.Count + (active == null ? 0 : 1);

    public void Enqueue(
        MyRefinery refinery,
        IReadOnlyList<MyDefinitionId> priority,
        long identityId,
        Func<bool> canContinue = null)
    {
        if (refinery == null || priority == null || priority.Count == 0 ||
            jobs.Any(job => ReferenceEquals(job.Refinery, refinery)) ||
            ReferenceEquals(active?.Refinery, refinery))
            return;
        jobs.Enqueue(new SortJob(refinery, priority, identityId, canContinue));
    }

    public void Update()
    {
        active ??= jobs.Count > 0 ? jobs.Dequeue() : null;
        if (active == null)
            return;
        if (active.Refinery.Closed || !active.Refinery.HasPlayerAccess(active.IdentityId) ||
            (active.CanContinue != null && !active.CanContinue()))
        {
            active = null;
            return;
        }
        var items = active.Refinery.InputInventory.GetItems();
        if (active.Waiting)
        {
            var signature = Signature(items);
            if (!string.Equals(signature, active.BeforeSignature, StringComparison.Ordinal))
                active.Waiting = false;
            else if (DateTime.UtcNow >= active.Deadline)
            {
                Plugin.Instance.Log.Warning("Refinery input sort timed out for {0}", active.Refinery.DisplayNameText);
                active = null;
                return;
            }
            else
                return;
        }

        var rank = active.Priority.Select((id, index) => (id, index))
            .ToDictionary(pair => pair.id, pair => pair.index);
        for (var index = 0; index < items.Count; index++)
        {
            var currentRank = Rank(items[index].Content.GetObjectId(), rank);
            var bestIndex = index;
            var bestRank = currentRank;
            for (var candidate = index + 1; candidate < items.Count; candidate++)
            {
                var candidateRank = Rank(items[candidate].Content.GetObjectId(), rank);
                if (candidateRank >= bestRank)
                    continue;
                bestRank = candidateRank;
                bestIndex = candidate;
            }
            if (bestIndex == index)
                continue;
            var moving = items[bestIndex];
            active.BeforeSignature = Signature(items);
            active.Deadline = DateTime.UtcNow.AddMilliseconds(
                Math.Max(500, Config.Current.AcknowledgementTimeoutMilliseconds));
            active.Waiting = true;
            MyInventory.TransferByUser(
                active.Refinery.InputInventory,
                active.Refinery.InputInventory,
                moving.ItemId,
                index,
                moving.Amount);
            return;
        }
        active = null;
    }

    public void Clear()
    {
        jobs.Clear();
        active = null;
    }

    private static int Rank(MyDefinitionId id, IReadOnlyDictionary<MyDefinitionId, int> ranks) =>
        ranks.TryGetValue(id, out var rank) ? rank : int.MaxValue;

    private static string Signature(IEnumerable<VRage.Game.Entity.MyPhysicalInventoryItem> items) =>
        string.Join("|", items.Select(item => $"{item.ItemId}:{item.Content.GetObjectId()}:{item.Amount}"));

    private sealed class SortJob
    {
        public SortJob(
            MyRefinery refinery,
            IReadOnlyList<MyDefinitionId> priority,
            long identityId,
            Func<bool> canContinue)
        {
            Refinery = refinery;
            Priority = priority;
            IdentityId = identityId;
            CanContinue = canContinue;
        }

        public MyRefinery Refinery { get; }
        public IReadOnlyList<MyDefinitionId> Priority { get; }
        public long IdentityId { get; }
        public Func<bool> CanContinue { get; }
        public bool Waiting { get; set; }
        public string BeforeSignature { get; set; }
        public DateTime Deadline { get; set; }
    }
}
