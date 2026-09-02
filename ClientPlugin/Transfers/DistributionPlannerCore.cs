using System;
using System.Collections.Generic;
using System.Linq;

namespace ClientPlugin.Transfers;

public readonly struct DistributionCandidateCore
{
    public DistributionCandidateCore(long key, long current, long capacity)
    {
        Key = key;
        Current = Math.Max(0, current);
        Capacity = Math.Max(0, capacity);
    }

    public long Key { get; }
    public long Current { get; }
    public long Capacity { get; }
}

public readonly struct DistributionAllocationCore
{
    public DistributionAllocationCore(long key, long amount)
    {
        Key = key;
        Amount = amount;
    }

    public long Key { get; }
    public long Amount { get; }
}

public static class DistributionPlannerCore
{
    public static IReadOnlyList<DistributionAllocationCore> Greedy(
        long amount,
        IEnumerable<DistributionCandidateCore> orderedCandidates,
        long quantum)
    {
        var remaining = Normalize(amount, quantum);
        var result = new List<DistributionAllocationCore>();
        foreach (var candidate in orderedCandidates)
        {
            var allocation = Normalize(Math.Min(remaining, candidate.Capacity), quantum);
            if (allocation <= 0)
                continue;
            result.Add(new DistributionAllocationCore(candidate.Key, allocation));
            remaining -= allocation;
            if (remaining <= 0)
                break;
        }
        return result;
    }

    public static IReadOnlyList<DistributionAllocationCore> Even(
        long amount,
        IEnumerable<DistributionCandidateCore> candidates,
        long quantum)
    {
        if (quantum <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantum));
        var remaining = Normalize(amount, quantum);
        var states = candidates.OrderBy(candidate => candidate.Key)
            .Select(candidate => new State(candidate)).ToArray();
        if (remaining <= 0 || states.Length == 0)
            return Array.Empty<DistributionAllocationCore>();

        var low = states.Min(state => state.Candidate.Current);
        var high = states.Max(state => SaturatingAdd(state.Candidate.Current, state.Candidate.Capacity));
        while (low < high)
        {
            var difference = high - low;
            var middle = low + difference / 2 + difference % 2;
            if (Required(states, middle, quantum) <= remaining)
                low = middle;
            else
                high = middle - 1;
        }

        foreach (var state in states)
            state.Allocated = Normalize(
                Math.Min(state.Candidate.Capacity, Math.Max(0, low - state.Candidate.Current)),
                quantum);
        remaining -= states.Sum(state => state.Allocated);
        foreach (var state in states.Where(state => state.RemainingCapacity >= quantum)
                     .OrderBy(state => state.Final)
                     .ThenBy(state => state.Candidate.Key))
        {
            if (remaining < quantum)
                break;
            state.Allocated += quantum;
            remaining -= quantum;
        }
        return states.Where(state => state.Allocated > 0)
            .Select(state => new DistributionAllocationCore(state.Candidate.Key, state.Allocated))
            .ToArray();
    }

    private static long Normalize(long value, long quantum) =>
        value <= 0 ? 0 : value / quantum * quantum;

    private static long Required(IEnumerable<State> states, long finalLevel, long quantum)
    {
        var result = 0L;
        foreach (var state in states)
        {
            var amount = Normalize(
                Math.Min(state.Candidate.Capacity, Math.Max(0, finalLevel - state.Candidate.Current)),
                quantum);
            result = SaturatingAdd(result, amount);
        }
        return result;
    }

    private static long SaturatingAdd(long left, long right) =>
        right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;

    private sealed class State
    {
        public State(DistributionCandidateCore candidate) => Candidate = candidate;
        public DistributionCandidateCore Candidate { get; }
        public long Allocated { get; set; }
        public long Final => Candidate.Current + Allocated;
        public long RemainingCapacity => Candidate.Capacity - Allocated;
    }
}
