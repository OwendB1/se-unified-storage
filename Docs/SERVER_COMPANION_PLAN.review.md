# Review of SERVER_COMPANION_PLAN.md

Reviewed against decompiled Space Engineers Dedicated Server code, version 1.210.014 b0 (se-dev-server-code, handbook version check: MATCH), cross-checked with the client build of the same version. File references are relative to `Data/Decompiled/` of those skills; the game-logic assemblies are identical on both sides. The original plan is not modified; this document only records review comments. Claims about the Magnetar PluginSdk were not verified because that SDK is outside the reviewed code.

## Verdict

The plan correctly positions the companion as an augmentation: the client stays the UI and stays functional alone, the server owns only what needs authority or persistence, and no synthetic inventory exists on either side. The capability split, the anchor-based profile identity, the revisioned settings protocol and the add-only automation rules are all sound and consistent with the client plan.

Three things should be fixed in the text before implementation:

1. The transport is unspecified. It must be the vanilla mod message channel; plugin-defined network events cannot give the "unmodified server ignores us" fallback the plan depends on.
2. The companion executes mutations locally, which bypasses every `[Server(ValidationType.Access | ValidationType.Ownership)]` check the vanilla path applies. The plan says it validates ownership, scope and conveyors, but should list the exact vanilla rules it replaces so it is never more permissive than the vanilla client.
3. The utility jobs and sorter inherit the same code facts as the client: bottle refill needs an explicit refill call, refinery reordering is a swap, and the assembler queue broadcast does not mean an insert happened.

The rest are refinements and confirmations.

## Verified claims

| Plan claim | Code | Status |
|---|---|---|
| Server can execute transfers on the game thread | `MySandboxGame.Static.Invoke(action, name)`; `MyInventory.Transfer(src, dst, srcItemId, dstIdx, amount, spawn)` is static and returns immediately when `!Sync.IsServer` | Confirmed |
| Server can validate conveyor routes, direction, sorters, tube size | `MyGridConveyorSystem.ComputeCanTransfer(start, end, itemId)` (public static, used by `MyInventory.CanTransferTo`), `Reachable(source, endPoint, playerId, itemId, predicate)` which applies access, sorter and `NeedsLargeTube` predicates under `SetTraversalPlayerId` | Confirmed |
| Server can resolve the authenticated player | `MyEventContext.Current.Sender` for events; the secure mod message handler receives the Steam id derived from `Sync.Clients.TryGetClient`; `MySession.Static.Players.TryGetIdentityId(steamId)` | Confirmed |
| Ownership and access checks exist server-side | `MyCubeBlock.GetUserRelationToOwner(identityId)`, `MyTerminalBlock.HasPlayerAccess(identityId)`, `MyReplicableRightsValidator.GetBigOwner` | Confirmed |
| Game persists refinery inventories and assembler queues | Both are part of block object builders; `MyRefinery.RebuildQueue` regenerates its queue from input contents on the server | Confirmed |
| Save and unload hooks for the profile store | `MySession.OnLoading` / `OnUnloading` (static), `MySession.Static.OnSavingCheckpoint` (instance), `MySession.Static.CurrentPath` for the world folder | Confirmed |
| Bottle refill can be executed authoritatively | `MyGasGenerator.RefillBottles()` is public; `MyGasTank` exposes it through the explicit `IMyGasTank.RefillBottles()` interface implementation | Confirmed, see caveats |
| Assembler queue additions from the server | `MyProductionBlock.InsertQueueItemRequest` executes `InsertQueueItem` locally on the server and broadcasts `OnAddQueueItemSuccess` | Confirmed, see caveats |

## Findings that change the design

### 1. Specify the transport: vanilla mod messages, not plugin events

`Sandbox.Game/Sandbox/ModAPI/MyModAPIHelper.cs` implements `SendMessageToServer`, `SendMessageTo(id, bytes, steamId, reliable)`, `SendMessageToOthers` and `RegisterSecureMessageHandler(id, (channel, bytes, senderSteamId, fromServer) => ...)`. All of them ride on static `[Event]` methods that already exist in the vanilla event table, so:

- An unmodified server receives the client's handshake and drops it silently in `HandleMessage` (no listener for that channel). That is exactly the fallback the plan needs, and it costs nothing.
- The secure handler's sender id comes from `MyEventContext.Current.Sender` and `Sync.Clients`, not from the payload, which satisfies "the server derives the requesting player from the authenticated network context".
- Messages can be targeted at one Steam id, which is what "broadcast accepted changes only to clients currently authorized to see that scope" requires.

Plugin-defined `[Event]` methods would need identical event tables on client and server; a client with the plugin talking to a server without it would send unknown event ids. Rule that option out in the plan.

Add to the protocol section: a fixed channel id with a magic header and protocol version in every message, reliable delivery for everything except optional progress updates, and a payload size ceiling (keep intents and snapshots small; page large profiles).

### 2. State the vanilla validation the companion replaces

When the companion mutates inventories or queues, `MyEventContext.Current.IsLocallyInvoked` is true, so every `[Server(Access | Ownership)]` check on the vanilla request paths is skipped. The plan's "validate player access, ship scope, conveyor routes ..." must therefore be at least as strict as what the vanilla server applies to a client request, which is (from `MyReplicableRightsValidator.HasRights(MyCubeBlock)` and `MyInventory.CheckCanAddItems`):

- Ownership: for both the source and the destination block, `GetUserRelationToOwner(identityId)` must be `Owner`, `FactionShare` or `NoOwnership` (or the remote-admin "use terminals" flag).
- Access: the requester's character exists and is within `3 × MyConstants.DEFAULT_INTERACTIVE_DISTANCE` of any grid AABB in the block's **logical** group, or that grid is in the character's replication dependencies.
- The two owning blocks are not more than 2 km apart while in the same logical group.
- Admins bypass the destination checks on the vanilla path.

Recommendations:

- Player-initiated intents (withdraw, deposit, Rebalance, Run now, utility jobs) apply all of the above with the requester's identity. This keeps the companion from becoming a remote-looting or conveyor-bypass tool, which is the main thing operators will check before installing it.
- Server-owned automation cannot apply the proximity rule (nobody is there); state explicitly that it runs under the profile principal's ownership relation only, and that the operator configuration decides whether `FactionShare` is sufficient or `BigOwners` membership is required (`MyReplicableRightsValidator.GetBigOwner` is the vanilla stricter check).
- Do not inherit the admin bypass. If admins need it, make it an explicit operator switch.
- Conveyor validation must use the requester's identity for the traversal predicates (`Reachable(..., playerId, itemId, ...)`), not the server's; sorter and access predicates are per player. The client's reachability wrapper uses `MySession.Static.LocalPlayerId`, which is invalid on a dedicated server, so this is one of the places the "shared planning logic" must stay pure and take the identity as a parameter.

Worth adding to Goals: because the vanilla server never validates conveyor reachability for user transfers (`InventoryTransferItem_Implementation` only checks entity existence, `CheckCanAddItems` and amount), the companion is the first place that enforcement happens server-side. That is a real security benefit for operators and should be stated, with the corollary that the companion must never be more permissive than the vanilla client UI.

### 3. Utility jobs and sorter: same code facts as the client

- **Refill bottles.** `MyGasTank` and `MyGasGenerator` fill bottles only when `AutoRefill` is on or a refill is explicitly triggered. The companion should call `IMyGasTank.RefillBottles()` / `MyGasGenerator.RefillBottles()` itself after staging, on the game thread, without touching the Auto-Refill setting (the plan already forbids toggling settings). Preconditions from `CanRefill`: tank powered, `FilledRatio > 0`, `CanStore`; generator has ice (or creative) and `CanProduce`. Both refill loops pattern-match `GasLevel: 0` while `CanRefill` tests `GasLevel < 1`; verify in-game whether partially filled bottles refill before promising it. Bottles with identical gas level stack (`MyObjectBuilder_OxygenContainerObject.CanStack`), so "one bottle at a time" is one stack at a time.
- **Refinery sorting.** `MyInventory.TransferItemsInternal` with `src == dst` swaps the two slots when the items cannot stack and merges when they can; there is no insert. Plan the server sorter as pairwise swaps. The server can perform all swaps for one refinery synchronously in one tick; each swap fires `ContentsChanged` and sets `m_queueNeedsRebuild`, so only one queue rebuild happens on the next update. Keep the per-tick swap limit anyway for large refinery farms.
- **Component maintenance.** `InsertQueueItem` silently inserts nothing when `CanUseBlueprint` fails or the queue is at `MySession.Static.MaxProductionQueueLength`, yet `OnAddQueueItemSuccess` is still broadcast. Inserting at `-1` merges into the last item when the blueprint matches and reuses its `ItemId`. Account by queue content and blueprint amount, never by item id or by the broadcast. Exclude cooperative (slave) assemblers, whose queues are fed by a master, in addition to disassembly mode, matching the terminal's own helper (`Mode != Disassembly && UseConveyorSystem && !CooperativeMode`).
- **Drain idle assemblers.** `IsProducing` and `IsQueueEmpty` are available server-side and authoritative. Keep disassembly-mode assemblers out even with an empty queue, since their output inventory holds the components the player intends to disassemble.

## Important refinements

### Executor API

Use the static `MyInventory.Transfer(src, dst, srcItemId, dstIdx, amount, spawn: false)` after the companion's own validation. It goes through the same `TransferItemsInternal` as user transfers, so partial fits (`FixTransferAmount` clamps to `ComputeAmountThatFits`) and same-inventory swaps behave identically to the client path, which keeps the "equivalent allocations" promise honest. `TransferItemsFrom(..., useConveyors: true)` also exists but is index-based and uses a per-frame LRU cache of `ComputeCanTransfer`; prefer explicit validation plus `Transfer`.

### Duplicate submission across the fallback boundary

The handshake timeout and the intent timeout are different cases. If a companion is present but slow, a client that times out and re-issues the same operation through the vanilla path can double-move items. The plan's "the same in-flight operation must not be submitted twice" should be strengthened to: a request id is never re-executed through either path; on timeout the client only refreshes from replicated state and reports "unknown outcome"; the server answers a repeated request id with the cached result rather than executing again.

### Access for unattended automation is an ownership decision, not an access decision

The plan's pause rules (owner loses access, anchor changes ownership, profile collision after merge) are right. Add "anchor grid's `BigOwners` no longer contains the principal" as the trigger for ownership-based pauses, since that is the value that changes on raids and ownership transfers, and re-evaluate on `MyCubeGrid` ownership change notifications rather than on a timer.

### Persistence details

- `OnSavingCheckpoint` runs on the game thread during save; take a snapshot there and write synchronously or hand the snapshot to a writer thread. Debounced writes must never serialize live objects off-thread.
- `MySession.OnUnloading` is the flush point for unload; `OnLoading` fires before entities exist, so bind anchors lazily when their grids appear rather than at load.
- The plan keys profiles by anchor grid entity id. Entity ids survive save and load but not blueprint paste or projector rebuild. The "orphaned, recoverable for a retention window" rule already covers that; mention that a pasted copy of a ship therefore starts without a profile by design.

### Mechanical group changes

There is no group entity id, as the plan says. For split and merge detection subscribe to grid split/merge notifications and re-evaluate `MyCubeGridGroups.Static.Mechanical.GetGroup(anchor)` on the game thread, coalescing bursts. The vanilla terminal already keeps a mechanical owner list per interacted grid by listening to each grid's conveyor-system `BlockAdded/BlockRemoved`; the same pattern works server-side and gives the "profile follows the anchor" behaviour without hashing members.

### Shared planning logic

Feasible as a shared project: both sides can reference `VRage`, `VRage.Library` and `VRage.Game` for `MyDefinitionId`, `MyFixedPoint` and object-builder types without pulling in `Sandbox.Game`. Constrain the shared project to those references and pass identities, snapshots and definition data in; the reachability, access and mutation calls stay in the two executors. The pure planner must not touch `MySession.Static.LocalPlayerId`, `LocalHumanPlayer` or anything GUI-related, since those are null or invalid on a dedicated server.

### Rate limits and dirty-profile scheduler

The design is right. Two concrete numbers worth exposing in configuration because they map onto vanilla costs: conveyor reachability queries per tick (the pathfinder takes a global lock and is the expensive part, not the transfer itself) and `MaxProductionQueueLength` awareness (queue additions beyond it are silently dropped by the game, so the scheduler should stop earlier rather than count them as "added").

## Minor comments

- "Rebuild the transfer plan from current authoritative server inventory state": on the server, `MyInventory` contents are authoritative and `ContentsChanged` fires synchronously, so the recheck-before-mutation step can be a plain re-read; no waiting for replication is needed there.
- The plan's "deposit policy" field on intents should be validated against the operator's "enabled deposit policies" list and against the client's own policy enum version; reject unknown values with a structured reason rather than defaulting.
- Telemetry: `MyMultiplayerServerBase.GetFailedValidations()` is the operator-visible list of failed vanilla validations. Companion rejections should be logged in a similar structured form so operators can compare the two.
- The handshake should be initiated by the client on world ready and also answered proactively by the server on player join, so a client that loads its plugin late still discovers the companion.
- `MyVisualScriptLogicProvider`, `MyAPIGateway` and the rest of the mod API are available to server plugins; using `MyAPIGateway.Multiplayer` for the channel keeps the code identical on both sides.

## Consistency with the client plan

- The client plan's independence requirement is preserved: nothing in this plan requires the client to change behaviour when the companion is absent, and the mod-message transport guarantees silence rather than errors on an unmodified server.
- The client plan (see its review) must treat conveyor reachability as a hard invariant because the vanilla server does not check it. The companion is the only place that check can move server-side; the two documents should say so in matching words.
- The three placement policies, the swap-based sorter, the content-based queue accounting and the refill request are the same on both sides, so the "equivalent allocations from equivalent snapshots" goal is achievable with golden vectors.
- Both plans should carry the same in-game verification item for partially filled bottles, since the answer changes the trigger condition in both.
