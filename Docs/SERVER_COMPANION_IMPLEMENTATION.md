# Server companion implementation status

## First milestone: persistence-only shared profiles

Implemented in source; not yet accepted in a live dedicated-server multiplayer session.

- Magnetar `IPlugin` lifecycle, PluginSdk-discoverable operator configuration and structured logging/statistics.
- Fixed secure mod-message channel `48763`, magic `USCP` (`0x55534350`), protocol version 1, reliable discovery and request/result messages. No plugin-defined network events.
- Client-initiated discovery every 20 seconds, proactive advertisement on player join, capability expiry after 45 seconds. Only `SharedProfiles` is advertised. All inventory transfers and automation still use the standalone client paths.
- Authenticated transport sender, current online identity, native terminal access/ownership validation, independently resolved mechanical scope. Profile creation requires anchor `BigOwners` membership; subsequent publication also requires the recorded owner. Optional faction access is read-only and requires explicit sharing. A profile whose principal no longer owns its anchor is inaccessible; there is no implicit ownership takeover or admin override.
- UUID profiles with revisions and explicit anchor grid IDs, preserving named group selectors and unknown mod definition strings. Requests resolve current mechanical membership. Multiple anchored profiles in one merged scope return conflict; split-off and pasted grids do not inherit the profile. Orphaned records are retained, not silently rebound.
- Fetch and compare-and-swap publication of priorities, component targets/blueprint choices, groups, loadouts, policy and exclusions. A stale revision returns the current authorized snapshot. Payload owner/identity/anchor metadata is never trusted to grant authority.
- World-local XML store, two-second debounced atomic writes with backup, checkpoint/unload flush, lazy entity resolution. Corrupt or unsupported data disables shared profiles without overwriting the existing file. An acknowledged publication is accepted in memory; crash durability follows the next successful flush, not the network acknowledgement.
- A duplicate-result journal is reserved before profile mutation. Same sender/ID with different bytes is rejected; exact duplicates return cached results after current access is checked. A timeout reports unknown outcome and never causes automatic replay.
- Revision-only invalidations for authorized readers, coalesced per recipient and processed two recipients per update. Clients fetch explicitly and never silently adopt changes.
- Client **Inventory groups → Shared profile** screen: fetch, inspect, publish, adopt and faction-read option. Adoption retains unmatched private groups and their loadout rules, preserves the local world/anchor identity, writes a timestamped `before-adoption` backup, and turns local maintenance switches off. Publishing retains groups present only in the fetched server snapshot. Local editing remains local until explicitly published.

Profile DTOs and enums now live in `Shared/Profiles`; historical `ClientPlugin` namespaces are retained solely to avoid unnecessary local XML/API churn. These files contain no game-session or GUI dependencies.

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

Not verified: live secure-message routing, native permission checks against connected players, multi-client revision invalidation, join/reconnect/world-unload timing, Quasar rendering, the new profile screen in-game, or real dedicated-server save/restart behavior. No server was deployed or started and the running game was not restarted for this work.

## Next implementation gates

1. Run the dedicated-server acceptance pass for this milestone: companion absent/present/disabled; two clients; owner versus faction/non-owner; nearby versus remote terminal; concurrent publication; ownership loss; disconnect/timeout; save/restart; mechanical split/merge.
2. Add paged snapshots, granular revisioned patches, explicit bind/delete/orphan-recovery UI and retention policy. Publication currently retains server-only groups in the client UI; deleting those remotely needs an explicit operation.
3. Extract the remaining pure planning calculations. Implement the **complete** transfer validation boundary before enabling transfer capabilities: both endpoints, same-logical-group separation, character/replication access, identity-aware conveyor direction/sorters/tube size, constraints, integral quantities and capacity. Then withdrawals, deposits and distinct mechanical-group transfers with structured partial amounts.
4. Add event-coalesced authoritative refinery, production and generic loadout schedulers. Only then advertise ownership of those loops and suspend corresponding client maintenance. Shared settings alone do not prevent two clients manually enabling competing local loops.
5. Add explicit bounded bottle-refill and idle-assembler-drain jobs, with the planned no-progress/race checks and cancellation semantics.

The complete roadmap and safety requirements remain in [SERVER_COMPANION_PLAN.md](SERVER_COMPANION_PLAN.md).
