# Unified Storage for Space Engineers

Unified Storage is a Pulsar client plugin that replaces the grid side of Space Engineers' terminal inventory page with a projected, ship-wide inventory. Items remain in their real inventories; the plugin groups stack-compatible items for display and resolves every interaction into ordinary client inventory requests accepted by an unmodified server.

The current client implementation includes:

- one view per mechanical grid group, plus optional conveyor-component and terminal block-group views;
- configurable inventory groups with editable cargo, weapon, power, refinery, assembler, gas, tool, safety, connector and safe unknown-definition presets; each group's editor lists its rules with buffered Apply/Cancel and Ctrl/Shift multi-selection;
- dynamic named terminal-group selectors, block types/definitions, recipe outputs, inventory roles and material filters;
- definition-derived support for vanilla and modded ammunition, reactor fuel, refinery recipes, production inventories, and other live constraints;
- optional WeaponCore API discovery of weapon definitions and their physical magazines, including sorter-based weapons and per-definition loadouts;
- stable, locally remembered display order with same-section drag reordering (refinery inputs retain ore-priority order);
- mouse and gamepad transfers, amount dialogs, search, filters, cross-grid-group transfers, and an in-page vanilla-UI fallback toggle;
- Existing Stack First, Fill First, and Even By Item placement/rebalance policies;
- UI-managed Manual, Reserved, and No Unified Cargo Destination exclusions;
- automatic/manual refinery ore priority and bounded physical input sorting;
- component production targets with add-only assembler queueing and opt-in local maintenance;
- generic loadouts with target/supply/return groups, overlap conflict protection, idle-assembler draining;
- bounded, acknowledgement-driven execution with access, capacity, constraint, and vanilla-equivalent conveyor reachability checks before every transfer.

The plugin works fully client-only and does not require a programmable block, mod, script, or server plugin. Local automation runs only while that client is connected. The optional Magnetar companion adds revisioned shared settings, batched transfers/rebalance, server-owned refinery/production/loadout services, and explicit idle-assembler-drain jobs. All server mutation capabilities default off pending live acceptance. Unattended services additionally require profile-owner opt-in. See [SERVER_COMPANION_PLAN.md](Docs/SERVER_COMPANION_PLAN.md).

WeaponCore compatibility activates automatically when its mod API is available in the world, on both the client and companion. Weapon groups and loadout item choices use WeaponCore's magazine mappings together with live inventory constraints. See [WEAPONCORE_COMPATIBILITY.md](Docs/WEAPONCORE_COMPATIBILITY.md) for behavior and in-game checks.

The client entry point is **Inventory groups → Shared profile**. Fetch, inspect, publish or adopt a revision; **Server automation** manages ownership and run-now/status requests; **Profile tools** supports section patches, binding recovery and archived deletion. Only the profile owner can publish; faction members may read when sharing and operator policy allow it. Adoption keeps unmatched private groups, writes a separate local backup, and leaves maintenance switches off. Paged profiles support up to 256 KiB. Multi-rule groups use group schema 2; update the companion for shared settings and accelerated actions. Without its `GroupRules` capability, the client keeps ownership coordination but uses standalone transfers and disables profile exchange. No companion means normal inventory operations remain available, with a discovery grace period before remembered client maintainers start.

Custom bottle refill is retired on SE 1.210+. Use native generator bottle pulling/auto-refill or a supplied Medical Room, Survival Kit or Refill Station. These are not identical to the removed job: native pulling does not promise to return bottles to their original cargo. The plugin leaves native settings untouched. See [Keen's 1.210 release notes](https://support.keenswh.com/spaceengineers/pc/announcement/update-1-210-prosperity).

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

The companion's operator settings use Magnetar PluginSdk (`UnifiedStorage.companion.cfg`); ship settings live separately in the world's `Storage/UnifiedStorage.server-profiles.xml`. Builds and a live single-client owner-path smoke test have passed. The full dedicated-server multiplayer acceptance matrix remains required before production deployment. See [SERVER_COMPANION_IMPLEMENTATION.md](Docs/SERVER_COMPANION_IMPLEMENTATION.md) for evidence, implemented limits and the remaining work.

## Safety boundary

When an updated companion advertises a capability, the corresponding UI action sends a bounded intent. The server resolves selectors and stack compatibility itself and rechecks endpoint rights and conveyor routes. Timeouts never replay submitted intents through vanilla. Server ownership suppresses matching client maintainers; stale ownership fails closed. This coordinates updated Unified Storage clients, not arbitrary scripts or older clients. Client source builds require `ClientPlugin`, `Shared`, and `Runtime`; server builds require `ServerPlugin`, `Shared`, and `Runtime`.

The projected UI never creates a synthetic game inventory and never mutates replicated state directly. In standalone mode, every transfer uses `MyInventory.TransferByUser` only after checking the concrete source and destination, current access, group membership, live constraints, capacity, sorter-aware reachable endpoints, and the plain conveyor reachability result. Conveyor automation send/receive flags do not restrict manual transfers. Production is add-only; the plugin never clears or reorders a player's assembler queues.

## Reporting bugs

Please open a [GitHub issue](https://github.com/OwendB1/se-unified-storage/issues) with the game version, Pulsar runtime, server type, relevant mods, reproduction steps, and the Unified Storage log excerpt. For inventory-path bugs, include the sorter direction/filter and whether the route needs a large conveyor tube.
