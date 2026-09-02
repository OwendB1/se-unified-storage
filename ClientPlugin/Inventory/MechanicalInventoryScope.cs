using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using VRage.Game.Entity;

namespace ClientPlugin.Inventory;

public sealed class MechanicalInventoryScope
{
    internal MechanicalInventoryScope(
        MyEntity interactedEntity,
        MyCubeGrid anchorGrid,
        IReadOnlyList<MyCubeGrid> grids,
        IReadOnlyList<InventoryDescriptor> inventories)
    {
        InteractedEntity = interactedEntity;
        AnchorGrid = anchorGrid;
        Grids = grids;
        Inventories = inventories;
    }

    public MyEntity InteractedEntity { get; }
    public MyCubeGrid AnchorGrid { get; }
    public IReadOnlyList<MyCubeGrid> Grids { get; }
    public IReadOnlyList<InventoryDescriptor> Inventories { get; }
}

public sealed class MechanicalInventoryScopeScanner
{
    private readonly MethodInfo getGridInventories;

    public MechanicalInventoryScopeScanner()
    {
        getGridInventories = typeof(MyGridConveyorSystem).GetMethod(
            "GetGridInventories",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(MyEntity), typeof(List<MyEntity>), typeof(long) },
            modifiers: null) ?? throw new MissingMethodException(
            typeof(MyGridConveyorSystem).FullName,
            "GetGridInventories(MyEntity, List<MyEntity>, long)");

#if DEBUG
        InventoryDescriptorFactory.RunSelfTest();
#endif
    }

    public MechanicalInventoryScope Capture(MyEntity interactedEntity, long identityId)
    {
        var anchorGrid = GetGrid(interactedEntity);
        if (anchorGrid == null)
            throw new ArgumentException("The interacted entity does not belong to a cube grid.", nameof(interactedEntity));
        return Capture(interactedEntity, anchorGrid, identityId);
    }

    public MechanicalInventoryScope Capture(MyEntity interactedEntity, MyCubeGrid anchorGrid, long identityId)
    {
        if (interactedEntity == null)
            throw new ArgumentNullException(nameof(interactedEntity));
        if (anchorGrid == null)
            throw new ArgumentNullException(nameof(anchorGrid));

        var grids = MyCubeGridGroups.Static?.Mechanical.GetGroupNodes(anchorGrid) ?? new List<MyCubeGrid> { anchorGrid };
        grids.Sort((left, right) => left.EntityId.CompareTo(right.EntityId));

        var owners = new List<MyEntity>();
        foreach (var grid in grids)
        {
            var conveyorSystem = grid.GridSystems?.ConveyorSystem;
            if (conveyorSystem != null)
                getGridInventories.Invoke(conveyorSystem, new object[] { interactedEntity, owners, identityId });
        }

        owners.Sort((left, right) => left.EntityId.CompareTo(right.EntityId));
        var seenOwners = new HashSet<long>();
        var inventories = new List<InventoryDescriptor>();
        foreach (var owner in owners)
        {
            if (owner is not MyCubeBlock block || !seenOwners.Add(owner.EntityId))
                continue;

            for (var inventoryIndex = 0; inventoryIndex < owner.InventoryCount; inventoryIndex++)
            {
                if (owner.GetInventoryBase(inventoryIndex) is MyInventory inventory)
                    inventories.Add(InventoryDescriptorFactory.Create(block, inventoryIndex, inventory));
            }
        }

        return new MechanicalInventoryScope(interactedEntity, anchorGrid, grids.ToArray(), inventories.ToArray());
    }

    public IReadOnlyList<MyCubeGrid> GetConnectedMechanicalAnchors(MyEntity interactedEntity)
    {
        var anchorGrid = GetGrid(interactedEntity);
        if (anchorGrid == null)
            return Array.Empty<MyCubeGrid>();
        var logical = MyCubeGridGroups.Static?.Logical.GetGroupNodes(anchorGrid) ?? new List<MyCubeGrid> { anchorGrid };
        var seenGroups = new HashSet<long>();
        var result = new List<MyCubeGrid>();
        foreach (var grid in logical.OrderBy(grid => grid.EntityId))
        {
            var mechanical = MyCubeGridGroups.Static?.Mechanical.GetGroupNodes(grid) ?? new List<MyCubeGrid> { grid };
            var groupKey = mechanical.Min(member => member.EntityId);
            if (seenGroups.Add(groupKey))
                result.Add(grid);
        }
        return result;
    }

    public long GetMechanicalGroupKey(MyCubeGrid grid)
    {
        if (grid == null)
            return 0L;
        var mechanical = MyCubeGridGroups.Static?.Mechanical.GetGroupNodes(grid) ?? new List<MyCubeGrid> { grid };
        return mechanical.Count == 0 ? grid.EntityId : mechanical.Min(member => member.EntityId);
    }

    private static MyCubeGrid GetGrid(MyEntity entity) =>
        (entity as MyCubeBlock)?.CubeGrid ?? entity as MyCubeGrid ?? entity?.Parent as MyCubeGrid;
}
