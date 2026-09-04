# Unified Storage for Space Engineers

Unified Storage is a Pulsar client plugin that replaces the grid side of Space Engineers' terminal inventory page with a projected, ship-wide inventory. Items remain in their real inventories; the plugin groups stack-compatible items for display and resolves every interaction into ordinary client inventory requests accepted by an unmodified server.

The current client implementation includes:

- one view per mechanical grid group, plus optional conveyor-component and terminal block-group views;
- configurable inventory groups with editable cargo, weapon, power, refinery, assembler, gas, tool, safety, connector and safe unknown-definition presets;
- dynamic named terminal-group selectors, block types/definitions, recipe outputs, inventory roles and material filters;
- definition-derived support for vanilla and modded ammunition, reactor fuel, refinery recipes, production inventories, and other live constraints;
- mouse and gamepad transfers, amount dialogs, search, filters, cross-grid-group transfers, and an in-page vanilla-UI fallback toggle;
- Existing Stack First, Fill First, and Even By Item placement/rebalance policies;
- UI-managed Manual, Reserved, and No Unified Cargo Destination exclusions;
- automatic/manual refinery ore priority and bounded physical input sorting;
- component production targets with add-only assembler queueing and opt-in local maintenance;
- generic loadouts with target/supply/return groups, overlap conflict protection, explicit empty-bottle refill jobs, and idle-assembler draining;
- bounded, acknowledgement-driven execution with access, capacity, constraint, and vanilla-equivalent conveyor reachability checks before every transfer.

The plugin works fully client-only and does not require a programmable block, mod, script, or server plugin. Local automation runs only while that client is connected. An optional Magnetar companion now implements the first server milestone: secure discovery and revisioned, world-local shared settings. Authoritative transfers and server-owned automation remain planned; see [SERVER_COMPANION_PLAN.md](Docs/SERVER_COMPANION_PLAN.md).

The client entry point is **Inventory groups → Shared profile**. Fetch and inspect a server snapshot, explicitly publish local settings, or adopt a fetched revision. Only the profile owner can publish; faction members may read when sharing and operator policy allow it. Adoption keeps unmatched private groups, writes a separate local backup, and leaves maintenance switches off. No companion means this feature is unavailable; normal inventory operations remain unchanged.

## Build and test

Install Space Engineers, Pulsar, Python 3.12+, and the .NET 10 SDK. On Windows, also install the .NET Framework 4.8.1 developer pack. Run `setup.py` if the automatic Steam/Pulsar path discovery does not match your installation.

```sh
dotnet build ClientPlugin/ClientPlugin.csproj -c Release -p:RunPostBuildEvent=Never
dotnet run --project Tests/UnifiedStorage.CoreTests.csproj -c Release
```

Omit `RunPostBuildEvent=Never` when you want the template build to deploy the plugin into Pulsar's local plugin folder. The implementation plan and in-game verification matrix are in [CLIENT_PLUGIN_PLAN.md](Docs/CLIENT_PLUGIN_PLAN.md) and [CLIENT_PLUGIN_TEST_MATRIX.md](Docs/CLIENT_PLUGIN_TEST_MATRIX.md).

To build the optional companion, install the Space Engineers Dedicated Server and Magnetar, then run:

```sh
dotnet build ServerPlugin/ServerPlugin.csproj -c Release -p:RunPostBuildEvent=Never
```

The companion's operator settings use Magnetar PluginSdk (`UnifiedStorage.companion.cfg`); ship settings live separately in the world's `Storage/UnifiedStorage.server-profiles.xml`. Builds have been checked, but this first milestone still needs a live dedicated-server multiplayer acceptance pass before production deployment. See [SERVER_COMPANION_IMPLEMENTATION.md](Docs/SERVER_COMPANION_IMPLEMENTATION.md) for implemented limits and the remaining work.

## Safety boundary

The projected UI never creates a synthetic game inventory and never mutates replicated state directly. Every transfer uses `MyInventory.TransferByUser` only after checking the concrete source and destination, current access, group membership, live constraints, capacity, sorter-aware reachable endpoints, and the plain conveyor reachability result. Conveyor automation send/receive flags do not restrict manual transfers. Production is add-only; the plugin never clears or reorders a player's assembler queues.

## Reporting bugs

Please open a [GitHub issue](https://github.com/OwendB1/se-unified-storage/issues) with the game version, Pulsar runtime, server type, relevant mods, reproduction steps, and the Unified Storage log excerpt. For inventory-path bugs, include the sorter direction/filter and whether the route needs a large conveyor tube.
