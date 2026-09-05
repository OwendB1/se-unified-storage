using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Utils;

namespace ClientPlugin.Inventory;

internal static class WeaponCoreCompatibility
{
    // WeaponCore's mod API; no dependency on its assembly or serialized weapon schema.
    private const long Channel = 67549756549;
    private const int RefreshInterval = 300;
    private static MySession session;
    private static IMyUtilities utilities;
    private static Action<ICollection<MyDefinitionId>> getWeapons;
    private static Action<IDictionary<MyDefinitionId, List<MyTuple<int, MyTuple<MyDefinitionId, string, string, bool>>>>> getMagazines;
    private static Dictionary<MyDefinitionId, HashSet<MyDefinitionId>> ammunition = new();
    private static int refreshTicks;
    private static bool warned;

    internal static long Revision { get; private set; }
    internal static bool IsWeapon(MyDefinitionId definition) => ammunition.ContainsKey(definition);
    internal static bool Accepts(MyDefinitionId definition, MyDefinitionId item) =>
        ammunition.TryGetValue(definition, out var magazines) && magazines.Contains(item);

    internal static void Update()
    {
        var current = MySession.Static;
        if (!ReferenceEquals(session, current) || !ReferenceEquals(utilities, MyAPIGateway.Utilities))
            Reset();
        if (current == null || !current.Ready || MyAPIGateway.Utilities == null)
            return;

        if (utilities == null)
        {
            session = current;
            utilities = MyAPIGateway.Utilities;
            utilities.RegisterMessageHandler(Channel, HandleMessage);
            MySession.OnUnloading += Reset;
        }
        if (--refreshTicks > 0) return;
        refreshTicks = RefreshInterval;

        var next = new Dictionary<MyDefinitionId, HashSet<MyDefinitionId>>();
        try
        {
            if (getWeapons == null || getMagazines == null)
                utilities.SendModMessage(Channel, "ApiEndpointRequest");
            refreshTicks = RefreshInterval;
            if (getWeapons != null)
            {
                var definitions = new HashSet<MyDefinitionId>();
                getWeapons(definitions);
                foreach (var definition in definitions)
                    next[definition] = new HashSet<MyDefinitionId>();

                // A known weapon with no usable magazine mapping accepts nothing, including vanilla ammo.
                var mappings = new Dictionary<MyDefinitionId, List<MyTuple<int, MyTuple<MyDefinitionId, string, string, bool>>>>();
                getMagazines?.Invoke(mappings);
                foreach (var mapping in mappings)
                {
                    if (!next.TryGetValue(mapping.Key, out var magazines) || mapping.Value == null) continue;
                    foreach (var entry in mapping.Value)
                    {
                        var magazine = entry.Item2;
                        // Item4 is SkipAimChecks, NOT an energy flag. Empty/Energy names are virtual ammo.
                        if (string.IsNullOrEmpty(magazine.Item2) || magazine.Item2 == "Energy" ||
                            magazine.Item1.SubtypeName == "Energy") continue;
                        if (MyDefinitionManager.Static.TryGetPhysicalItemDefinition(magazine.Item1, out var item) &&
                            item is MyAmmoMagazineDefinition)
                            magazines.Add(magazine.Item1);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            // Keep known ownership, but never use stale/partially fetched ammo after an API failure.
            foreach (var definition in ammunition.Keys)
                next[definition] = new HashSet<MyDefinitionId>();
            foreach (var magazines in next.Values) magazines.Clear();
            Warn(exception.Message);
        }
        SetAmmunition(next);
    }

    private static void HandleMessage(object payload)
    {
        if (payload is not IReadOnlyDictionary<string, Delegate> api) return;
        api.TryGetValue("GetCoreWeapons", out var weapons);
        api.TryGetValue("GetAllWeaponMagazines", out var magazines);
        getWeapons = weapons as Action<ICollection<MyDefinitionId>>;
        getMagazines = magazines as Action<IDictionary<MyDefinitionId, List<MyTuple<int, MyTuple<MyDefinitionId, string, string, bool>>>>>;
        refreshTicks = 0;
        if (api.Count != 0 && (getWeapons == null || getMagazines == null))
            Warn("Required definition/magazine endpoints are missing or incompatible.");
    }

    private static void SetAmmunition(Dictionary<MyDefinitionId, HashSet<MyDefinitionId>> next)
    {
        if (ammunition.Count == next.Count && ammunition.All(pair =>
                next.TryGetValue(pair.Key, out var magazines) && pair.Value.SetEquals(magazines))) return;
        ammunition = next;
        Revision++;
    }

    private static void Warn(string message)
    {
        if (warned) return;
        warned = true;
        MyLog.Default.WriteLine("UnifiedStorage: WeaponCore compatibility: " + message);
    }

    internal static void Reset()
    {
        MySession.OnUnloading -= Reset;
        utilities?.UnregisterMessageHandler(Channel, HandleMessage);
        utilities = null;
        session = null;
        getWeapons = null;
        getMagazines = null;
        refreshTicks = 0;
        warned = false;
        SetAmmunition(new Dictionary<MyDefinitionId, HashSet<MyDefinitionId>>());
    }
}
