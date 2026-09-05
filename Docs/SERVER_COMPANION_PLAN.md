# Optional Server Companion Plan

## Status and scope

This is a separate, optional companion. Current source implements shared profiles with paging/section patches/lifecycle tools, authoritative transfers and rebalance, coordinated refinery/production/loadout services, and bounded utility jobs. Mutation capabilities are experimental and default off. Source/build checks are not a substitute for the dedicated-server acceptance matrix, which remains open. See [SERVER_COMPANION_IMPLEMENTATION.md](SERVER_COMPANION_IMPLEMENTATION.md) for current bounds and known limitations. The companion is not required by [CLIENT_PLUGIN_PLAN.md](CLIENT_PLUGIN_PLAN.md).

The companion consists of a Magnetar server plugin plus the smallest corresponding integration in the Pulsar client plugin. It does not replace the unified client UI or create a real virtual inventory. Its purpose is to augment the complete client-only feature set with authoritative batching, shared settings, cross-client coordination, unattended automation, validation, and observability.

This is not a server-hosted port of ISY's programmable-block script. It has no programmable block, LCD protocol, block-name tags, or duplicated PB scheduler. The client remains the UI; the server companion owns only the capabilities that genuinely benefit from server authority or persistence.

## Goals

- Accept one high-level transfer intent instead of a separate network request for every physical stack allocation.
- Rebuild the transfer plan from current authoritative server inventory state.
- Validate player access, ship scope, conveyor routes and direction, sorter rules, tube sizes, inventory constraints, and capacity at least as strictly as the vanilla terminal would. This is an operator security benefit because the vanilla `TransferByUser` server handler does not itself enforce conveyor reachability.
- Execute the resulting physical inventory transfers on the server game thread.
- Return clear requested, moved, and rejected amounts with a failure reason.
- Add server-side rate limits, configuration, logging, and telemetry.
- Use the same placement-policy rules as the client implementation.
- Persist shared refinery priorities, component targets, and blueprint choices per ship-scope profile.
- Coordinate refinery sorting and target production so multiple clients cannot enqueue the same deficit.
- Persist shared management exclusions and Phase 2 machine-loadout rules.
- Coordinate optional server-owned loadout maintenance without creating separate uranium, ice, or ammunition automation engines.
- Execute explicit idle-assembler-drain jobs from authoritative state and return structured partial results.
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

## Capability handshake and transport

Use the vanilla secure mod-message channel exposed by `MyAPIGateway.Multiplayer` / `MyModAPIHelper.MyMultiplayer`. Register one fixed channel ID through `RegisterSecureMessageHandler`; every payload starts with a plugin magic value, message kind, and protocol version. Use reliable delivery for handshake, intents, settings, and final results. Optional progress updates may be unreliable. Enforce a small payload ceiling and page any profile snapshot too large to fit comfortably in one message.

Do not define plugin-specific `[Event]` network methods. Mod messages ride vanilla events already present on both peers, so an unmodified server simply drops a client handshake sent to an unregistered channel. Plugin events would require compatible event tables on both sides and break the required client-only fallback. The secure handler's `senderSteamId` is transport-authenticated and is the only player identity accepted for a request.

At connection or world readiness, the client detects whether the server exposes a compatible protocol version and which independent capabilities are enabled: batched transfers, shared settings, refinery automation, component-target automation, loadout automation, and explicit utility jobs.

The client sends discovery when its world is ready, and the server also advertises to a joining player so a client whose plugin becomes ready late can still discover it.

- If no compatible companion is present, the client continues using ordinary `MyInventory.TransferByUser` requests exactly as described in the client plan.
- If it is present, the client may submit a single transfer intent and wait for its result before refreshing the unified view.
- A capability loss or handshake timeout falls back to the client-only path for later operations. An intent timeout has different semantics: its request ID is never replayed through either the companion or vanilla path. The client refreshes replicated state and reports **unknown outcome**; the server caches completed results for a bounded retention window and returns the cached result for a duplicate request ID instead of executing it again.
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
    ordered configurable inventory groups with stable IDs
    generic loadout rules referencing target, supply and return group IDs
```

Each management override identifies a block entity and, where relevant, inventory index plus the exact Manual, Reserved, or NotAUnifiedCargoDestination flags. Reserved inventories are excluded from target counts and every virtual, automatic, or bulk source and destination selection but remain visible to clients. Share the client group's intent schema: stable group ID, name, list order, block selector, role and material/item filters. Selectors support family, block type, exact definition, specific block, named terminal group, recipe output, and all blocks. Loadouts reference target/supply/return group IDs, inventory role, full item definition ID, per-inventory or group-total target, policy, maintenance toggle and non-working inclusion. None explicitly disables supply or excess returns.

Persist a named terminal group's **name within the anchored ship profile**, never its resolved block IDs. Resolve live server membership for every operation; filter terminal systems to that mechanical ship so docked grids cannot leak into same-name groups. New members participate automatically. Missing or renamed groups pause affected rules with **Group not found**, never broaden to the whole ship. Only specific-block selectors persist entity IDs. Definition/type selectors retain their full identifiers. Unknown definitions and missing groups remain repairable records.

Existing client preset IDs and custom IDs survive sharing, rename and reordering. Do not overwrite private groups when adopting or publishing: apply the existing explicit adoption/revision flow. Restore-defaults and delete-group mutations must be revision-checked, and deleting a group retains paused referencing rules. The first companion implementation shares this intent schema with the standalone client; granular remote editing and server evaluation remain later steps.

Do not persist inventory manifests, computed scarcity scores, the automatically discovered mod ore list, current stock, queue contents, capacities, conveyor reachability, or transient bottle/drain jobs. Rebuild those from authoritative live state. Store definition IDs as their full type/subtype pair; if a mod is temporarily absent, retain the unknown entry as inactive rather than deleting it. Ignore stale block-entity overrides after destruction and retain them only for the same orphan-retention window as their profile; never reattach them by display name.

The dynamic per-scope profile store is separate from Magnetar PluginSdk's operator configuration. PluginSdk XML is appropriate for global operator switches, limits, and logging and already provides sparse atomic configuration writes. Ship profiles are player-facing runtime data with their own schema, revisions, permissions, and lookup keys, so they belong in a dedicated versioned data file under the current world's storage. Write atomically after a short debounce. Never serialize live game objects off-thread: capture an immutable profile snapshot on the game thread during `MySession.Static.OnSavingCheckpoint`, then write that snapshot synchronously or hand only the snapshot to a writer. Flush the last snapshot from `MySession.OnUnloading`; `MySession.OnLoading` runs before entities exist, so load data there but bind anchors lazily as their grids appear.

### Profile identity across mechanical changes

A mechanical grid group has no permanent group entity ID, so do not key settings by a hash of its current members or by its display name. Give each profile its own UUID and bind it to an explicit anchor grid entity ID. The initial anchor may default to the largest grid in the group, but the binding remains explicit afterward.

- Adding or removing rotors, pistons, or hinges does not change the profile while its anchor remains in the group.
- On a split, the side containing the anchor keeps the profile; another side receives local/default settings until a permitted user binds or creates a profile for it.
- On a merge where only one anchored profile is present, that profile applies to the combined group.
- On a merge containing multiple anchored profiles, preserve every profile but pause unattended automation and ask an authorized client which profile should control the combined scope. Never merge targets or priority lists silently.
- If an anchor grid is destroyed, keep the profile as orphaned recoverable data for an operator-configured retention period rather than rebinding it by name.
- A blueprint paste or projector rebuild creates new entity IDs and therefore starts without the original profile by design; the old profile remains orphaned under the same retention rule.

This gives predictable raid, split, docking, and rebuild behaviour without writing metadata into block names or custom data.

Subscribe to grid split, merge, and ownership-change notifications and coalesce bursts before reevaluating `MyCubeGridGroups.Static.Mechanical.GetGroup(anchor)` on the game thread. The profile follows its explicit anchor rather than a hash of group membership. Pause ownership-based automation when the principal disappears from the anchor grid's `BigOwners`; event-driven reevaluation is preferred to periodic ownership polling.

### Settings protocol and permissions

Use revisioned messages rather than replacing a whole profile blindly:

```text
GetScopeProfile(scope identity)
ScopeProfileSnapshot(profile ID, revision, values, permissions)
PatchScopeProfile(profile ID, base revision, changed fields)
ScopeProfileChanged(profile ID, new revision, changed fields)
RunAutomationNow(profile ID, refinery/component/loadout/all)
AutomationStatus(profile ID, state, last result)
StartUtilityJob(request ID, profile ID, drain-idle-assemblers, selection)
UtilityJobProgress(request ID, state, counts)
UtilityJobResult(request ID, completed/partial/rejected, details)
```

The server resolves the authenticated player from the secure message sender and resolves the current mechanical scope itself. Reading or changing settings requires normal terminal access plus the configured ownership/faction policy. A stale `base revision` is rejected with the current snapshot so concurrent editors cannot silently overwrite each other. Send accepted changes only to Steam IDs currently authorized to see that scope.

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

Use the same authenticated envelope and request-ID rules for explicit Rebalance, **Run now**, and utility-job intents. Validate a deposit policy against both the protocol-version enum and the operator's enabled-policy list; reject unknown or disabled values with a structured reason rather than silently choosing a default.

The server derives the requesting player from the authenticated network context. It must not accept a claimed player identity, trusted candidate list, capacity, or access result from the client.

Example: the client asks to withdraw `1,000 Steel Plate` from ship scope `X` into the player's character inventory. The server enumerates the accessible physical stacks in scope `X`, selects live reachable sources, executes as much as vanilla rules permit, and responds with something like `requested: 1,000; moved: 650; reason: destination full`.

For a deposit, the concrete source is known but the destination is the ship scope. The server filters eligible inventories and applies `ExistingStackFirst`, `FillFirst`, or `EvenByItem` to choose physical destinations.

## Authoritative validation

Local server mutations bypass the `[Server(ValidationType.Access | ValidationType.Ownership)]` guards that protect remotely invoked vanilla requests. The companion must reproduce those rules explicitly before any player-initiated withdraw, deposit, Rebalance, **Run now**, or utility job:

- Resolve the requester's identity from the authenticated Steam ID; never accept an identity from the payload.
- For both source and destination blocks, require `GetUserRelationToOwner(identityId)` to be `Owner`, `FactionShare`, or `NoOwnership`, or the vanilla remote-admin **use terminals** right.
- Require the requester's character to exist and be within `3 * MyConstants.DEFAULT_INTERACTIVE_DISTANCE` of any grid AABB in the endpoint block's logical group, or require that grid to be in the character's replication dependencies.
- Enforce the vanilla same-logical-group maximum separation of 2 km between the owning blocks.
- Do not inherit vanilla's destination-side admin bypass unless the operator enables an explicit companion setting for it.
- Validate conveyor direction and basic endpoint connectivity with `MyGridConveyorSystem.ComputeCanTransfer`, then use identity-aware `Reachable(..., playerId, itemId, predicate)` for player access, sorter filters, and `NeedsLargeTube`; never use `MySession.Static.LocalPlayerId` on a dedicated server.
- Recheck inventory identity, scope membership, constraints, capacity, and integral amounts immediately before mutation. Match vanilla manual-transfer semantics; conveyor automation send/receive flags alone must not prohibit reactor withdrawals or output-inventory transfers.

Server-owned unattended automation has no nearby requesting character, so proximity cannot apply. It runs under the persisted profile principal's ownership relation and pauses when ownership changes. Operator configuration chooses whether `FactionShare` is sufficient or the principal must remain in the anchor and target grids' `BigOwners`. Admin bypass remains opt-in here as well.

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

The server rechecks each allocation immediately before mutation because production blocks, sorters, players, block destruction, or other plugins may alter inventory state during execution. Server inventory changes are synchronous and authoritative, so this is a direct reread rather than a replication wait. Invalid allocations are skipped or replanned within a bounded amount of work. The result always reflects the amount actually moved.

After validation, execute with `MyInventory.Transfer(source, destination, sourceItemId, destinationIndex, amount, spawn: false)` on the game thread. It reaches the same internal transfer, partial-fit, and same-inventory swap behavior as the client request path without spawning overflow. Never use local invocation as a substitute for the checks above.

One intent may still require several physical game mutations. Batching reduces network chatter and centralizes validation; it does not remove the underlying conveyor graph or make the operation transactional.

## Authoritative refinery and production automation

The companion may run in either of two modes per capability:

- **Persistence only:** the server stores and synchronizes the shared profile, while a connected client performs sorting or target maintenance through ordinary vanilla requests.
- **Server owned:** one server scheduler evaluates the profile and performs bounded work on the game thread. Clients show its status and send settings or **Run now** commands but do not run a competing local loop.

Server-owned refinery sorting uses the same loaded blueprint prerequisites/results and the same pinned, manual, and automatic-scarcity rules as the client plan. It filters the desired order per live refinery, verifies the profile principal still has the configured ownership/faction relationship to the anchor and machine, then plans selection-sort-style pairwise swaps inside that refinery's input; same-inventory transfer is a swap or merge, never list insertion. All swaps for one refinery may run synchronously in one game tick, causing one queue rebuild on its next update, but the global per-tick swap budget still bounds large farms. Sorting does not redistribute ore unless a separate explicit Rebalance intent is submitted.

Server-owned component maintenance uses the same component-to-blueprint resolution and `stock + queued` deficit accounting as the client. It appends whole blueprint runs only to assemblers in assembly mode with **Use conveyor system** enabled, cooperative/slave mode disabled, queue capacity below `MySession.Static.MaxProductionQueueLength`, and `CanUseBlueprint` true. An add is acknowledged only by rereading the queue content and observing the blueprint amount delta; the success broadcast and queue item ID are not proof because a rejected insert still broadcasts and insertion may merge with the last entry. It does not clear queues, switch modes, enable conveyors, or disassemble excess items. If the profile owner loses the configured ownership relation, the anchor changes ownership, multiple profiles collide after a merge, or the chosen blueprint becomes unavailable, pause that profile and expose the reason to clients.

Server-owned generic loadout maintenance uses the same per-inventory or group-total accounting, selected supply/return groups and policies as the client. Exclude all same-item target inventories from supply and return candidates. Block all conflicting overlapping target rules visibly, rather than choosing an order that permits oscillation. Deduplicate physical inventories regardless of overlapping display groups. Revalidate membership and rule revision before each bounded operation, reserve stock within each batch, and clamp against current deficits/excesses. Missing definitions/groups, insufficient stock, disconnected inventories or full storage produce an inactive or partial status rather than aggressive retries. When server automation owns a profile, clients suspend their corresponding local loops; sharing group settings alone does not enable automation.

Do not add script-assisted refinery filling merely because the companion can move ore authoritatively. It remains behind the client plan's testing gate. If later justified, give it a separate capability and explicit per-profile opt-in; it must not be hidden inside refinery sorting or loadout maintenance.

Inventory, production-queue, block-group, ownership, and mechanical-group changes mark a profile dirty. Coalesce those events and evaluate dirty profiles on a bounded scheduler; do not recreate a programmable-block-style full-grid polling cycle. Keep per-tick limits for profiles evaluated, conveyor reachability queries, physical inventory swaps, loadout transfers, and assembler queue additions. A save or shutdown flushes settings but does not need to persist live work queues because the game already persists refinery inventories and assembler queues.

This mode is the actual solution to duplicate multi-client target batches and offline maintenance. The client-only implementation remains useful without it, but cannot elect a single durable automation owner on an unmodified server.

## Explicit utility jobs

Assembler drains are request-driven jobs, not maintainers. The companion is useful because it can validate and execute the complete multi-step operation from authoritative state, batch status messages, and finish after the initiating client closes the terminal or disconnects. Jobs are bounded and live only for the current server process; after a server restart, report them interrupted rather than reconstructing intent from inventory state.

### Bottle refill retired

Custom bottle refill is retired on SE 1.210+. Use native generator bottle pulling/auto-refill or a supplied Medical Room, Survival Kit or Refill Station. These are not identical to the removed job: native pulling does not promise to return bottles to their original cargo. The plugin leaves native settings untouched. The old `RefillBottles` wire value remains reserved and returns `PolicyDisabled`; do not renumber protocol actions.

### Drain idle assemblers

Immediately before touching each assembler, recheck that it is in assembly mode, its queue is empty, it is not producing, the profile principal retains the configured ownership relation, and the Manual override is absent. Exclude disassembly-mode assemblers even with an empty queue because their output contains material deliberately staged for disassembly. Drain only non-Reserved input and output inventories through the normal authoritative deposit planner. If its mode changes or a queue appears during the job, skip that assembler without modifying its mode or queue.

Drain jobs use authenticated, idempotent request IDs, per-player and per-scope rate limits, maximum item/allocation counts, cancellation, progress, and structured partial results. Cancellation stops future steps but does not roll back transfers already accepted by the game.

## Shared planning logic

The client and server should produce equivalent deposit allocations, refinery priorities, blueprint choices, component deficits, loadout deficits, exclusion filtering, and assembler assignments from equivalent snapshots. Keep these calculations pure and independent from either runtime. The shared source may depend on `VRage`, `VRage.Library`, and `VRage.Game` value and object-builder types such as `MyDefinitionId` and `MyFixedPoint`, but not `Sandbox.Game`, GUI types, session singletons, or either executor. Pass identities, definition data, and immutable snapshots into it; never read `MySession.Static.LocalPlayerId` or `LocalHumanPlayer` there. Maintain versioned golden test vectors for equivalent client/server results.

The executor remains platform-specific:

- The client executor issues vanilla user transfer requests.
- The server executor mutates inventories through the appropriate authoritative game APIs.

## Server configuration and telemetry

Initial configuration should be limited to controls the server operator actually needs:

- Enable or disable companion transfers.
- Enable or disable shared scope profiles, server-owned refinery sorting, server-owned component maintenance, server-owned loadout maintenance, assembler-drain jobs independently.
- Maximum intents per player over a time window.
- Maximum physical allocations per intent.
- Maximum dirty profiles, conveyor reachability queries, refinery swaps, loadout transfers, assembler additions, utility jobs, and job allocations processed per update window.
- Enabled deposit policies.
- Whether player-initiated admins may use the vanilla destination-check bypass; disabled by default.
- Whether unattended automation accepts `FactionShare` or requires `BigOwners` membership.
- Who may create, edit, share, bind, or delete owner/faction profiles and how long orphaned profiles are retained.
- Logging or telemetry level.

Expose these global operator controls through a Magnetar PluginSdk `PluginConfig` property discoverable on the plugin entry point. Keep dynamic ship profiles out of that static configuration schema.

Useful runtime statistics include accepted and rejected intents, profile loads and revision conflicts, active/paused automation profiles, refinery swaps, loadout transfers, assembler runs added, bottle and drain job outcomes, partial transfers, validation failures by reason, execution duration, dirty-profile backlog, and queue depth. Log companion rejections in a structured form comparable to the server's failed-vanilla-validation list. Avoid recording item manifests, stock targets, or player cargo contents unless explicitly enabled for diagnostics.

## Implementation order

The initial persistence-only vertical slice has been extended through the feature steps below. Publication supports revision-bound 16 KiB pages up to a 256 KiB document; explicit section patches preserve untouched regions. The list remains the target architecture and acceptance checklist, not a claim that every multiplayer/failure scenario has passed. Current defaults and remaining limitations are recorded in the implementation status document.

The 2026-09-05 live Magnetar owner-path smoke test verified discovery, fetch, publication through revision 2, inspection, backed-up local adoption, and debounced server persistence with a previous-revision backup. Multi-client permission/conflict and lifecycle acceptance remain pending; see [SERVER_COMPANION_IMPLEMENTATION.md](SERVER_COMPANION_IMPLEMENTATION.md) for evidence and limits.

The subsequent source milestone wires normal unified transfers to bounded authoritative intents, with independent capability gating and no timeout replay. It reuses definition/group adapters from `Runtime` and the pure distribution core from `Shared/Planning`. The generic adapters are game-dependent, not a claim that all remaining refinery/production/loadout calculations have been extracted into pure shared planners. Transfer execution remains disabled by default until the endpoint/conveyor comparison matrix below has passed on a dedicated server.

1. Finish and validate the complete client-only transfer, exclusions, refinery-priority, and component-target paths.
2. Define the fixed secure mod-message channel, magic/version envelope, payload limit, proactive and client-initiated handshake, and independent transfer, settings, refinery-automation, target-automation, loadout-automation, and utility-job flags.
3. Implement request-ID idempotency and cached results before adding mutations; distinguish handshake fallback from an in-flight **unknown outcome**.
4. Add the versioned scope-profile store, game-thread snapshots, lazy anchor binding, management overrides, permission checks, revisioned settings messages, and local-profile adoption flow.
5. Implement the complete vanilla-equivalent player-intent validation boundary, including authenticated identity, both endpoint ownership/access checks, proximity, 2 km separation, explicit admin policy, and identity-aware conveyor reachability.
6. Add authoritative withdrawal through validated `MyInventory.Transfer(..., spawn: false)` and structured partial results.
7. Add authoritative deposits using the three client placement policies and reject unknown or disabled policy values.
8. Add persistence-only refinery and component settings so multiple clients see one profile before enabling server mutation.
9. Add swap-based server-owned refinery sorting with dirty-event debouncing, reachability and swap limits, ownership-change pauses, and mixed refinery definitions.
10. Add server-owned component maintenance with definition-derived blueprint resolution, cooperative-assembler exclusion, maximum queue length, content-based queue accounting, integral rounding, and add-only semantics.
11. Add persistence-only Phase 2 loadout rules, then server-owned loadout maintenance using the existing target and transfer planners.
12. Add bounded assembly-only idle-assembler-drain jobs. Custom bottle refill is retired in favor of native SE systems.
13. Add bounded queues, rate limits, timeouts, cancellation, merge conflicts, event-driven split/merge/ownership handling, and orphan retention.
14. Add PluginSdk operator configuration, structured results, comparable validation logging, and runtime statistics.
15. Test companion absence, late discovery, partial capability sets, version mismatch, oversized/paged messages, disconnects, duplicate IDs, unknown outcomes, stale stacks, destroyed blocks, sorter and tube-size changes, full destinations, simultaneous editors, two automation clients, ownership/faction/admin-policy changes, server restart, profile split/merge, blueprint paste, missing mods, utility-job interruption, and world save/unload.

Transfer tests must compare companion decisions with the vanilla client for both endpoint rights, proximity and replication-dependency access, 2 km separation, direction, sorters, tube size, constraints, capacity, partial fits, and admins. Automation tests must cover swap-based refinery ordering, content-based assembler queue acknowledgement, cooperative and disassembly exclusions, maximum queue length, ownership-change pauses, and bounded reachability work. Bottle tests must explicitly exercise empty and partially filled stacks in both tanks and generators; drain tests must race queue and mode changes. Persistence tests must cover save snapshots, unload flush, lazy load binding, split/merge bursts, destroyed anchors, and pasted grids.

## Additional implementation optimizations

- Share the actual profile DTOs, enums and migration code between client and server rather than maintaining two schemas. Keep the existing XML names for local-file compatibility.
- Coalesce settings-change notifications per connected reader. Send only profile identity and revision, then fetch full settings on demand. Recheck authorization when sending, and process a bounded number of readers per update.
- Bound inbound bytes, per-sender backlog, global backlog, processing rate and cached-result memory independently. Reject work before mutation when the result journal is full; never evict a still-replayable result to make room.
- Bind requests to a world-session epoch and a short server-time deadline. An expired request must not become executable after its cached result is pruned. Handshake establishes the clock offset so client wall-clock skew does not cause immediate rejection.
- Cache XML serializers and debounce world-profile writes. Profile changes contain intent only; no inventory manifest is scanned or serialized for settings persistence.
- Reuse the game's native terminal access/ownership validator for settings access. This does not substitute for the additional conveyor, distance, endpoint and mutation checks required by future transfer intents.

## UX companion boundary — 2026-09-05

Display ordering, drag rearrangement and the two-state fallback icon are entirely client-local. They do not modify shared profiles or physical inventory order. Companion transfer batching remains the accelerated path; preserve validation and authoritative receipts. Remove the custom refill executor and its operator option; old requests fail safely. The client remains compatible with an older companion for retained actions.
