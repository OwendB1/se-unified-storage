// Minimal game boundary for running the production adapter without launching SE.
// Client/server builds separately verify the adapter against the real game assemblies.
namespace VRage
{
    public struct MyTuple<T1, T2>
    {
        public T1 Item1;
        public T2 Item2;
    }

    public struct MyTuple<T1, T2, T3, T4>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public T4 Item4;
    }
}

namespace VRage.Game
{
    public readonly record struct MyDefinitionId(string TypeId, string SubtypeName);
}

namespace VRage.Game.ModAPI
{
    public interface IMyUtilities
    {
        void RegisterMessageHandler(long channel, Action<object> handler);
        void UnregisterMessageHandler(long channel, Action<object> handler);
        void SendModMessage(long channel, object payload);
    }
}

namespace Sandbox.ModAPI
{
    public static class MyAPIGateway
    {
        public static VRage.Game.ModAPI.IMyUtilities Utilities;
    }
}

namespace Sandbox.Game.World
{
    public sealed class MySession
    {
        public static MySession Static;
        public bool Ready;
        public static event Action OnUnloading;
        public static void Unload() => OnUnloading?.Invoke();
    }
}

namespace Sandbox.Definitions
{
    public class MyPhysicalItemDefinition;
    public sealed class MyAmmoMagazineDefinition : MyPhysicalItemDefinition;

    public sealed class MyDefinitionManager
    {
        public static MyDefinitionManager Static = new();
        public readonly Dictionary<VRage.Game.MyDefinitionId, MyPhysicalItemDefinition> Items = new();
        public bool TryGetPhysicalItemDefinition(VRage.Game.MyDefinitionId id, out MyPhysicalItemDefinition item) =>
            Items.TryGetValue(id, out item);
    }
}

namespace VRage.Utils
{
    public sealed class MyLog
    {
        public static MyLog Default = new();
        public readonly List<string> Messages = new();
        public void WriteLine(string message) => Messages.Add(message);
    }
}
