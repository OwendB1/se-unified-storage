using System;
using System.Collections.Generic;
using System.Linq;

namespace ClientPlugin.Automation;

// Value-only planning inputs: no game session, definitions manager, inventories or executor.
public sealed class ProductionTargetCore
{
    public string Item { get; set; }
    public decimal Deficit { get; set; }
    public IReadOnlyDictionary<string, decimal> Outputs { get; set; }
    public IReadOnlyList<(long Id, double QueueSeconds, double SecondsPerRun)> Assemblers { get; set; }
}

public static class AutomationPlannerCore
{
    public static IReadOnlyList<(int TargetIndex, long Assembler, decimal Runs)> Production(IReadOnlyList<ProductionTargetCore> targets)
    {
        var remaining = targets.ToDictionary(target => target.Item, target => Math.Max(0, target.Deficit), StringComparer.Ordinal);
        var result = new List<(int, long, decimal)>();
        var workloads = targets.Where(target => target.Assemblers != null).SelectMany(target => target.Assemblers)
            .GroupBy(assembler => assembler.Id).ToDictionary(group => group.Key, group => group.Max(assembler => assembler.QueueSeconds));
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            if (remaining[target.Item] <= 0 || target.Outputs == null || target.Assemblers == null || target.Assemblers.Count == 0 ||
                !target.Outputs.TryGetValue(target.Item, out var yield) || yield <= 0) continue;
            var runs = decimal.Ceiling(remaining[target.Item] / yield);
            var assembler = target.Assemblers.OrderBy(candidate => workloads[candidate.Id]).ThenBy(candidate => candidate.Id).First();
            result.Add((index, assembler.Id, runs));
            workloads[assembler.Id] += (double)runs * Math.Max(0, assembler.SecondsPerRun);
            foreach (var output in target.Outputs)
                if (remaining.TryGetValue(output.Key, out var deficit)) remaining[output.Key] = Math.Max(0, deficit - output.Value * runs);
        }
        return result;
    }

    public static int Blueprint(string requested, string canonical, IReadOnlyList<(string Id, bool SafePrimary)> choices)
    {
        for (var index = 0; index < choices.Count; index++) if (choices[index].Id == requested) return index;
        for (var index = 0; index < choices.Count; index++) if (choices[index].Id == canonical) return index;
        var safe = Enumerable.Range(0, choices.Count).Where(index => choices[index].SafePrimary).ToArray();
        return safe.Length == 1 ? safe[0] : choices.Count == 1 ? 0 : -1;
    }

    public static string[] OreOrder(IEnumerable<(string Id, string Name, double Scarcity, int Priority)> inputs,
        IEnumerable<string> preferred, bool automatic)
    {
        var entries = inputs.ToArray();
        var ids = new HashSet<string>(entries.Select(entry => entry.Id), StringComparer.Ordinal);
        var prefix = preferred.Where(ids.Contains).Distinct(StringComparer.Ordinal).ToArray();
        var remaining = entries.Where(entry => !prefix.Contains(entry.Id));
        return prefix.Concat(automatic ? remaining.OrderBy(entry => entry.Scarcity).ThenByDescending(entry => entry.Priority)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(entry => entry.Id, StringComparer.Ordinal).Select(entry => entry.Id) :
            remaining.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(entry => entry.Id, StringComparer.Ordinal).Select(entry => entry.Id)).ToArray();
    }

    public static IReadOnlyList<(decimal Need, decimal Excess)> Loadout(decimal target,
        IReadOnlyList<(decimal Current, decimal Fits)> members, bool perMember)
    {
        var remainingExcess = Math.Max(0, members.Sum(member => member.Current) - target);
        var result = new List<(decimal, decimal)>();
        foreach (var member in members)
        {
            var need = perMember ? Math.Min(Math.Max(0, target - member.Current), Math.Max(0, member.Fits)) : 0;
            var excess = perMember ? Math.Max(0, member.Current - target) : Math.Min(member.Current, remainingExcess);
            if (!perMember) remainingExcess -= excess;
            result.Add((need, excess));
        }
        return result;
    }
}
