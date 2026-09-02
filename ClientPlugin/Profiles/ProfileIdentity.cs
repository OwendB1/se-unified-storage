using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;

namespace ClientPlugin.Profiles;

public static class ProfileIdentity
{
    public static string CurrentWorld => MySession.Static == null
        ? string.Empty
        : $"{Sync.ServerId}:{MySession.Static.WorldId:N}";
}
