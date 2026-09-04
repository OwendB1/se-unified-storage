using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using Sandbox.ModAPI;

namespace ClientPlugin.Inventory;

public sealed class InventoryProjectionView
{
    public InventoryProjectionView(string id, string name, InventoryProjection projection)
    {
        Id = id;
        Name = name;
        Projection = projection;
    }

    public string Id { get; }
    public string Name { get; }
    public InventoryProjection Projection { get; }
}

public static class ProjectionViewBuilder
{
    public static IReadOnlyList<InventoryProjectionView> Build(
        MechanicalInventorySession session,
        InventoryProjection projection,
        InventoryScopeMode mode)
    {
        return mode switch
        {
            InventoryScopeMode.BlockGroups => BuildBlockGroups(session, projection),
            InventoryScopeMode.ConveyorComponents => BuildConveyorComponents(session, projection),
            _ => new[]
            {
                new InventoryProjectionView(
                    $"mechanical:{session.Scope.Grids.Min(grid => grid.EntityId)}",
                    session.Scope.AnchorGrid.DisplayName,
                    projection)
            }
        };
    }

    private static IReadOnlyList<InventoryProjectionView> BuildBlockGroups(
        MechanicalInventorySession session,
        InventoryProjection projection)
    {
        var result = new List<InventoryProjectionView>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grid in session.Scope.Grids)
        foreach (var group in grid.GridSystems?.TerminalSystem?.BlockGroups ?? Enumerable.Empty<MyBlockGroup>())
        {
            var blocks = new List<IMyTerminalBlock>();
            ((Sandbox.ModAPI.IMyBlockGroup)group).GetBlocks(blocks);
            var ids = new HashSet<long>(blocks.Select(block => block.EntityId));
            if (ids.Count == 0)
                continue;
            var key = $"group:{grid.EntityId}:{group.Name}";
            if (!seen.Add(key))
                continue;
            var filtered = Filter(projection, descriptor => ids.Contains(descriptor.OwnerEntityId));
            if (filtered.Roles.Count > 0)
                result.Add(new InventoryProjectionView(key, group.Name.ToString(), filtered));
        }
        return result.Count > 0
            ? result
            : Build(session, projection, InventoryScopeMode.MechanicalGroups);
    }

    private static IReadOnlyList<InventoryProjectionView> BuildConveyorComponents(
        MechanicalInventorySession session,
        InventoryProjection projection)
    {
        return session.GetConveyorNetworks().Select((ids, index) => new InventoryProjectionView(
                $"conveyor:{session.Scope.Grids.Min(grid => grid.EntityId)}:{ids.Min()}",
                $"Network {index + 1}",
                Filter(projection, descriptor => ids.Contains(descriptor.OwnerEntityId))))
            .Where(view => view.Projection.Roles.Count > 0)
            .ToArray();
    }

    internal static IReadOnlyList<HashSet<long>> FindConveyorNetworks(MechanicalInventoryScope scope)
    {
        // A flight seat can implement the endpoint interface without any model
        // conveyor ports. Line count includes unconnected ports, not just links.
        var descriptors = scope.Inventories
            .Where(descriptor => Resolve(descriptor.Owner)?.ConveyorEndpoint?.GetLineCount() > 0).ToArray();
        var endpointGroups = new List<HashSet<long>>();
        var remaining = new HashSet<long>(descriptors.Select(descriptor => descriptor.OwnerEntityId));
        var endpoints = descriptors.GroupBy(descriptor => descriptor.OwnerEntityId)
            .Select(group => (Id: group.Key, Endpoint: Resolve(group.First().Owner)))
            .ToDictionary(pair => pair.Id, pair => pair.Endpoint);
        while (remaining.Count > 0)
        {
            var rootId = remaining.Min();
            var group = new HashSet<long> { rootId };
            var endpoint = endpoints[rootId];
            if (endpoint != null)
            {
                var reachable = new List<IMyConveyorEndpoint>();
                MyGridConveyorSystem.FindReachable(endpoint.ConveyorEndpoint, reachable);
                MyGridConveyorSystem.FindReachableInverted(endpoint.ConveyorEndpoint, reachable);
                foreach (var pair in endpoints)
                    if (pair.Value != null && reachable.Contains(pair.Value.ConveyorEndpoint))
                        group.Add(pair.Key);
            }
            // Opposing sorter branches can reach the same inventory. Merge those
            // components so a physical inventory never appears in two networks.
            foreach (var previous in endpointGroups.Where(previous => previous.Overlaps(group)).ToArray())
            {
                group.UnionWith(previous);
                endpointGroups.Remove(previous);
            }
            remaining.ExceptWith(group);
            endpointGroups.Add(group);
        }
        return endpointGroups.OrderBy(ids => ids.Min()).ToArray();
    }

    private static IMyConveyorEndpointBlock Resolve(Sandbox.Game.Entities.MyCubeBlock block)
    {
        if (block is IMyConveyorEndpointBlock endpoint)
            return endpoint;
        return block.Components.TryGet<IMyConveyorEndpointBlock>(out var component) ? component : null;
    }

    private static InventoryProjection Filter(
        InventoryProjection source,
        Func<InventoryDescriptor, bool> include)
    {
        var roles = source.Roles.Select(role =>
        {
            var members = role.Members.Where(include).ToArray();
            if (members.Length == 0)
                return null;
            var memberSet = new HashSet<InventoryDescriptor>(members);
            var stacks = role.Stacks.Select(stack => FilterStack(stack, memberSet))
                .Where(stack => stack != null).ToArray();
            var inventories = members.Select(member => member.Inventory).Distinct().ToArray();
            return new InventoryRoleProjection(
                role.Section,
                role.Role,
                members,
                stacks,
                inventories.Aggregate(VRage.MyFixedPoint.Zero, (sum, inventory) => sum + inventory.CurrentMass),
                inventories.Aggregate(VRage.MyFixedPoint.Zero, (sum, inventory) => sum + inventory.CurrentVolume),
                inventories.Aggregate(VRage.MyFixedPoint.Zero, (sum, inventory) => sum + inventory.MaxVolume), role.Group);
        }).Where(role => role != null).ToArray();
        return new InventoryProjection(source.Scope, roles);
    }

    private static ProjectedInventoryStack FilterStack(
        ProjectedInventoryStack source,
        ISet<InventoryDescriptor> members)
    {
        ProjectedInventoryStack result = null;
        foreach (var stack in source.Sources.Where(stack => stack.Descriptor != null && members.Contains(stack.Descriptor)))
        {
            var item = stack.Inventory.GetItemByID(stack.ItemId);
            if (!item.HasValue)
                continue;
            result ??= new ProjectedInventoryStack(item.Value);
            result.Add(stack.Descriptor, item.Value);
        }
        return result;
    }
}
