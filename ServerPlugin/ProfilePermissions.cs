using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Shared.Companion;
using VRage.Network;

namespace ServerPlugin;

internal sealed class ProfilePermissions
{
    // Reuse the game's exact access/ownership boundary instead of approximating remote terminal access.
    private readonly Func<MyCubeBlock, EndpointId, ValidationType, ValidationResult> hasRights;

    public ProfilePermissions()
    {
        var type = typeof(MyCubeGrid).Assembly.GetType("Sandbox.Game.Replication.MyReplicableRightsValidator", true);
        var method = AccessTools.Method(type, "HasRights", new[] { typeof(MyCubeBlock), typeof(EndpointId), typeof(ValidationType) });
        hasRights = (Func<MyCubeBlock, EndpointId, ValidationType, ValidationResult>)Delegate.CreateDelegate(
            typeof(Func<MyCubeBlock, EndpointId, ValidationType, ValidationResult>), method);
    }

    public bool TryResolve(ulong sender, CompanionMessage request, out long identity, out MyCubeGrid anchor, out HashSet<long> gridIds)
    {
        identity = MySession.Static.Players.TryGetIdentityId(sender);
        anchor = null; gridIds = null;
        if (identity == 0 || !MySession.Static.Players.TryGetPlayerBySteamId(sender, out _) ||
            !MyEntities.TryGetEntityById(request.AnchorEntityId, out var entity) || entity is not MyCubeGrid grid || grid.MarkedForClose ||
            !MyEntities.TryGetEntityById(request.TerminalEntityId, out var terminalEntity) ||
            terminalEntity is not MyTerminalBlock terminal || terminal.MarkedForClose)
            return false;
        var group = MyCubeGridGroups.Static.Mechanical.GetGroup(grid);
        gridIds = group == null ? new HashSet<long> { grid.EntityId } : new HashSet<long>(group.Nodes.Select(n => n.NodeData.EntityId));
        if (!gridIds.Contains(terminal.CubeGrid.EntityId) ||
            hasRights(terminal, new EndpointId(sender), ValidationType.Access | ValidationType.Ownership) != ValidationResult.Passed)
            return false;
        anchor = grid;
        return true;
    }

    public static bool CanRead(SharedScopeProfile profile, long identity, bool allowFaction)
    {
        if (!MyEntities.TryGetEntityById(profile.AnchorEntityId, out var entity) || entity is not MyCubeGrid anchor ||
            anchor.MarkedForClose || !anchor.BigOwners.Contains(profile.OwnerIdentityId)) return false;
        if (profile.OwnerIdentityId == identity) return true;
        if (!allowFaction || !profile.FactionShared) return false;
        var ownerFaction = MySession.Static.Factions.TryGetPlayerFaction(profile.OwnerIdentityId);
        return ownerFaction != null && ownerFaction.FactionId == MySession.Static.Factions.TryGetPlayerFaction(identity)?.FactionId;
    }
}
