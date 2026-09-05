using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;

namespace ClientPlugin.Inventory;

public static class ConveyorNetworkResolver
{
    public static IReadOnlyList<HashSet<long>> Find(MechanicalInventoryScope scope, int maxSearches = int.MaxValue)
    {
        var descriptors = scope.Inventories.Where(descriptor =>
            Resolve(descriptor.Owner)?.ConveyorEndpoint?.GetLineCount() > 0).ToArray();
        var groups = new List<HashSet<long>>();
        var remaining = new HashSet<long>(descriptors.Select(descriptor => descriptor.OwnerEntityId));
        var endpoints = descriptors.GroupBy(descriptor => descriptor.OwnerEntityId)
            .ToDictionary(group => group.Key, group => Resolve(group.First().Owner));
        var searches = 0;
        while (remaining.Count > 0)
        {
            if (maxSearches - searches < 2) throw new InvalidOperationException("Conveyor network search budget exceeded.");
            searches += 2;
            var root = remaining.Min();
            var group = new HashSet<long> { root };
            var reachable = new List<IMyConveyorEndpoint>();
            MyGridConveyorSystem.FindReachable(endpoints[root].ConveyorEndpoint, reachable);
            MyGridConveyorSystem.FindReachableInverted(endpoints[root].ConveyorEndpoint, reachable);
            foreach (var pair in endpoints)
                if (reachable.Contains(pair.Value.ConveyorEndpoint)) group.Add(pair.Key);
            // Opposing sorter branches may share a sink: merge overlapping views on both runtimes.
            foreach (var previous in groups.Where(previous => previous.Overlaps(group)).ToArray())
            {
                group.UnionWith(previous);
                groups.Remove(previous);
            }
            remaining.ExceptWith(group);
            groups.Add(group);
        }
        return groups.OrderBy(group => group.Min()).ToArray();
    }

    private static IMyConveyorEndpointBlock Resolve(MyCubeBlock block) => block is IMyConveyorEndpointBlock endpoint ? endpoint :
        block.Components.TryGet<IMyConveyorEndpointBlock>(out var component) ? component : null;
}
