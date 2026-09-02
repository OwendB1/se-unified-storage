# Optional Server Companion Plan

## Status and scope

This is a separate, optional future project. It is not required by the client-only implementation in [CLIENT_PLUGIN_PLAN.md](CLIENT_PLUGIN_PLAN.md), and the client must remain functional against an unmodified Space Engineers server.

The companion consists of a Magnetar server plugin plus the smallest corresponding integration in the Pulsar client plugin. It does not replace the unified client UI or create a real virtual inventory. Its purpose is to augment the complete client-only feature set with authoritative batching, shared settings, cross-client coordination, unattended automation, validation, and observability.

This is not a server-hosted port of ISY's programmable-block script. It has no programmable block, LCD protocol, block-name tags, or duplicated PB scheduler. The client remains the UI; the server companion owns only the capabilities that genuinely benefit from server authority or persistence.

## Goals

- Accept one high-level transfer intent instead of a separate network request for every physical stack allocation.
- Rebuild the transfer plan from current authoritative server inventory state.
- Validate player access, ship scope, conveyor routes and direction, sorter rules, tube sizes, inventory constraints, and capacity.
- Execute the resulting physical inventory transfers on the server game thread.
- Return clear requested, moved, and rejected amounts with a failure reason.
- Add server-side rate limits, configuration, logging, and telemetry.
- Use the same placement-policy rules as the client implementation.
- Persist shared refinery priorities, component targets, and blueprint choices per ship-scope profile.
- Coordinate refinery sorting and target production so multiple clients cannot enqueue the same deficit.
- Persist shared management exclusions and Phase 2 machine-loadout rules.
- Coordinate optional server-owned loadout maintenance without creating separate uranium, ice, or ammunition automation engines.
- Execute explicit bottle-refill and idle-assembler-drain jobs from authoritative state and return structured partial results.
- Optionally continue configured automation while no participating client has the terminal open or is connected.

## Non-goals

- The server does not own a synthetic combined inventory.
- The companion does not bypass vanilla conveyor, ownership, or inventory rules.
- A multi-inventory transfer is not promised to be atomic; the server reports partial completion rather than attempting rollback.
- The client-only path must not depend on the companion being installed or reachable.
- Server settings do not erase or replace a player's client-local profiles unless that player explicitly adopts the server profile.
- The companion does not automatically disassemble excess stock or clear, reorder, or change the mode of queues it did not create.
- The companion does not continuously fill bottles, automatically drain idle assemblers, steal stock between loadouts, or toggle a block's conveyor, power, stockpile, or enabled state.
- The companion does not restore rejected ISY features such as physical type-container sorting, automatic container naming, LCD output, or survival-kit stone crafting.

## Capability handshake

At connection or world readiness, the client detects whether the server exposes a compatible protocol version and which independent capabilities are enabled: batched transfers, shared settings, refinery automation, component-target automation, loadout automation, and explicit utility jobs.

- If no compatible companion is present, the client continues using ordinary `MyInventory.TransferByUser` requests exactly as described in the client plan.
- If it is present, the client may submit a single transfer intent and wait for its result before refreshing the unified view.
- A capability loss or timeout falls back to the client-only path for later operations; the same in-flight operation must not be submitted twice.
- A server may expose settings persistence without enabling unattended automation, or authoritative automation without enabling batched cargo transfers. The client selects each path independently instead of treating the companion as one all-or-nothing flag.
- Local profiles remain available until the player explicitly publishes one to, or adopts one from, the server. Once server automation owns a scope, the client stops its local maintain/sort loop for that scope and becomes a controller and status viewer.

## Persistent scope settings

Persist player intent, not derived game state. A versioned profile should contain:

```text
ScopeProfileV1
    profile ID and revision
    anchor grid entity ID
    owner identity ID and sharing mode
    refinery mode: AutomaticScarcity or Manual
    auto-sort enabled
    pinned and manual ore definition-ID lists
    excluded refinery entity IDs
    component targets by definition ID
    maintain-targets enabled and start threshold
    component-to-blueprint overrides
    block/inventory management overrides
    Phase 2 machine-loadout rules
```

Each management override identifies a block entity and, where relevant, inventory index plus the exact Manual, Reserved, or NotAUnifiedCargoDestination flags. Reserved inventories are excluded from target counts and every virtual, automatic, or bulk source and destination selection but remain visible to clients. Each loadout rule stores its target selector and inventory role, full item definition ID, per-member or section-total mode, target amount, section-total distribution policy, maintain toggle, and explicit inclusion of non-working blocks. Exact-block selectors use entity IDs; definition selectors use full block definition IDs.

Do not persist inventory manifests, computed scarcity scores, the automatically discovered mod ore list, current stock, queue contents, capacities, conveyor reachability, or transient bottle/drain jobs. Rebuild those from authoritative live state. Store definition IDs as their full type/subtype pair; if a mod is temporarily absent, retain the unknown entry as inactive rather than deleting it. Ignore stale block-entity overrides after destruction and retain them only for the same orphan-retention window as their profile; never reattach them by display name.

The dynamic per-scope profile store is separate from Magnetar PluginSdk's operator configuration. PluginSdk XML is appropriate for global operator switches, limits, and logging and already provides sparse atomic configuration writes. Ship profiles are player-facing runtime data with their own schema, revisions, permissions, and lookup keys, so they belong in a dedicated versioned data file under the plugin's world storage. Write it atomically after a short debounce and flush it on world save and plugin unload.

### Profile identity across mechanical changes

A mechanical grid group has no permanent group entity ID, so do not key settings by a hash of its current members or by its display name. Give each profile its own UUID and bind it to an explicit anchor grid entity ID. The initial anchor may default to the largest grid in the group, but the binding remains explicit afterward.

- Adding or removing rotors, pistons, or hinges does not change the profile while its anchor remains in the group.
- On a split, the side containing the anchor keeps the profile; another side receives local/default settings until a permitted user binds or creates a profile for it.
- On a merge where only one anchored profile is present, that profile applies to the combined group.
- On a merge containing multiple anchored profiles, preserve every profile but pause unattended automation and ask an authorized client which profile should control the combined scope. Never merge targets or priority lists silently.
- If an anchor grid is destroyed, keep the profile as orphaned recoverable data for an operator-configured retention period rather than rebinding it by name.

This gives predictable raid, split, docking, and rebuild behaviour without writing metadata into block names or custom data.

### Settings protocol and permissions

Use revisioned messages rather than replacing a whole profile blindly:

```text
GetScopeProfile(scope identity)
ScopeProfileSnapshot(profile ID, revision, values, permissions)
PatchScopeProfile(profile ID, base revision, changed fields)
ScopeProfileChanged(profile ID, new revision, changed fields)
RunAutomationNow(profile ID, refinery/component/loadout/all)
AutomationStatus(profile ID, state, last result)
StartUtilityJob(request ID, profile ID, refill-bottles/drain-idle-assemblers, selection)
UtilityJobProgress(request ID, state, counts)
UtilityJobResult(request ID, completed/partial/rejected, details)
```

The server resolves the authenticated player and current mechanical scope itself. Reading or changing settings requires normal terminal access plus the configured ownership/faction policy. A stale `base revision` is rejected with the current snapshot so concurrent editors cannot silently overwrite each other. Broadcast accepted changes only to clients currently authorized to see that scope.

## Transfer intent

A request should describe user intent, not trust a client-generated physical allocation. It minimally contains:

```text
request ID
operation: withdraw or deposit
ship scope identity
concrete source or destination inventory
item content/stack identity
requested amount
deposit policy, when applicable
```

The server derives the requesting player from the authenticated network context. It must not accept a claimed player identity, trusted candidate list, capacity, or access result from the client.

Example: the client asks to withdraw `1,000 Steel Plate` from ship scope `X` into the player's character inventory. The server enumerates the accessible physical stacks in scope `X`, selects live reachable sources, executes as much as vanilla rules permit, and responds with something like `requested: 1,000; moved: 650; reason: destination full`.

For a deposit, the concrete source is known but the destination is the ship scope. The server filters eligible inventories and applies `ExistingStackFirst`, `FillFirst`, or `EvenByItem` to choose physical destinations.

## Authoritative execution

```text
client intent
    -> authenticate and rate-limit
    -> rebuild cargo snapshot
    -> validate scope and endpoints
    -> plan physical allocations
    -> recheck and execute allocations
    -> return result
```

The server rechecks each allocation immediately before mutation because production blocks, sorters, players, block destruction, or other plugins may alter inventory state during execution. Invalid allocations are skipped or replanned within a bounded amount of work. The result always reflects the amount actually moved.

One intent may still require several physical game mutations. Batching reduces network chatter and centralizes validation; it does not remove the underlying conveyor graph or make the operation transactional.

## Authoritative refinery and production automation

The companion may run in either of two modes per capability:

- **Persistence only:** the server stores and synchronizes the shared profile, while a connected client performs sorting or target maintenance through ordinary vanilla requests.
- **Server owned:** one server scheduler evaluates the profile and performs bounded work on the game thread. Clients show its status and send settings or **Run now** commands but do not run a competing local loop.

Server-owned refinery sorting uses the same loaded blueprint prerequisites/results and the same pinned, manual, and automatic-scarcity rules as the client plan. It filters the desired order per live refinery, verifies the profile principal still has the configured ownership/faction relationship to the anchor and machine, then reorders only that refinery's input. It does not redistribute ore unless a separate explicit Rebalance intent is submitted.

Server-owned component maintenance uses the same component-to-blueprint resolution and `stock + queued` deficit accounting as the client. It appends whole blueprint runs only to eligible assembly-mode assemblers. It does not clear queues, switch modes, enable conveyors, or disassemble excess items. If the profile owner loses access, the anchor changes ownership, multiple profiles collide after a merge, or the chosen blueprint becomes unavailable, pause that profile and expose the reason to clients.

Server-owned Phase 2 loadout maintenance uses the same per-member or section-total target accounting and destination policies as the client. It respects all management overrides, sources deficits only from Unified Cargo, and returns excess only to Unified Cargo. It never drains one managed loadout to satisfy another. Missing definitions, insufficient stock, disconnected inventories, or full cargo produce a visible partial status rather than repeated aggressive retries.

Do not add script-assisted refinery filling merely because the companion can move ore authoritatively. It remains behind the client plan's testing gate. If later justified, give it a separate capability and explicit per-profile opt-in; it must not be hidden inside refinery sorting or loadout maintenance.

Inventory, production-queue, block-group, ownership, and mechanical-group changes mark a profile dirty. Coalesce those events and evaluate dirty profiles on a bounded scheduler; do not recreate a programmable-block-style full-grid polling cycle. Keep per-tick limits for profiles evaluated, physical inventory swaps, loadout transfers, and assembler queue additions. A save or shutdown flushes settings but does not need to persist live work queues because the game already persists refinery inventories and assembler queues.

This mode is the actual solution to duplicate multi-client target batches and offline maintenance. The client-only implementation remains useful without it, but cannot elect a single durable automation owner on an unmodified server.

## Explicit utility jobs

Bottle refill and assembler drain are request-driven jobs, not maintainers. The companion is useful because it can validate and execute the complete multi-step operation from authoritative state, batch status messages, and finish after the initiating client closes the terminal or disconnects. Jobs are bounded and live only for the current server process; after a server restart, report them interrupted rather than reconstructing intent from inventory state.

### Refill bottles

The server independently resolves the requested scope, selected bottles, compatible non-Manual working fillers, ownership, constraints, and conveyor paths. Bottles stored on Manual blocks or in Reserved inventories are skipped as well. Process one stateful bottle at a time so transfers or stacking cannot make job identity ambiguous. Stage it into a filler, observe authoritative fill progress, and return it to its original inventory or an eligible Unified Cargo destination. A no-progress timeout returns the bottle when possible and records a per-bottle failure.

### Drain idle assemblers

Immediately before touching each assembler, recheck that its queue is empty, it is not producing, the profile principal retains access, and the Manual override is absent. Drain only non-Reserved input and output inventories through the normal authoritative deposit planner. If a queue appears during the job, skip that assembler without modifying its mode or queue.

Both jobs use authenticated, idempotent request IDs, per-player and per-scope rate limits, maximum item/allocation counts, cancellation, progress, and structured partial results. Cancellation stops future steps but does not roll back transfers already accepted by the game.

## Shared planning logic

The client and server should produce equivalent deposit allocations, refinery priorities, blueprint choices, component deficits, loadout deficits, exclusion filtering, and assembler assignments from equivalent snapshots. Keep these calculations pure and independent from either runtime. Share a small source project if the client and server target sets permit it; otherwise use versioned golden test vectors. Do not make the client depend on a server assembly or require the companion to load client UI types.

The executor remains platform-specific:

- The client executor issues vanilla user transfer requests.
- The server executor mutates inventories through the appropriate authoritative game APIs.

## Server configuration and telemetry

Initial configuration should be limited to controls the server operator actually needs:

- Enable or disable companion transfers.
- Enable or disable shared scope profiles, server-owned refinery sorting, server-owned component maintenance, server-owned loadout maintenance, bottle-refill jobs, and assembler-drain jobs independently.
- Maximum intents per player over a time window.
- Maximum physical allocations per intent.
- Maximum dirty profiles, refinery swaps, loadout transfers, assembler additions, utility jobs, and job allocations processed per update window.
- Enabled deposit policies.
- Who may create, edit, share, bind, or delete owner/faction profiles and how long orphaned profiles are retained.
- Logging or telemetry level.

Expose these global operator controls through Magnetar PluginSdk configuration. Keep dynamic ship profiles out of that static configuration schema.

Useful runtime statistics include accepted and rejected intents, profile loads and revision conflicts, active/paused automation profiles, refinery swaps, loadout transfers, assembler runs added, bottle and drain job outcomes, partial transfers, validation failures by reason, execution duration, dirty-profile backlog, and queue depth. Avoid recording item manifests, stock targets, or player cargo contents unless explicitly enabled for diagnostics.

## Implementation order

1. Finish and validate the complete client-only transfer, exclusions, refinery-priority, and component-target paths.
2. Define a versioned capability handshake with independent transfer, settings, refinery-automation, target-automation, loadout-automation, and utility-job flags.
3. Add the versioned scope-profile store, explicit anchor binding, management overrides, permission checks, revisioned settings messages, and local-profile adoption flow.
4. Add authoritative withdrawal with access, scope, and inventory validation.
5. Add authoritative deposits using the three client placement policies.
6. Add persistence-only refinery and component settings so multiple clients see one profile before enabling server mutation.
7. Add server-owned refinery sorting with dirty-event debouncing, work limits, ownership-change pauses, and mixed refinery definitions.
8. Add server-owned component maintenance with definition-derived blueprint resolution, queue accounting, integral rounding, and add-only semantics.
9. Add persistence-only Phase 2 loadout rules, then server-owned loadout maintenance using the existing target and transfer planners.
10. Add bounded explicit bottle-refill jobs, then idle-assembler-drain jobs.
11. Add bounded queues, rate limits, timeouts, cancellation, duplicate-request protection, merge conflicts, and orphan retention.
12. Add PluginSdk operator configuration, structured results, logging, and runtime statistics.
13. Test companion absence, partial capability sets, version mismatch, disconnects, retries, stale stacks, destroyed blocks, sorter changes, full destinations, simultaneous editors, two automation clients, ownership/faction changes, server restart, profile split/merge, missing mods, utility-job interruption, and world unload.
