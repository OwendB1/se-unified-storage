# Review of CLIENT_PLUGIN_PLAN.md

Reviewed against decompiled Space Engineers client code, version 1.210.014 b0 (se-dev-game-code, handbook version check: MATCH). File references below are relative to `Data/Decompiled/` of that skill. The original plan is not modified; this document only records review comments.

## Verdict

The plan is architecturally sound. Its central bet, that a client-only plugin can present a unified view and still issue nothing but vanilla, server-validated requests, holds up against the code. The definition-driven discovery (weapons, reactors, production blocks, blueprints) maps one-to-one onto real fields, and the transfer, refinery-order and assembler-queue request paths exist with the claimed validation attributes.

Four findings should be resolved before implementation starts, because they change the design rather than the details:

1. The "zombie" `MyInventory` cannot be filled through the public API on a multiplayer client (`AddItems` is a no-op unless `Sync.IsServer`). Either use the client replication entry points deliberately, or do not fake inventories at all.
2. The vanilla server does not validate conveyor reachability for `TransferByUser`. The plugin's own reachability check is the only enforcement on an unmodified server. Treat it as a hard invariant, not as a nicety.
3. `Refill Bottles` as written will hang: staging a bottle into a tank or generator does nothing unless the block's Auto-Refill toggle is on or the terminal's Refill request is sent. Both blocks expose that request, and the plugin should use it.
4. Refinery input reordering is a swap, not an insert. The sorter must be planned as a sequence of pairwise swaps.

Everything else is either confirmed or a refinement.

## Verified claims

| Plan claim | Code | Status |
|---|---|---|
| `MyInventory.TransferByUser(src, dst, itemId, dstIdx, amount)` is the vanilla user transfer | `Sandbox.Game/Sandbox/Game/MyInventory.cs`, `TransferByUser` raises `InventoryTransferItem_Implementation` marked `[Server(ValidationType.Access | ValidationType.Ownership)]` | Confirmed |
| Same-inventory `TransferByUser` reorders refinery input and the refinery rebuilds its queue | `TransferItemsInternal` handles `src == dst`; `MyRefinery.RebuildQueue` (server only, triggered by `ContentsChanged`) enumerates input stacks in inventory order and queues one blueprint per stack; production consumes the first queue item | Confirmed, but see swap semantics below |
| `MyProductionBlock.InsertQueueItemRequest` is the client-callable queue add | `InsertQueueItemRequest` → `AddQueueItemRequest` → `OnAddQueueItemRequest` `[Server(Access | Ownership)]`, then `OnAddQueueItemSuccess` `[Broadcast]` | Confirmed, with caveats below |
| `CanUseBlueprint` | `MyProductionBlock.CanUseBlueprint` checks `ProductionBlockDefinition.BlueprintClasses` | Confirmed |
| `MyInventoryOwnerTypeEnum` / `InventoryOwnerType()` is obsolete | `Sandbox.Game/Sandbox/Game/Entities/MyInventoryOwnerTypeEnum.cs` carries `[Obsolete]`; vanilla filters still call `InventoryOwnerType()` | Confirmed |
| `MyWeaponBlockDefinition.WeaponDefinitionId`, `MyWeaponDefinition.AmmoMagazinesId`, `MyGunBase` builds the constraint from that list | `Sandbox.Game/Sandbox/Definitions/MyWeaponBlockDefinition.cs`, `MyWeaponDefinition.cs`, `Game/Weapons/MyGunBase.cs` `CreateAmmoInventoryConstraints` | Confirmed. `MyLargeTurretBaseDefinition` derives from `MyWeaponBlockDefinition`, so turrets are covered |
| `MyReactorDefinition.FuelInfos[].FuelId` | `Sandbox.Game/Sandbox/Definitions/MyReactorDefinition.cs`, also `InventoryConstraint` built from it | Confirmed |
| `MyProductionBlockDefinition.BlueprintClasses`, `InputInventoryConstraint`, `OutputInventoryConstraint` | `Sandbox.Game/Sandbox/Definitions/MyProductionBlockDefinition.cs`; `MyRefinery` assigns them to its inventories | Confirmed |
| `MyBlueprintDefinitionBase` exposes prerequisites, results, priority, production time | Fields `Prerequisites`, `Results`, `Priority`, `BaseProductionTimeInSeconds`, `IsPrimary`, `Atomic` | Confirmed. `IsPrimary` is the field to use for "prefer a primary blueprint" |
| Canonical result-to-blueprint mapping exists | `MyDefinitionManager.TryGetBlueprintDefinitionByResultId`, used by the terminal's own "add to production" path | Confirmed |
| Stackability follows the game | `MyObjectBuilder_PhysicalObject.CanStack`, overridden by `MyObjectBuilder_OxygenContainerObject` (compares gas level), `MyObjectBuilder_Datapad`, `MyObjectBuilder_Package` | Confirmed |
| `CanItemsBeAdded`, `ComputeAmountThatFits`, `CheckConstraint`, send/receive flags | All present on `MyInventory`; `MyInventoryFlags.CanSend/CanReceive` | Confirmed |
| Mechanical group as scope | `VRage.Game/VRage/Game/ModAPI/GridLinkTypeEnum.cs`: `Mechanical` = rotor, piston, suspension; `Logical` includes mechanical plus connectors | Confirmed. Note wheel suspensions join the mechanical group |
| Mod message transport for the optional companion | `Sandbox.Game/Sandbox/ModAPI/MyModAPIHelper.cs`: `SendMessageToServer`, `RegisterSecureMessageHandler` route through vanilla static events; unknown channel ids are dropped silently | Confirmed, see Companion notes |

## Findings that change the design

### 1. Zombie inventory: `MyInventory.AddItems` is a no-op on clients

`MyInventory.AddItems(amount, objectBuilder)` (MyInventory.cs, around line 950 to 1000) computes fit and returns `true`, but only calls `AddItemsInternal` when `Sync.IsServer`. `AddItemsInternal`, `SwapItems` and `RemoveItems` internals are private. So a synthetic `MyInventory` built with the public API stays empty on a multiplayer client.

The client population paths that do exist are the replication entry points: `AddItemClient(position, item)`, `ChangeItemClient(item, position)`, `UpdateItemAmoutClient(itemId, amount, gas)`, `RemoveItemClient(itemId)`, `Refresh()`, and `Init(MyObjectBuilder_Inventory)` which fills items during `OnAddedToContainer`. They are public and usable, but they are designed for the replication layer, fire `ContentsChanged`, and assume the inventory belongs to a real entity in a component container.

Also relevant: `MyGuiControlInventoryOwner` and `MyTerminalInventoryController` do not treat inventories abstractly. The controller casts `grid.UserData` to `MyInventory` and `item.UserData` to `MyPhysicalInventoryItem`, resolves conveyor endpoints from `InventoryOwner`, and calls `TransferByUser` directly in `TransferToOppositeFirst`, `CanTransferItem`, drag handlers and the gamepad path. Feeding it a fake owner means every one of those handlers must be replaced, not wrapped.

Recommendation: decide explicitly between two options and record it in the plan.

- Option A (recommended): write an own owner control that reuses `MyGuiControlGrid` with the `Inventory` visual style and the vanilla item-icon builder, with the plugin's own row object as `UserData`. Keep Keen's `MyGuiControlInventoryOwner` for the character pane and the vanilla fallback. No fake `MyEntity`, no fake `MyInventory`, no risk of a synthetic inventory leaking into `MyInventory.OnTransferByUser` subscribers or into save/replication code.
- Option B: keep the zombie owner but populate it only through `AddItemClient` / `RemoveItemClient` / `Refresh`, never through `AddItems`, and never add the owner entity to `MyEntities`. Document that `MyEntity.InventoryCount` resolves through a `MyInventoryBase` component (for several inventories an aggregate), so the zombie needs a real component container.

Either way, the sentence "one synthetic `MyInventory` view per rendered section" should stop implying that the public add API works on clients.

### 2. Vanilla server validates ownership and proximity, not conveyor paths

`InventoryTransferItem_Implementation` does `EntityExists`, `CheckCanAddItems(destinationOwnerId)`, non-negative amount, then `TransferItemsInternal`. There is no `Reachable` or `ComputeCanTransfer` call on the server side of user transfers. What is validated:

- Source: `MyInventoryReplicable.HasRights` → `MyReplicableRightsValidator.HasRights(MyCubeBlock)`. Ownership passes for `Owner`, `FactionShare`, `NoOwnership` relations (or the remote admin "use terminals" flag). Access requires the sender's character to exist and to be within `3 × DEFAULT_INTERACTIVE_DISTANCE` of any grid AABB in the source's **logical** group, or that grid to be in the character's replication dependencies.
- Destination: `CheckCanAddItems` applies the same Access + Ownership check to the destination entity's replicable, rejects if the two block owners are more than 2 km apart while in the same logical group, and **skips all destination checks for admins**.

Consequences for the plan:

- The sentence "Relies on Keen's existing server handlers to validate and synchronize mutations" is only true for ownership and proximity. Conveyor connectivity, sorter rules and tube size are enforced only by the vanilla client UI. The plan already says the plugin reproduces those checks; upgrade that from "reproduce" to a stated invariant: no request leaves the client unless the same reachability test the vanilla terminal runs has passed. Otherwise the plugin becomes a conveyor-bypass tool and server operators will treat it as a cheat.
- The vanilla terminal runs two checks per transfer and both must be reproduced: `MyGridConveyorSystem.AppendReachableEndpoints(srcEndpoint, playerId, list, itemId, predicate)` (respects access, sorters, and large-tube requirement via `NeedsLargeTube`) followed by the plain `Reachable(from, to)` (`MyTerminalInventoryController.CanTransferItem`).
- The character's conveyor endpoint is the **interacted block** (`GetConveyorEndpoint(fromUser, ...)` returns `m_interactedAsOwner`). When a pane resolves to a different mechanical group than the one the terminal was opened on, withdrawals into the character must still be tested from the source block to the interacted block, exactly as vanilla does.
- A failed validation is not answered. `MyMultiplayerServerBase.ValidationFailed` logs the record into the server's failed-validation list and drops the event. There is no kick in this build, but every drop is visible to admins. Every "wait for replicated state" step therefore needs a timeout and a stop-on-repeated-failure rule, and automatic loops must re-check ownership and proximity immediately before each request so they do not fill the server log.

### 3. Refill Bottles needs the refill request, not just staging

`MyGasTank` and `MyGasGenerator` fill bottles only in two situations: the block's `AutoRefill` sync value is on (checked on `ContentsChanged` and on the 100-frame update), or a client sends the refill request behind the terminal's Refill button (`MyGasTank.SendRefillRequest` → `OnRefillCallback`, `[Server(Access | Ownership)]`; `MyGasGenerator.SendRefillRequest` is public, the tank's is private). Staging a bottle into a filler with Auto-Refill off and waiting for "replicated bottle state to show full" waits forever.

Additional constraints from the code:

- `MyGasTank.CanRefill` requires electricity, `FilledRatio > 0` and `CanStore`; `MyGasGenerator.CanRefill` requires ice (or creative) and `CanProduce`.
- The refill loops in both blocks pattern-match `MyObjectBuilder_GasContainerObject { GasLevel: 0 }`, while `CanRefill` tests `GasLevel < 1`. Whether partially filled bottles are refilled at all must be verified in-game before the plan's trigger ("a partially filled compatible bottle") is finalized. If only empty bottles refill, the action should target empty bottles and treat partials as "cannot refill" rather than "stranded".
- Bottles with identical gas level stack (`CanStack` compares `GasLevel` and `OxygenLevel`), so "one stateful bottle at a time" is really one stack at a time. A single refill request can top up a whole stack from one filler's stored gas.
- The generator's single input inventory carries both ice and bottles. `MyGasGenerator` splits its constraint into `m_oreConstraint` and `m_containersConstraint`. The descriptor model should allow two roles in one inventory index for this block family; today it keys roles by inventory index only.

Recommendation: after staging, send the same request the Refill button sends (reflection or a Harmony reverse patch for the tank's private method, direct call for the generator), then wait for the bottle stack's `GasLevel` to change or time out. Do not toggle Auto-Refill.

### 4. Refinery sorting works by swaps

`TransferItemsInternal` with `src == dst`: when `destItemIndex` is valid and the item there cannot stack with the moved one, it calls `SwapItems(srcIndex, dstIndex)`. When the items can stack, they merge at the destination slot. There is no insert-and-shift.

So "for each misplaced position, send one `TransferByUser(input, input, itemId, destinationIndex)`" is right only if the planner models the result as a swap. Plan it as a selection sort: at most `n-1` swaps, one in flight, re-read replicated order after each. The plan's "one reorder request per refinery at a time" already fits; add the swap model to the text so the implementer does not expect list-insert semantics.

Two more refinery facts worth writing down:

- `RebuildQueue` runs only on the server and only when `m_queueNeedsRebuild` is set by `ContentsChanged`, so a reorder immediately causes a queue rebuild. Good.
- Refineries with "use conveyor system" enabled pull ore themselves (`MyRefinery` calls `ConveyorSystem.PullItems(inputInventory.Constraint, ...)`), and pulled ore lands at the end of the input. This is the concrete reason the debounce exists; mention it so the debounce is not removed as "unnecessary".

## Important refinements

### Scope discovery should reuse the terminal's own mechanical list

The vanilla inventory tab already has a "Ship" filter (`LeftFilterTypeIndex == 2`, `MyGuiControlRadioButtonStyleEnum.FilterGrid`) that renders `m_interactedGridOwnersMechanical`; the default list (`m_interactedGridOwners`) is built from `MyCubeGridGroups.Static.Logical`, which includes connector-docked grids. The per-grid enumeration is `MyGridConveyorSystem.GetGridInventories(interactedEntity, list, identityId)`, which applies `HasPlayerAccess(identityId)` and `MyTerminalBlock.ShowInInventory` (except for the interacted block itself). That is exactly the plan's "all accessible inventories normally shown in the terminal".

Recommendation: enumerate `MyCubeGridGroups.Static.Mechanical.GetGroup(grid).Nodes`, call `GetGridInventories` per grid, and subscribe to each grid's `ConveyorSystem.BlockAdded/BlockRemoved` as the controller does for `m_registeredConveyorMechanicalSystems`. This gives the split/merge/dock behaviour for free and avoids a second, subtly different notion of "accessible".

### Component targets: acknowledge by content, not by event

- `OnAddQueueItemSuccess` is broadcast even when the server-side `InsertQueueItem` inserted nothing (blueprint not usable, or queue already at `MySession.Static.MaxProductionQueueLength`). Waiting for "the replicated queue change" must compare queue contents, not just wait for the event.
- Inserting at index `-1` merges into the last queue item when it has the same blueprint and reuses that item's `ItemId`. Do not track "my batch" by queue item id; track it by blueprint and amount delta.
- Respect `MaxProductionQueueLength` in the planner; it is a world setting.
- The terminal's own production helper (`MyTerminalInventoryController`, around line 850) filters assemblers by `Mode != Disassembly && UseConveyorSystem && !CooperativeMode` and uses `TryGetBlueprintDefinitionByResultId`. Adopt the same eligibility. Cooperative (slave) assemblers receive queue items from a master (`GetMasterAssembler`) and should never be direct targets.
- Estimated queued time per assembler: `Blueprint.BaseProductionTimeInSeconds / (MySession.Static.AssemblerSpeedMultiplier * (AssemblySpeed + UpgradeValues["Productivity"]))`, from `MyAssembler.CalculateBlueprintProductionTime`. This is the formula for "least estimated queued base-production time".
- `MyAssembler.CurrentState` (`Ok, Disabled, NotWorking, NotEnoughPower, MissingItems, InventoryFull`) is replicated from the server. Use it for the status column and to avoid queueing into machines that are `InventoryFull` or `MissingItems` for that blueprint.

### Drain Idle Assemblers

`IsProducing` is a replicated sync value and `IsQueueEmpty` is local, so the eligibility check is cheap and accurate on the client. In disassembly mode, components sit in the **output** inventory and the assembler pulls them there; keep disassembly assemblers out of the drain even when their queue is empty, otherwise the drain fights the player's disassembly setup.

### Vanilla filter mapping

The plan keeps the Storage / Energy / System / All buttons but bans `InventoryOwnerType()` for grouping. Those buttons are implemented by comparing `owner.InventoryOwnerType()` to the filter enum. Derive section visibility from the plugin's own section semantics and only fall back to `InventoryOwnerType()` for unknown sections. That keeps the safety argument (unknowns never become cargo) while matching player expectations for the filter buttons.

### Reachability cost on large grids

The vanilla terminal runs `AppendReachableEndpoints` once per drag. Rebalance across N items and M members, and the automatic maintain loops, will run it far more often. The conveyor pathfinder takes a global lock (`lock (Pathfinding)`). Add a per-operation cache keyed by (source endpoint, item definition) and cap the number of reachability queries per frame, like the transfer queue caps mutations. Definition caches alone will not keep the terminal responsive on a 500-container station.

### Terminal integration point

`MyGuiScreenTerminal` creates `MyTerminalInventoryController` (an `internal` class) in `CreateInventoryPageControls`, calls `Init(tabSubControl, m_user, InteractedEntity, m_colorHelper, this)` and `Refresh()` when the page is selected. The plan should name the patch strategy: prefix-and-skip `Init`/`Refresh` and mount the plugin's controller on the same tab page, with the **Unified** toggle switching between the two controllers. Gamepad support in the vanilla controller is substantial (`MyGamepadTransferCollection`, `grid_ItemControllerAction`, per-owner help text); step 14 will be larger than it looks.

## Minor comments

- "Prefer larger stacks initially to reduce the number of game transfer calls": fine, but the server's `FixTransferAmount` clamps to `ComputeAmountThatFits` on constrained destinations and spawns nothing for user transfers, so the partial-result path is exactly as described.
- `TransferByUser` silently returns when `dst.CheckConstraint` fails on the client. Treat a locally rejected allocation as a planner bug, log it, and continue with the next allocation rather than waiting for a reply.
- The 2 km rule in `CheckCanAddItems` only applies inside one logical group, so it does not affect unified-to-unified transfers between separate mechanical groups joined by a connector (they are in one logical group but the AABB proximity is also what makes the transfer possible). No change needed; noted for completeness.
- `MyInventory.OnTransferByUser` is a public static event other plugins may subscribe to; the plugin should call `TransferByUser` rather than raising the event itself so those subscribers keep working.
- Local profile key "server/world identity": specify it. `MySession.Static.Name` is mutable; prefer the world's checkpoint session id or, in multiplayer, the server's Steam id plus world name.
- Manual / Reserved / Not-a-destination flags are stored by block entity id plus inventory index. Entity ids survive save/load but not blueprint paste or projector rebuild; the plan already treats a missing anchor as a new profile, so this is consistent.
- "Hundreds of per-block owner panels are replaced by one" is the main performance win and should be measured early: the vanilla `MyGuiControlInventoryOwner` subscribes to every inventory's `ContentsChanged` and re-lays out the whole list on each change, which is what makes big-grid terminals slow.

## Suggested changes to the implementation order

- Move the decision from finding 1 (own owner control versus zombie inventory) into step 2, before any drag-and-drop work.
- Add a step between 8 and 9: "Implement and test the vanilla reachability pair (`AppendReachableEndpoints` + `Reachable`) with the interacted block as the character's proxy." Every later step depends on it.
- Step 16 should include sending the refill request and the in-game test of whether partially filled bottles refill.

## Companion notes for this plan

The optional companion (SERVER_COMPANION_PLAN.md) is reviewed separately. Two points matter here so the client stays independent:

- The capability handshake should use the mod message channel (`MyAPIGateway.Multiplayer` / `MyModAPIHelper.MyMultiplayer`). Unknown channel ids are silently dropped by an unmodified server, which gives the "no companion" fallback for free. Registering plugin-defined `[Event]` methods would require identical event tables on both sides and breaks when only the client has the plugin.
- The client plan's "wait for replicated state, then continue" rule already handles the companion's batched path: on a companion result, refresh from replicated inventories as usual rather than trusting the reported numbers.
