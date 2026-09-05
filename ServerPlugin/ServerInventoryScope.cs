using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Inventory;
using ClientPlugin.Profiles;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Shared.Companion;
using VRage.Game;

namespace ServerPlugin;

internal static class ServerInventoryScope
{
    public static MechanicalInventoryScope Capture(MyCubeGrid anchor, Func<MyCubeBlock, bool> access, int maximum)
    {
        if (anchor == null || anchor.MarkedForClose) throw new InvalidOperationException("Anchor unavailable.");
        var grids = MyCubeGridGroups.Static.Mechanical.GetGroupNodes(anchor) ?? new List<MyCubeGrid> { anchor };
        if (grids.Count > 128) throw new InvalidOperationException("Too many mechanical grids.");
        var blocks = grids.SelectMany(grid => grid.GetFatBlocks()).Take(8193).ToArray();
        if (blocks.Length > 8192) throw new InvalidOperationException("Too many blocks.");
        var members = new List<InventoryDescriptor>();
        var stacks = 0;
        foreach (var block in blocks.Where(block => block.HasInventory && access(block)))
        for (var index = 0; index < block.InventoryCount; index++)
            if (block.GetInventoryBase(index) is MyInventory inventory)
            {
                members.Add(InventoryDescriptorFactory.Create(block, index, inventory));
                stacks += inventory.GetItems().Count;
                if (members.Count > Math.Max(1, Math.Min(1024, maximum)) || stacks > 8192)
                    throw new InvalidOperationException("Scope work limit reached.");
            }
        return new MechanicalInventoryScope(anchor, anchor, grids.ToArray(), members);
    }

    // Offline jobs use the recorded principal, not a server admin or the requesting client's identity.
    public static bool PrincipalAccess(MyCubeBlock block, long identity, bool faction = false) => block != null && !block.MarkedForClose &&
        (block.GetUserRelationToOwner(identity) == MyRelationsBetweenPlayerAndBlock.Owner ||
         faction && block.GetUserRelationToOwner(identity) == MyRelationsBetweenPlayerAndBlock.FactionShare);

    public static InventoryManagementFlags Flags(ScopeProfile settings, InventoryDescriptor member) =>
        settings.GetFlags(member.OwnerEntityId, member.InventoryIndex) |
        (settings.InventoryManagement.Any(record => record.BlockEntityId == member.OwnerEntityId &&
            (record.Flags & InventoryManagementFlags.ManualBlock) != 0) ? InventoryManagementFlags.ManualBlock : 0);

    public static bool Excluded(ScopeProfile settings, InventoryDescriptor member, bool destination = false) =>
        (Flags(settings, member) & (InventoryManagementFlags.ManualBlock | InventoryManagementFlags.ReservedInventory |
            (destination && member.Section.Kind == InventorySectionKind.UnifiedCargo ? InventoryManagementFlags.NoUnifiedCargoDestination : 0))) != 0;

    public static void Restrict(ScopeProfile local, ScopeProfile shared)
    {
        if (shared == null) return;
        foreach (var record in shared.InventoryManagement)
            local.SetFlags(record.BlockEntityId, record.InventoryIndex,
                local.GetFlags(record.BlockEntityId, record.InventoryIndex) | record.Flags);
    }
}
