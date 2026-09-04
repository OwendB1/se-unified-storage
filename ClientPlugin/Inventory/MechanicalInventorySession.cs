using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using VRage.Game.Entity;

namespace ClientPlugin.Inventory;

public sealed class MechanicalInventorySession : IDisposable
{
    private readonly MechanicalInventoryScopeScanner scanner;
    private readonly InventoryProjectionBuilder projectionBuilder;
    private readonly MyEntity interactedEntity;
    private readonly MyCubeGrid anchorGrid;
    private readonly long identityId;
    private readonly HashSet<MyGridConveyorSystem> conveyorSystems = new();
    private readonly HashSet<MyInventory> inventories = new();
    private bool disposed;
    private bool structureDirty = true;
    private bool contentsDirty = true;
    private string namedGroupSignature;
    private IReadOnlyList<HashSet<long>> conveyorNetworks;
    private string conveyorNetworkSignature;

    public MechanicalInventorySession(
        MechanicalInventoryScopeScanner scanner,
        MyEntity interactedEntity,
        long identityId,
        MyCubeGrid anchorGrid = null,
        InventoryProjectionBuilder projectionBuilder = null)
    {
        this.scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        this.interactedEntity = interactedEntity ?? throw new ArgumentNullException(nameof(interactedEntity));
        this.anchorGrid = anchorGrid;
        this.identityId = identityId;
        this.projectionBuilder = projectionBuilder ?? new InventoryProjectionBuilder();
    }

    public MechanicalInventoryScope Scope { get; private set; }
    public InventoryProjection Projection { get; private set; }
    public bool IsDirty => structureDirty || contentsDirty;

    public event Action Changed;

    public bool PollStructure()
    {
        ThrowIfDisposed();
        if (Scope == null || anchorGrid?.Closed == true || Scope.AnchorGrid.Closed)
        {
            MarkStructureDirty();
            return true;
        }
        var referenceGrid = anchorGrid ?? Scope.AnchorGrid;
        var current = MyCubeGridGroups.Static?.Mechanical.GetGroupNodes(referenceGrid) ??
            new List<MyCubeGrid> { referenceGrid };
        var changed = current.Count != Scope.Grids.Count ||
                      !current.Select(grid => grid.EntityId).OrderBy(id => id)
                          .SequenceEqual(Scope.Grids.Select(grid => grid.EntityId).OrderBy(id => id));
        if (changed)
            MarkStructureDirty();
        var names = string.Join("\n", InventoryGroups.NamedGroups(Scope).OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + ":" + string.Join(",", pair.Value.OrderBy(id => id))));
        if (names != namedGroupSignature)
        {
            namedGroupSignature = names;
            MarkContentsDirty();
        }
        if (!structureDirty && conveyorNetworks != null)
        {
            var networks = ProjectionViewBuilder.FindConveyorNetworks(Scope);
            var signature = string.Join(";", networks.Select(ids => string.Join(",", ids.OrderBy(id => id))));
            conveyorNetworks = networks;
            if (signature != conveyorNetworkSignature)
            {
                conveyorNetworkSignature = signature;
                MarkContentsDirty();
            }
        }
        return changed;
    }

    public IReadOnlyList<HashSet<long>> GetConveyorNetworks() =>
        conveyorNetworks ??= ProjectionViewBuilder.FindConveyorNetworks(Scope);

    public InventoryProjection Refresh(Func<InventoryDescriptor, bool> includeInventory = null)
    {
        ThrowIfDisposed();
        if (structureDirty || Scope == null)
        {
            Detach();
            Scope = anchorGrid == null
                ? scanner.Capture(interactedEntity, identityId)
                : scanner.Capture(interactedEntity, anchorGrid, identityId);
            Attach(Scope);
            conveyorNetworks = null;
            structureDirty = false;
            contentsDirty = true;
        }

        if (contentsDirty || Projection == null || includeInventory != null)
        {
            Projection = projectionBuilder.Build(Scope, includeInventory);
            contentsDirty = false;
        }
        return Projection;
    }

    public void MarkStructureDirty()
    {
        structureDirty = true;
        contentsDirty = true;
        Changed?.Invoke();
    }

    public void MarkContentsDirty()
    {
        contentsDirty = true;
        Changed?.Invoke();
    }

    private void Attach(MechanicalInventoryScope scope)
    {
        foreach (var grid in scope.Grids)
        {
            var conveyor = grid.GridSystems?.ConveyorSystem;
            if (conveyor == null || !conveyorSystems.Add(conveyor))
                continue;
            conveyor.BlockAdded += ConveyorChanged;
            conveyor.BlockRemoved += ConveyorChanged;
        }

        foreach (var descriptor in scope.Inventories)
        {
            if (!inventories.Add(descriptor.Inventory))
                continue;
            descriptor.Inventory.ContentsChanged += InventoryChanged;
        }
    }

    private void Detach()
    {
        foreach (var conveyor in conveyorSystems)
        {
            conveyor.BlockAdded -= ConveyorChanged;
            conveyor.BlockRemoved -= ConveyorChanged;
        }
        conveyorSystems.Clear();
        foreach (var inventory in inventories)
            inventory.ContentsChanged -= InventoryChanged;
        inventories.Clear();
    }

    private void ConveyorChanged(MyCubeBlock block) => MarkStructureDirty();
    private void InventoryChanged(MyInventoryBase inventory) => MarkContentsDirty();

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Detach();
        Changed = null;
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(MechanicalInventorySession));
    }
}
