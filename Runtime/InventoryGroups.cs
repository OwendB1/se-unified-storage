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
        InventoryGroupRecord group, out string error, MyDefinitionId? item = null, InventoryRoleKind? role = null)
    {
        var rules = ResolveRules(scope, group, out error);
        return rules.SelectMany(match => match.Members.Where(member =>
                member.Roles.Any(candidate => match.Rule.AcceptsRole(candidate.Kind) &&
                    (!role.HasValue || candidate.Kind == role.Value) &&
                    (!item.HasValue || candidate.Accepts(item.Value) && Accepts(match.Rule, item.Value)))))
            .GroupBy(member => member.Inventory).Select(members => members.First()).ToArray();
    }

    private static List<(InventoryGroupRule Rule, HashSet<InventoryDescriptor> Members)> ResolveRules(
        MechanicalInventoryScope scope, InventoryGroupRecord group, out string error)
    {
        var result = new List<(InventoryGroupRule, HashSet<InventoryDescriptor>)>();
        error = null;
        if (group == null)
        {
            error = "Group not found";
            return result;
        }
        IReadOnlyDictionary<string, HashSet<long>> named = null;
        foreach (var rule in group.EffectiveRules)
        {
            HashSet<long> namedIds = null;
            if (rule.Selector == InventoryGroupSelector.TerminalGroup &&
                !(named ??= NamedGroups(scope)).TryGetValue(rule.Value ?? string.Empty, out namedIds))
            {
                // Fail closed, including other rows: loadouts must not redistribute a missing row's stock.
                error = "Terminal group not found: " + rule.Value;
                result.Clear();
                return result;
            }
            result.Add((rule, new HashSet<InventoryDescriptor>(scope.Inventories.Where(member =>
                !member.Owner.Closed && member.Roles.Any(role => rule.AcceptsRole(role.Kind)) &&
                Matches(rule, member, namedIds)))));
        }
        return result;
    }

    private static bool Matches(InventoryGroupRule rule, InventoryDescriptor member, HashSet<long> namedIds) =>
            rule.Selector switch
            {
                InventoryGroupSelector.All => true,
                InventoryGroupSelector.Family => member.Section.Kind == rule.Family,
                InventoryGroupSelector.BlockType => member.BlockDefinitionId.TypeId.ToString() == rule.Value,
                InventoryGroupSelector.BlockDefinition => member.BlockDefinitionId.ToString() == rule.Value,
                InventoryGroupSelector.TerminalGroup => namedIds.Contains(member.OwnerEntityId),
                InventoryGroupSelector.Block => member.OwnerEntityId.ToString(System.Globalization.CultureInfo.InvariantCulture) == rule.Value,
                InventoryGroupSelector.RecipeOutput => member.Owner.BlockDefinition is MyProductionBlockDefinition production &&
                    production.BlueprintClasses.SelectMany(blueprints => blueprints)
                        .Any(blueprint => blueprint.Results.Any(result => result.Id.ToString() == rule.Value)),
                _ => false
            };

    public static bool Accepts(InventoryGroupRecord group, MyDefinitionId item) => group == null ||
        group.EffectiveRules.Any(rule => Accepts(rule, item));

    private static bool Accepts(InventoryGroupRule rule, MyDefinitionId item) =>
        rule.AcceptsItem(item.TypeId.ToString(), item.ToString());

    public static InventoryProjection Build(InventoryProjection source, ScopeProfile profile)
    {
        InventoryGroupRecord.Migrate(profile);
        var result = new List<InventoryRoleProjection>();
        foreach (var group in profile.Groups)
        {
            var matches = ResolveRules(source.Scope, group, out _);
            bool Includes(InventoryDescriptor member, InventoryRoleKind role) =>
                matches.Any(match => match.Members.Contains(member) && match.Rule.AcceptsRole(role));
            bool AcceptsMember(InventoryDescriptor member, InventoryRoleKind role, MyDefinitionId item) =>
                matches.Any(match => match.Members.Contains(member) && match.Rule.AcceptsRole(role) && Accepts(match.Rule, item));
            var members = new HashSet<InventoryDescriptor>(matches.SelectMany(match => match.Members));
            var rawRoles = source.Roles.Where(role => role.Members.Any(member => Includes(member, role.Role))).ToArray();
            // Unknown definitions keep their safe inventory/constraint separation by default.
            var fallback = group.EffectiveRules.Any(rule => rule.Selector == InventoryGroupSelector.Family && rule.Family == InventorySectionKind.DefinitionFallback);
            var family = members.Select(member => member.Section.Kind).Distinct().ToArray();
            foreach (var bucket in rawRoles.GroupBy(role => (Section: fallback ? role.Section :
                         InventorySectionKey.Semantic(family.Length == 1 ? family[0] : InventorySectionKind.DefinitionFallback), role.Role)))
            {
                var selected = bucket.SelectMany(role => role.Members).Where(member => Includes(member, bucket.Key.Role)).Distinct().ToArray();
                var selectedSet = new HashSet<InventoryDescriptor>(selected);
                var stacks = new List<ProjectedInventoryStack>();
                var seen = new HashSet<(long, int, uint)>();
                foreach (var reference in bucket.SelectMany(role => role.Stacks).SelectMany(stack => stack.Sources))
                {
                    if (!selectedSet.Contains(reference.Descriptor) || !AcceptsMember(reference.Descriptor, bucket.Key.Role, reference.DefinitionId) ||
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
                    inventories.Aggregate(MyFixedPoint.Zero, (sum, inv) => sum + inv.MaxVolume), group,
                    (member, item) => AcceptsMember(member, bucket.Key.Role, item)));
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
                var matches = ResolveRules(scope, group, out var error);
                if (error != null) return null;
                // Length prefix strings so names containing separators cannot mask a change.
                string Text(string value) => (value?.Length ?? 0) + ":" + value;
                parts.Add(Text(group.Id));
                foreach (var match in matches)
                {
                    var rule = match.Rule;
                    parts.Add(string.Join("|", rule.Selector, rule.Family, Text(rule.Value),
                        rule.AllRoles, rule.Role, Text(rule.ItemType), Text(rule.ItemDefinitionId),
                        string.Join(",", match.Members.Select(member => $"{member.OwnerEntityId}:{member.InventoryIndex}").OrderBy(v => v))));
                }
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
