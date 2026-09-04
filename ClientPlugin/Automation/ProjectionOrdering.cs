using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Inventory;

namespace ClientPlugin.Automation;

public static class ProjectionOrdering
{
    public static InventoryProjection ApplyRefineryPriority(
        InventoryProjection projection,
        RefineryPriorityModel model)
    {
        if (projection == null)
            throw new ArgumentNullException(nameof(projection));
        if (model == null)
            return projection;
        var ranks = model.OrderedInputs.Select((id, index) => (id, index))
            .ToDictionary(pair => pair.id, pair => pair.index);
        var roles = projection.Roles.Select(role =>
        {
            if (!role.Members.Any(member => member.Owner is Sandbox.Game.Entities.Cube.MyRefinery) ||
                role.Role != InventoryRoleKind.ProductionInput)
                return role;
            var stacks = role.Stacks.OrderBy(stack =>
                    ranks.TryGetValue(stack.DefinitionId, out var rank) ? rank : int.MaxValue)
                .ThenBy(stack => stack.DefinitionId.ToString(), StringComparer.Ordinal)
                .ToArray();
            return new InventoryRoleProjection(
                role.Section,
                role.Role,
                role.Members,
                stacks,
                role.CurrentMass,
                role.CurrentVolume,
                role.MaxVolume, role.Group);
        }).ToArray();
        return new InventoryProjection(projection.Scope, roles);
    }
}
