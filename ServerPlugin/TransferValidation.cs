using System;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using Sandbox.Game.World;
using Shared.Companion;
using VRage.Game;
using VRage.Game.Entity;

namespace ServerPlugin;

internal sealed class TransferValidation
{
    private readonly ProfilePermissions permissions;
    public TransferValidation(ProfilePermissions permissions) => this.permissions = permissions;

    public bool HasAccess(MyInventory inventory, ulong sender, long identity)
    {
        if (inventory?.Owner == null || inventory.Owner.MarkedForClose) return false;
        var attached = false;
        for (var index = 0; index < inventory.Owner.InventoryCount; index++)
            attached |= ReferenceEquals(inventory.Owner.GetInventoryBase(index), inventory);
        if (!attached) return false;
        if (inventory.Owner is MyCharacter character)
            return ReferenceEquals(character, MySession.Static.Players.TryGetIdentity(identity)?.Character);
        return inventory.Owner is MyCubeBlock block && permissions.HasAccess(block, sender);
    }

    public bool CanTransfer(MyInventory source, MyInventory destination, MyCubeBlock terminal,
        ulong sender, long identity, MyDefinitionId item, out TransferFailure failure)
    {
        failure = TransferFailure.AccessDenied;
        if (!HasAccess(source, sender, identity) || !HasAccess(destination, sender, identity)) return false;
        // Deliberately do not inherit vanilla's blanket destination-side admin bypass.
        if (source.Owner is MyCubeBlock a && destination.Owner is MyCubeBlock b &&
            a.CubeGrid.IsInSameLogicalGroupAs(b.CubeGrid) &&
            (a.PositionComp.GetPosition() - b.PositionComp.GetPosition()).LengthSquared() > 4000000d) return false;
        failure = TransferFailure.NoConveyorPath;
        if (ReferenceEquals(source, destination)) return false;
        if (ReferenceEquals(source.Owner, destination.Owner)) return true;
        if ((source.IsCharacterOwner || destination.IsCharacterOwner) && !PhysicalTerminal(terminal, sender, identity)) return false;
        var from = Endpoint(source.IsCharacterOwner ? terminal : source.Owner);
        var to = Endpoint(destination.IsCharacterOwner ? terminal : destination.Owner);
        if (from == null || to == null) return false;
        return MyGridConveyorSystem.ComputeCanTransfer(from, to, item) &&
            MyGridConveyorSystem.Reachable(from, to, identity, item) &&
            MyGridConveyorSystem.Reachable(from.ConveyorEndpoint, to.ConveyorEndpoint);
    }

    private bool PhysicalTerminal(MyCubeBlock terminal, ulong sender, long identity)
    {
        var character = MySession.Static.Players.TryGetIdentity(identity)?.Character;
        var distance = MyConstants.DEFAULT_INTERACTIVE_DISTANCE * 3d;
        return character != null && permissions.HasAccess(terminal, sender) &&
            terminal.PositionComp.WorldAABB.DistanceSquared(character.PositionComp.GetPosition()) <= distance * distance;
    }

    public static IMyConveyorEndpointBlock Endpoint(MyEntity owner) => owner is IMyConveyorEndpointBlock endpoint ? endpoint :
        owner?.Components.TryGet<IMyConveyorEndpointBlock>(out var component) == true ? component : null;

    public static MyInventory Resolve(InventoryAddress address)
    {
        if (address == null || !MyEntities.TryGetEntityById(address.OwnerId, out var entity) ||
            entity.MarkedForClose || address.Index < 0 || address.Index >= entity.InventoryCount) return null;
        return entity.GetInventoryBase(address.Index) as MyInventory;
    }
}
