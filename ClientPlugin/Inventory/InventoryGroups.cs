using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Profiles;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;

namespace ClientPlugin.Inventory;

public static class InventoryGroups
{
    public static IReadOnlyDictionary<string, HashSet<long>> NamedGroups(MechanicalInventoryScope scope)
    {
        var result = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        var grids = new HashSet<long>(scope.Grids.Select(grid => grid.EntityId));
        foreach (var system in scope.Grids.Select(grid => grid.GridSystems?.TerminalSystem).Where(s => s != null).Distinct())
        foreach (var group in system.BlockGroups)
        {
            var name = group.Name.ToString();
            var blocks = new List<IMyTerminalBlock>();
            ((IMyBlockGroup)group).GetBlocks(blocks);
            // Terminal systems can include docked ships. Never resolve outside this mechanical ship.
            var local = blocks.Where(block => grids.Contains(block.CubeGrid.EntityId)).ToArray();
            if (local.Length == 0)
                continue;
            if (!result.TryGetValue(name, out var ids))
                result[name] = ids = new HashSet<long>();
            ids.UnionWith(local.Select(block => block.EntityId));
        }
        return result;
    }

    public static IReadOnlyList<InventoryDescriptor> Resolve(MechanicalInventoryScope scope,
        InventoryGroupRecord group, out string error)
    {
        error = null;
        if (group == null)
        {
            error = "Group not found";
            return Array.Empty<InventoryDescriptor>();
        }
        HashSet<long> namedIds = null;
        if (group.Selector == InventoryGroupSelector.TerminalGroup &&
            !NamedGroups(scope).TryGetValue(group.Value ?? string.Empty, out namedIds))
        {
            error = "Group not found";
            return Array.Empty<InventoryDescriptor>();
        }
        return scope.Inventories.Where(member => !member.Owner.Closed &&
            (group.AllRoles || member.Roles.Any(role => role.Kind == group.Role)) &&
            (group.Selector switch
            {
                InventoryGroupSelector.All => true,
                InventoryGroupSelector.Family => member.Section.Kind == group.Family,
                InventoryGroupSelector.BlockType => member.BlockDefinitionId.TypeId.ToString() == group.Value,
                InventoryGroupSelector.BlockDefinition => member.BlockDefinitionId.ToString() == group.Value,
                InventoryGroupSelector.TerminalGroup => namedIds.Contains(member.OwnerEntityId),
                InventoryGroupSelector.Block => member.OwnerEntityId.ToString(System.Globalization.CultureInfo.InvariantCulture) == group.Value,
                InventoryGroupSelector.RecipeOutput => member.Owner.BlockDefinition is MyProductionBlockDefinition production &&
                    production.BlueprintClasses.SelectMany(blueprints => blueprints)
                        .Any(blueprint => blueprint.Results.Any(result => result.Id.ToString() == group.Value)),
                _ => false
            })).GroupBy(member => member.Inventory).Select(members => members.First()).ToArray();
    }

    public static bool Accepts(InventoryGroupRecord group, MyDefinitionId item) => group == null ||
        (string.IsNullOrEmpty(group.ItemType) || group.ItemType == item.TypeId.ToString()) &&
        (string.IsNullOrEmpty(group.ItemDefinitionId) || group.ItemDefinitionId == item.ToString());

    public static InventoryProjection Build(InventoryProjection source, ScopeProfile profile)
    {
        InventoryGroupRecord.Migrate(profile);
        var result = new List<InventoryRoleProjection>();
        foreach (var group in profile.Groups)
        {
            var members = new HashSet<InventoryDescriptor>(Resolve(source.Scope, group, out _));
            var rawRoles = source.Roles.Where(role => group.AllRoles || role.Role == group.Role)
                .Where(role => role.Members.Any(members.Contains)).ToArray();
            // Unknown definitions keep their safe inventory/constraint separation by default.
            var fallback = group.Selector == InventoryGroupSelector.Family && group.Family == InventorySectionKind.DefinitionFallback;
            var family = members.Select(member => member.Section.Kind).Distinct().ToArray();
            foreach (var bucket in rawRoles.GroupBy(role => (Section: fallback ? role.Section :
                         InventorySectionKey.Semantic(family.Length == 1 ? family[0] : InventorySectionKind.DefinitionFallback), role.Role)))
            {
                var selected = bucket.SelectMany(role => role.Members).Where(members.Contains).Distinct().ToArray();
                var selectedSet = new HashSet<InventoryDescriptor>(selected);
                var stacks = new List<ProjectedInventoryStack>();
                var seen = new HashSet<(long, int, uint)>();
                foreach (var reference in bucket.SelectMany(role => role.Stacks).SelectMany(stack => stack.Sources))
                {
                    if (!selectedSet.Contains(reference.Descriptor) || !Accepts(group, reference.DefinitionId) ||
                        !seen.Add((reference.Descriptor.OwnerEntityId, reference.Descriptor.InventoryIndex, reference.ItemId)))
                        continue;
                    var item = reference.Inventory.GetItemByID(reference.ItemId);
                    if (!item.HasValue) continue;
                    var stack = stacks.FirstOrDefault(candidate => candidate.CanStack(item.Value));
                    if (stack == null) stacks.Add(stack = new ProjectedInventoryStack(item.Value));
                    stack.Add(reference.Descriptor, item.Value);
                }
                var inventories = selected.Select(member => member.Inventory).Distinct().ToArray();
                result.Add(new InventoryRoleProjection(bucket.Key.Section.InGroup(group.Id), bucket.Key.Role,
                    selected, stacks,
                    inventories.Aggregate(MyFixedPoint.Zero, (sum, inv) => sum + inv.CurrentMass),
                    inventories.Aggregate(MyFixedPoint.Zero, (sum, inv) => sum + inv.CurrentVolume),
                    inventories.Aggregate(MyFixedPoint.Zero, (sum, inv) => sum + inv.MaxVolume), group));
            }
        }
        return new InventoryProjection(source.Scope, result);
    }

    public static Func<bool> Guard(MechanicalInventoryScope scope, ScopeProfile profile, IEnumerable<string> groupIds)
    {
        var ids = groupIds.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToArray();
        string Signature()
        {
            var parts = new List<string>();
            foreach (var id in ids)
            {
                var group = profile.Groups.FirstOrDefault(candidate => candidate.Id == id);
                var members = Resolve(scope, group, out var error);
                if (error != null) return null;
                parts.Add(string.Join("|", group.Id, group.Selector, group.Family, group.Value,
                    group.AllRoles, group.Role, group.ItemType, group.ItemDefinitionId,
                    string.Join(",", members.Select(member => $"{member.OwnerEntityId}:{member.InventoryIndex}").OrderBy(v => v))));
            }
            return string.Join("\n", parts);
        }
        var original = Signature();
        var grids = scope.Grids.Select(grid => grid.EntityId).OrderBy(id => id).ToArray();
        return () => original != null && !scope.AnchorGrid.Closed &&
            (MyCubeGridGroups.Static?.Mechanical.GetGroupNodes(scope.AnchorGrid) ?? new List<MyCubeGrid> { scope.AnchorGrid })
                .Select(grid => grid.EntityId).OrderBy(id => id).SequenceEqual(grids) && Signature() == original;
    }
}
