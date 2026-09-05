# Server companion implementation status

## Current source — full companion feature pass

The sections below this one record earlier milestones. This section supersedes their feature-status and size-limit statements. The full companion build was published at `f5f52e8e290bf4a51e2e52dc5a23ad2c55e8d000` and loaded by NewTest, with mutation capabilities explicitly enabled by its operator for acceptance testing. Subsequent gap-closure source changes require a new load before their live results count. Keep mutation capabilities disabled on production servers until acceptance passes.

Implemented:

- Authoritative withdrawals, deposits and selected-row rebalance, including cross-mechanical-ship transfers. Client requests contain intent/selectors, never trusted allocations; server execution uses native transfers after endpoint/path checks.
- Shared runtime definition/group/planning adapters; value-only distribution and automation calculations in `Shared/Planning` (ore ordering, blueprint choice, co-product-aware production assignment and loadout gaps). Disassembly queues no longer count as pending component production; missing modded Productivity upgrades default to zero.
- Independently gated refinery sorting, component queue additions and generic loadout services. Shared profile ownership is a separate explicit owner-only operation. Operator enablement or publishing settings alone never claims a loop.
- Compact ownership manifests (maximum 4,100 bytes), per-scope client suppression, guards for previously queued client work, 60-second server handover delays, and a 45-second initial client discovery grace period. Once coordination is seen, stale ownership does not resume local maintainers. Arbitrary scripts and old client versions cannot be coordinated; use updated clients for acceptance testing.
- Server scheduling coalesces inventory/queue changes, conveyor and terminal-group changes, grid ownership, split/merge and completed connection changes. One profile is considered per 100 ms, with a configurable minimum service interval and a 15-second topology audit. Each pass defaults to four mutations. No-op services wait for dirtiness; uncertain mutations pause that profile until explicitly revised.
- Owner run-now controls and status queries. Faction access for unattended endpoints is separately configurable and defaults off; anchor ownership remains mandatory. Missing/merged/changed scopes fail closed.
- Explicit bottle-refill and idle-assembler-drain jobs with sender-bound status/cancel, one active job per sender/ship, 32 retained jobs globally, 120-second active deadlines and 60-second completed-result retention. Cancellation never rolls back game mutations. Jobs use current rights while the initiator is online; disconnected continuation requires anchor ownership and the configured principal relationship. Access loss otherwise interrupts work.
- Refill stages one empty bottle at a time, checks definition-derived gas compatibility, requests native refill without toggling auto-refill, observes progress for up to five seconds, and returns the exact staged bottle to its original inventory. Changed/stranded bottles are reported with their block/inventory. Initially at most 16 source stacks are selected; a multi-bottle stack contributes one bottle per explicit pass. Partially filled bottles remain deliberately excluded.
- Revision-bound paged snapshots/uploads: 16 KiB raw pages, 16 pages / 256 KiB document maximum. Uploads are bound to sender, anchor, profile and base revision, retained for two minutes, limited to one per sender and 16 globally. Client page sequences reserve the request channel and pace pages at 1.2-second intervals. A complete upload commits only after current revision/ownership and schema validation.
- Owner-only section patches; paged owned-profile catalog; explicit bind/recover and delete UI. Rebinding disables automation. Deletion archives the removed profile independently of ordinary `.bak` saves. Orphans are retained forever by default; opt-in day-based expiry archives at most one profile per audit, after a world-load grace period.
- PluginSdk operator switches, service budgets, policy controls, orphan retention, request timing, refinery/queue counters and active-job/profile gauges.

Verification: both targets build against the installed game/SDK; core checks and the temporary companion harness pass. No additional regression project was added. The client devfolder was restarted successfully and logged `UnifiedStorage: Successfully loaded`. This proves source-loader/startup compatibility, not the new server gameplay paths. The live dedicated-server matrix below remains required.

Current limitations to exercise or refine during acceptance:

- Large manual action payloads and individual section patches still obey the 48 KiB packet ceiling; paging is for profile fetch/publication. Reduce/split a section if it exceeds that ceiling.
- Native graph traversal cost is not bounded internally; only call counts, enumerated scope sizes and mutation counts are bounded. Profile before increasing budgets. No measured conveyor speedup is claimed.
- Refill tries the original inventory, then eligible Unified Cargo using the selected enabled policy and normal rights/exclusion/path checks. Pair and mutation budgets bound fallback attempts; failed or uncertain jobs identify the possibly stranded bottle. Utility jobs are not persisted across process restarts.
- A metadata-only server status query reports current ownership and the latest scheduler result, not a detailed per-machine dashboard. The client component-target panel shows native machine-state/eligibility reasons. Shared production planning updates projected assembler workloads between assignments. Recipe discovery, eligibility and scope reads remain runtime adapters, not pure value planners.
- A very late companion discovery after the standalone grace period cannot retract vanilla requests already sent. All automated clients must run a compatible build; programmable blocks and unrelated plugins remain outside this coordination mechanism.
- New server paths, Quasar rendering, two-client ownership transitions, authorization races, save/restart and the full sorter/constraint/bottle matrix still require live testing against the newly published build. Prior live persistence-only evidence does not establish these results.

### Gap-closure verification — 2026-09-05

The client devfolder includes the component-target validation, live status refresh and projected-workload changes. NewTest still runs published companion `f5f52e8`; the new server bottle-return fallback has **not** been loaded or verified live. The final status-tooltip addition compiles but also needs a client reload. Hub pins remain unchanged pending acceptance.

- Client/server Release builds pass. Build targets warn that their default binary-deployment folders are absent; the active client uses source loading instead. Existing core tests and 215 checks in the temporary companion harness pass, including projected workload updates between production assignments. `git diff --check` passes.
- On the provided docked rigs, setting Steel Plate target to 368 with stock 367 and invoking **Craft deficits** produced exactly one plate in the assembler output. The still-open target panel refreshed to stock 368, queued 0 and **On target**. Maintenance remained off, and the temporary target was reset to zero afterwards.
- Saving a blank target disabled the target. An oversized quantity was rejected without changing the saved value. These were UI interactions, not direct profile-file edits.
- Unified Cargo drag/drop with an explicit quantity of one transferred a Computer between the two distinct mechanical scopes and back. Native inventory observations were 41/44 → 40/45 → 41/44. Both operations used the plugin UI, not the Remote fixture-transfer endpoint.
- 21 live assertions passed for independent scope selection, suit/grid switching, search preserving selection, and two disable/re-enable cycles with working vanilla suit/grid controls. Re-enabling selected the accessed network on both sides.
- The target and inventory screenshots were visually inspected at the current ultrawide resolution. No overlap appeared in the inspected layouts; this does not establish other resolutions, gamepad navigation or the complete flicker/performance matrix.

### Hub-loaded utility checks — 2026-09-05

After the operator selected testing through the public hub, NewTest compiled and loaded `22764751f1ad57a4130cef3975b01b4a809dd714` on game 1.210.14 / .NET 10.0.11. The local client rejoined successfully after the unrelated auth service recovered. The temporary devfolder configuration did not survive the Quasar launch path; hub loading is the active verification route.

- Bottle fallback passed through the plugin's **Refill** action. A bottle was staged from an initially empty connector, then 114 existing radio components occupied 7,980 of its 8,000 litres. The original could no longer fit the 120-litre bottle. The job returned the exact staged bottle to Unified Cargo, left the filler empty, and reported `InsufficientStock` because no ice was available. All 114 radio components were then returned to cargo (160 total), restoring the connector to empty.
- With ice already available in the generator, a subsequent explicit refill completed: one bottle, three mutations, no failure. The cargo UI displayed that bottle at 100%; the generator consumed four units from the 400 spawned test ice. An earlier attempt before ice arrived correctly returned an unfilled bottle and reported partial rather than retrying indefinitely.
- Idle drain moved the produced plate from assembler output into cargo (367 → 368), but incorrectly reported `StackChanged` afterwards. Source inspection confirmed that fallback allocations were still attempted after fulfilling the original request. The follow-up fix tracks remaining quantity per drain operation and skips exhausted work; it builds but needs a fresh-server replay.
- Historical shutdown logs exposed a second defect: Magnetar's worker-thread disposal called the game-thread-only secure-handler unregister API, skipping subsequent cleanup and profile flush. The follow-up fix makes disposal idempotent and dispatches handler removal to the update thread when needed, while retaining synchronous profile flush. If the process stops before another update, the queued handler removal cannot run; process teardown releases it. Shutdown/save-restart verification remains required.
- Server Release build, existing core checks, 215 temporary companion checks and whitespace validation pass after these fixes. These checks do not substitute for live replay of the two changed paths.

Remaining acceptance includes fresh-server drain/shutdown checks, the remaining scheduler/authorization/lifecycle matrix, and two-client coordination. A second client/player is not currently confirmed. No full multiplayer acceptance or measured conveyor speedup is claimed.

## First milestone: persistence-only shared profiles

Implemented in source. A single-client owner-path smoke test passed on a live Magnetar dedicated server on 2026-09-05; the full multiplayer acceptance matrix remains outstanding.

- Magnetar `IPlugin` lifecycle, PluginSdk-discoverable operator configuration and structured logging/statistics.
- Fixed secure mod-message channel `48763`, magic `USCP` (`0x55534350`), protocol version 1, reliable discovery and request/result messages. No plugin-defined network events.
- Client-initiated discovery every 20 seconds, proactive advertisement on player join, capability expiry after 45 seconds. By default only `SharedProfiles` is advertised. Transfers have a separate default-off capability; automation still uses standalone client paths.
- Authenticated transport sender, current online identity, native terminal access/ownership validation, independently resolved mechanical scope. Profile creation requires anchor `BigOwners` membership; subsequent publication also requires the recorded owner. Optional faction access is read-only and requires explicit sharing. A profile whose principal no longer owns its anchor is inaccessible; there is no implicit ownership takeover or admin override.
- UUID profiles with revisions and explicit anchor grid IDs, preserving named group selectors and unknown mod definition strings. Requests resolve current mechanical membership. Multiple anchored profiles in one merged scope return conflict; split-off and pasted grids do not inherit the profile. Orphaned records are retained, not silently rebound.
- Fetch and compare-and-swap publication of priorities, component targets/blueprint choices, groups, loadouts, policy and exclusions. A stale revision returns the current authorized snapshot. Payload owner/identity/anchor metadata is never trusted to grant authority.
- World-local XML store, two-second debounced atomic writes with backup, checkpoint/unload flush, lazy entity resolution. Corrupt or unsupported data disables shared profiles without overwriting the existing file. An acknowledged publication is accepted in memory; crash durability follows the next successful flush, not the network acknowledgement.
- A duplicate-result journal is reserved before profile mutation. Same sender/ID with different bytes is rejected; exact duplicates return cached results after current access is checked. A timeout reports unknown outcome and never causes automatic replay.
- Revision-only invalidations for authorized readers, coalesced per recipient and processed two recipients per update. Clients fetch explicitly and never silently adopt changes.
- Client **Inventory groups → Shared profile** screen: fetch, inspect, publish, adopt and faction-read option. Adoption retains unmatched private groups and their loadout rules, preserves the local world/anchor identity, writes a timestamped `before-adoption` backup, and turns local maintenance switches off. Publishing retains groups present only in the fetched server snapshot. Local editing remains local until explicitly published.

Profile DTOs and enums now live in `Shared/Profiles`; historical `ClientPlugin` namespaces are retained solely to avoid unnecessary local XML/API churn. These files contain no game-session or GUI dependencies.

## Second source milestone: bounded authoritative transfers

Implemented locally; not deployed or verified against the running dedicated server. `CompanionConfig.Transfers` defaults to `false`. No public registry pin was advanced for this milestone.

- Added reliable `Transfer` intents and structured receipts to the existing authenticated, epoch/deadline-bound request journal. The same request ID cannot execute twice; exceptions during mutation report unknown outcome rather than a retryable rejection.
- The client sends selectors, a concrete seed stack identity, the requested fixed-point amount, policy and restrictive local exclusions—not physical allocations. Both source and destination may name independent mechanical ship scopes. Named terminal groups remain names; network selection uses the same component resolver on both runtimes, including opposing-sorter overlap merging and omission of portless blocks.
- The server resolves game definitions, group/role filters, stack compatibility and current membership itself. Stateful items use their exact seed identity; additional stacks must satisfy the game's bidirectional `CanStack` rule. Shared exclusions are ORed with local intent exclusions and cannot be removed by the request.
- Each allocation checks attached inventory identity, current native endpoint access/ownership, 2 km separation within the same logical group, constraints, whole-item quantities and capacity. Conveyor checks use `ComputeCanTransfer`, identity-aware item/large-tube `Reachable`, and plain directed `Reachable`. Character endpoints are limited to the sender's own live character and require a nearby physical terminal; remote-terminal access alone cannot teleport items into a suit. The blanket vanilla destination-admin bypass is not inherited.
- The server executes `MyInventory.Transfer(..., spawn: false)` synchronously and returns the native moved amount. Work exhaustion produces a partial result, never automatic continuation/replay. Reserved and Manual inventories are excluded; No Unified Cargo Destination remains enforced for cargo deposits.
- Existing Stack First, Fill First and Even By Item use the same distribution planner as the client. Operators can disable policies independently. Withdrawal does not depend on an enabled deposit policy.
- Unified drag/drop, double-click and their existing gamepad path select the companion when advertised; absent capability keeps the vanilla implementation. Busy requests are not silently sent through a second path. Local transfer starts/maintenance wait while a companion request is pending. Rebalance, refinery sorting, production, loadouts and utility jobs have **not** been routed to server execution.
- Reused game-dependent inventory/group/planner adapters now live in `Runtime`; pure distribution arithmetic lives in `Shared/Planning`. Namespaces remain compatible. Both source descriptors include the new directory; the published registry revisions still refer to the earlier persistence-only implementation.

Bounds: per selected scope, 128 mechanical grids, 8,192 fat blocks and 256 accessible inventories by default (operator maximum 1,024); 8,192 scanned source stacks. One intent defaults to at most 32 physical allocations and 32 candidate-pair checks (operator maximum 128 each). Network decomposition is separately capped at the pair-check setting, with a minimum of two searches. Every pair check performs three native conveyor queries. The existing messages-per-update cap bounds total intents per frame. Large or heavily disconnected selections can return `WorkLimit`; these ceilings bound calls and enumerated inventories, not the internal cost of traversing a very large native conveyor graph. No global conveyor speedup is claimed without profiling.

Source verification: client/server Release builds and existing core checks pass. The temporary companion harness now passes 159 checks, including transfer DTO/receipt round trips, invalid policies/selectors/quantities, ambiguous destinations, and duplicate management overrides. No additional regression project was introduced. These checks do **not** establish live endpoint authorization, gameplay parity, or performance; keep the feature off until the dedicated-server transfer matrix passes.

## Bounds and operational semantics

| Resource | Current bound |
|---|---|
| Envelope | 100-byte fixed header; 48 KiB maximum body |
| Settings document | 32 KiB; no pagination in v1 |
| Profile collections | 128 groups, 256 loadouts, 512 targets/overrides/ore-list entries |
| Inbound queue | 64 messages globally, 4 per sender |
| Request processing | 2 messages/update by default; operator range 1–16 |
| Rate limit | 12 messages/player/10 seconds by default; includes discovery |
| Rate buckets / subscriptions | 256 each |
| Duplicate-result journal | 256 entries; at most about 12 MiB of response bodies |
| Request lifetime | At most 60 seconds in monotonic session time anchored to server UTC; client requests 30 seconds |
| Result retention | Until request deadline + 60 seconds; no early eviction |
| Client wait | 15 seconds, then unknown outcome; fetch before publishing again |
| World profiles | 128 by default, operator maximum 256 |

Oversized, malformed or rate-limited packets may be dropped without reply to avoid amplification. The client eventually reports unavailable/unknown outcome. Publication is a revision-checked whole snapshot, not yet a field-level patch. Definitions and named groups are persisted as intent, not looked up and expanded into live block IDs.

No inventory-path or conveyor-work reduction is claimed for this milestone. Its optimizations concern settings traffic, bounded request work, serializer reuse and write coalescing.

## Verification performed

- Client and server Debug/Release builds against the installed game and Magnetar SDK: no warnings/errors.
- Existing distribution core checks pass.
- 146 isolated companion checks: envelope round-trip and every truncation boundary, bad magic/version/kind/length, size ceilings, duplicate/in-flight/result handling, altered request IDs, sender isolation, capacity saturation, expiry, DTO deep copy and validation, XML DTD/depth rejection, atomic store save/reload/backup, anchor membership and merge collisions, corrupt-store preservation, and unchanged loading of the actual existing local profile file.
- These checks used a temporary harness; no additional regression project was added to the repository.

### Live Magnetar smoke test — 2026-09-05

Tested implementation revision `546f5cd5d215100d82c1e4c8ef335760fbea8982` with the Pulsar client and the companion loaded from MagnetarHub, on the user-provided local dedicated server. The authenticated player used a provided cargo hatch on an owned mechanical ship. No inventory transfers, ownership changes, server restart, or shared automation were triggered by the test.

- Inventory panels populated from the accessed cargo network; **Groups → Shared profile** opened successfully.
- Secure discovery and request/reply routing worked. Initial fetch returned `NotFound` for the ship.
- Explicit owner publication created revision 1 with faction reading off. Fetch returned that revision without adopting it. Inspect opened the vanilla mission screen with the expected policy and default groups.
- Confirmed adoption created a timestamped local `before-adoption` backup. Comparing it with the resulting local XML showed only the selected ship's `AutoSortInputs` changing from `true` to `false`. World/anchor identifiers and unrelated profiles remained unchanged; component maintenance was already off and the profile had no loadout rules. Preservation of non-empty private groups/loadouts still needs a dedicated fixture.
- Republishing the adopted settings created revision 2. The server's world-storage XML contained revision 2 and its `.bak` retained revision 1, confirming the debounced write and backup path without requiring a world save/restart.
- Screenshots of the inventory, fetched-profile inspector, and adoption screen were inspected at 3440×1440; these screens showed no overlapping controls. Evidence is retained locally under the SE Remote skill's `Screenshots/20260905_companion_*.png`, not committed with private world details.
- No companion errors appeared in the client or Magnetar logs during these operations.

Found one UI-only issue: successful discovery left the initial “Waiting for companion discovery” label unchanged even though Fetch was enabled and worked. The client now updates the status when availability changes, without overwriting operation results every frame. Release build passes with zero warnings/errors; this label fix requires a client reload and has not yet been verified in-game.

Still not verified live: denied/faction/non-owner access (the successful owner was also an admin), two-client conflicts and revision invalidation, reconnect/world-unload timing, companion absent/disabled/version mismatch, Quasar rendering, failure/timeout paths, mechanical split/merge, non-empty private-group adoption, or dedicated-server save/restart recovery. Do not treat this owner-path smoke test as full multiplayer acceptance.

## Historical implementation gates (superseded by current-source status above)

1. Run the dedicated-server acceptance pass for this milestone: companion absent/present/disabled; two clients; owner versus faction/non-owner; nearby versus remote terminal; concurrent publication; ownership loss; disconnect/timeout; save/restart; mechanical split/merge.
2. Add paged snapshots, granular revisioned patches, explicit bind/delete/orphan-recovery UI and retention policy. Publication currently retains server-only groups in the client UI; deleting those remotely needs an explicit operation.
3. Live-verify the new default-off transfer milestone against vanilla decisions: both endpoints, same-logical-group separation, character/replication access, identity-aware conveyor direction/sorters/tube size, constraints, integral quantities, capacity, stateful stacks, cross-mechanical transfers and work-limit partial results. Profile native graph cost before raising the budgets. Extract the remaining pure planning calculations and add explicit authoritative rebalance without accepting client allocations.
4. Add event-coalesced authoritative refinery, production and generic loadout schedulers. Only then advertise ownership of those loops and suspend corresponding client maintenance. Shared settings alone do not prevent two clients manually enabling competing local loops.
5. Add explicit bounded bottle-refill and idle-assembler-drain jobs, with the planned no-progress/race checks and cancellation semantics.

The complete roadmap and safety requirements remain in [SERVER_COMPANION_PLAN.md](SERVER_COMPANION_PLAN.md).
