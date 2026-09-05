using ClientPlugin.Inventory;
using Sandbox.Definitions;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Utils;
using MagazineMap = System.Collections.Generic.Dictionary<VRage.Game.MyDefinitionId,
    System.Collections.Generic.List<VRage.MyTuple<int, VRage.MyTuple<VRage.Game.MyDefinitionId, string, string, bool>>>>;

internal static class WeaponCoreCompatibilityChecks
{
    private const long Channel = 67549756549;

    internal static void Run()
    {
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("WeaponCore: " + message);
        }
        void Poll()
        {
            for (var i = 0; i < 300; i++) WeaponCoreCompatibility.Update();
        }

        var gun = new MyDefinitionId("ConveyorSorter", "ModGun");
        var otherType = new MyDefinitionId("LargeGatlingTurret", "ModGun");
        var laser = new MyDefinitionId("CargoContainer", "Laser");
        var unmapped = new MyDefinitionId("SmallMissileLauncher", "Unmapped");
        var ammoA = new MyDefinitionId("AmmoMagazine", "A");
        var ammoB = new MyDefinitionId("AmmoMagazine", "B");
        var energy = new MyDefinitionId("AmmoMagazine", "Energy");
        var missing = new MyDefinitionId("AmmoMagazine", "Missing");
        var component = new MyDefinitionId("Component", "A");
        var known = new List<MyDefinitionId> { gun, laser, unmapped };
        var mappings = new MagazineMap
        {
            [gun] = new()
            {
                Entry(0, ammoA, "A"), Entry(1, ammoB, "B", true), Entry(2, ammoA, "A"),
                Entry(0, energy, "Energy"), Entry(0, ammoB, ""), Entry(0, component, "A"),
                Entry(0, missing, "Missing"), Entry(0, default, "Invalid")
            },
            [laser] = new() { Entry(0, energy, "Energy"), Entry(1, ammoA, "") },
            [otherType] = new() { Entry(0, ammoA, "A") }
        };
        MyDefinitionManager.Static.Items[ammoA] = new MyAmmoMagazineDefinition();
        MyDefinitionManager.Static.Items[ammoB] = new MyAmmoMagazineDefinition();
        MyDefinitionManager.Static.Items[energy] = new MyAmmoMagazineDefinition();
        MyDefinitionManager.Static.Items[component] = new MyPhysicalItemDefinition();
        var fail = false;
        var api = new Dictionary<string, Delegate>
        {
            ["GetCoreWeapons"] = new Action<ICollection<MyDefinitionId>>(ids =>
            {
                foreach (var definition in known) ids.Add(definition);
            }),
            ["GetAllWeaponMagazines"] = new Action<IDictionary<MyDefinitionId,
                List<MyTuple<int, MyTuple<MyDefinitionId, string, string, bool>>>>>(result =>
            {
                foreach (var mapping in mappings) result.Add(mapping.Key, mapping.Value);
                if (fail) throw new InvalidOperationException("provider failure after partial result");
            })
        };
        var bus = new ModMessages();
        MyAPIGateway.Utilities = bus;
        MySession.Static = new MySession();
        WeaponCoreCompatibility.Update();
        Check(bus.Handlers.Count == 0, "wait for ready world");
        MySession.Static.Ready = true;
        WeaponCoreCompatibility.Update();
        Check(bus.Requests == 1 && !WeaponCoreCompatibility.IsWeapon(gun), "absent API leaves vanilla discovery available");
        Poll();
        Check(bus.Requests == 2 && bus.Handlers.Count == 1, "bounded retries, one handler");

        bus.SendModMessage(Channel, "unrelated string");
        bus.SendModMessage(Channel, new object());
        bus.SendModMessage(Channel, api);
        WeaponCoreCompatibility.Update();
        Check(WeaponCoreCompatibility.IsWeapon(gun), "sorter-based definition recognized");
        Check(!WeaponCoreCompatibility.IsWeapon(otherType), "match full ID, ignore orphan magazine entries");
        Check(WeaponCoreCompatibility.Accepts(gun, ammoA) && WeaponCoreCompatibility.Accepts(gun, ammoB),
            "union all parts; SkipAimChecks does not exclude physical ammo");
        Check(!WeaponCoreCompatibility.Accepts(gun, energy) && !WeaponCoreCompatibility.Accepts(gun, component) &&
            !WeaponCoreCompatibility.Accepts(gun, missing) && !WeaponCoreCompatibility.Accepts(gun, default),
            "reject virtual, non-magazine, missing and invalid IDs");
        Check(WeaponCoreCompatibility.IsWeapon(laser) && !WeaponCoreCompatibility.Accepts(laser, ammoA) &&
            !WeaponCoreCompatibility.Accepts(laser, energy), "energy-only weapon has empty physical ammo set");
        Check(WeaponCoreCompatibility.IsWeapon(unmapped) && !WeaponCoreCompatibility.Accepts(unmapped, ammoA),
            "missing mapping stays a weapon, accepts no guessed vanilla ammo");

        var revision = WeaponCoreCompatibility.Revision;
        known.Reverse();
        mappings[gun].Reverse();
        Poll();
        Check(WeaponCoreCompatibility.Revision == revision, "provider order and duplicate ammo do not invalidate views");
        mappings[unmapped] = new() { Entry(0, ammoB, "B") };
        Poll();
        Check(WeaponCoreCompatibility.Accepts(unmapped, ammoB) && WeaponCoreCompatibility.Revision > revision,
            "late definition mapping refreshes cached views");

        fail = true;
        Poll();
        Check(WeaponCoreCompatibility.IsWeapon(gun) && !WeaponCoreCompatibility.Accepts(gun, ammoA),
            "provider failure retains ownership and rejects stale/partial ammo");
        Poll();
        Check(MyLog.Default.Messages.Count == 1, "provider failure logs once per world");
        fail = false;
        Poll();
        Check(WeaponCoreCompatibility.Accepts(gun, ammoA), "provider recovers on retry");

        var partialApi = new Dictionary<string, Delegate> { ["GetCoreWeapons"] = api["GetCoreWeapons"] };
        bus.Response = partialApi;
        bus.SendModMessage(Channel, partialApi);
        WeaponCoreCompatibility.Update();
        Check(WeaponCoreCompatibility.IsWeapon(gun) && !WeaponCoreCompatibility.Accepts(gun, ammoA),
            "missing magazine endpoint cannot fall back to vanilla ammo");
        var requests = bus.Requests;
        Poll();
        Check(bus.Requests == requests + 1, "synchronous incomplete response cannot cause request every frame");
        bus.Response = api;
        Poll();
        Check(WeaponCoreCompatibility.Accepts(gun, ammoA), "late compatible endpoint recovered through request");

        bus.Response = null;
        bus.SendModMessage(Channel, new Dictionary<string, Delegate>());
        WeaponCoreCompatibility.Update();
        Check(!WeaponCoreCompatibility.IsWeapon(gun), "API unload removes cached definitions");
        bus.SendModMessage(Channel, api);
        WeaponCoreCompatibility.Update();
        MySession.Unload();
        Check(bus.Handlers.Count == 0 && !WeaponCoreCompatibility.IsWeapon(gun), "world unload detaches and clears");

        MySession.Static = new MySession { Ready = true };
        WeaponCoreCompatibility.Update();
        Check(bus.Handlers.Count == 1 && !WeaponCoreCompatibility.IsWeapon(gun), "next vanilla world has no stale API");
        bus.SendModMessage(Channel, api);
        WeaponCoreCompatibility.Update();
        MySession.Static = new MySession { Ready = true };
        WeaponCoreCompatibility.Update();
        Check(bus.Handlers.Count == 1 && !WeaponCoreCompatibility.IsWeapon(gun), "session replacement resets even without unload event");
        WeaponCoreCompatibility.Reset();
        WeaponCoreCompatibility.Reset();
        Check(bus.Handlers.Count == 0, "repeated disposal safe");
        MySession.Static = null;
        MyAPIGateway.Utilities = null;
        Console.WriteLine("WeaponCore API checks passed.");
    }

    private static MyTuple<int, MyTuple<MyDefinitionId, string, string, bool>> Entry(
        int part, MyDefinitionId id, string magazine, bool skipAimChecks = false) => new()
    {
        Item1 = part,
        Item2 = new() { Item1 = id, Item2 = magazine, Item3 = "Round", Item4 = skipAimChecks }
    };

    private sealed class ModMessages : IMyUtilities
    {
        internal readonly List<Action<object>> Handlers = new();
        internal IReadOnlyDictionary<string, Delegate> Response;
        internal int Requests;
        public void RegisterMessageHandler(long channel, Action<object> handler)
        {
            if (channel != Channel) throw new InvalidOperationException("Wrong API channel");
            Handlers.Add(handler);
        }
        public void UnregisterMessageHandler(long channel, Action<object> handler) => Handlers.Remove(handler);
        public void SendModMessage(long channel, object payload)
        {
            foreach (var handler in Handlers) handler(payload);
            if (payload as string != "ApiEndpointRequest") return;
            Requests++;
            if (Response != null) SendModMessage(channel, Response);
        }
    }
}
