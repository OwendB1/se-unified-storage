# Unified Ship Storage Client Plugin Plan

This document covers only the Pulsar client plugin operating against an unmodified Space Engineers server. A possible future server-side extension is outside this implementation scope and is documented separately in [SERVER_COMPANION_PLAN.md](SERVER_COMPANION_PLAN.md).

## Goal

Give every ship one virtual cargo inventory that combines items scattered across its real inventories. This removes the need for inventory-management scripts and makes distributed storage practical without changing where items physically exist.

## Architectural boundary

ISY's Inventory Manager is a source of useful behaviours, not an implementation base. A programmable block runs its inventory and production logic through a server-synchronized block. This plugin must instead remain fully functional as a client-only Pulsar plugin against an unmodified server, including official-style servers.

Consequently, the client may read replicated definitions and inventory state, build local projections and plans, and send only the same access- and ownership-validated requests that the vanilla client can send. It must not call server-only mutation methods, assume a programmable block exists, require block-name tags, or depend on the optional companion. Shared persistence and unattended server execution are separate augmentations in [SERVER_COMPANION_PLAN.md](SERVER_COMPANION_PLAN.md).

## First-pass scope

- One virtual cargo per mechanical grid group: the main grid plus rotors, pistons, hinges, and attached subgrids.
- Do not merge a docked ship with a station merely because connectors are locked. They remain distinct unified cargo scopes, and the user can transfer between them when the game permits it.
- Read all accessible inventories normally shown in the terminal.
- Use regular cargo-capable inventories as automatic unified-cargo deposit targets. Constrained functional inventories appear in their own block-type sections, not as general storage.
- Provide UI-managed block and inventory exclusions without requiring block-name or custom-data tags.
- Derive refinery ore priorities from loaded refinery blueprints, show refinery inputs in that order, and optionally keep each accessed refinery's real input stacks in the same order.
- Provide component stock targets and add missing production to compatible assemblers without clearing or taking ownership of existing queues.
- Preserve all physical inventories and vanilla destruction and raid behaviour.

Separate conveyor networks, sorter rules, player-defined block groups, and custom semantics for mods that expose neither definitions nor useful inventory constraints come later.

After that first pass is stable, Phase 2 may add generic machine loadout targets plus explicit **Refill Bottles** and **Drain Idle Assemblers** actions. They are extensions of the same projection and transfer planner, not prerequisites for Unified Cargo.

## Client UI

A Pulsar client plugin replaces the existing grid-side inventory panels and UI with the unified view by default. It also adds a **Unified** toggle that lets the player return to the original vanilla panels as a just-in-case fallback if the unified UI breaks or behaves incorrectly.

Each inventory pane resolves its selected block to a mechanical grid group and displays that group's unified cargo. The left and right panes may therefore show two different unified cargo scopes, such as a docked miner and its station.

In the unified view:

- Hundreds of per-block owner panels are replaced by one stock-looking owner panel containing several inventory-grid sections, matching the way vanilla renders multiple inventories inside one owner.
- The first section is **Unified Cargo**, containing general-purpose storage.
- Later sections represent block types such as **Weapons**, **Power Producers**, **Refineries**, **Assemblers**, and **Ship Tools**. Only sections present in the current mechanical grid group are rendered.
- Production types may have separate role grids under one type header, such as **Refinery Input** and **Refinery Output**, matching the visual separation in the reference UI.
- Each type header displays its name and member count and has a **Rebalance** button. A pane-wide policy selector shows the policy that every button will use.
- The vanilla **Storage**, **Energy**, **System**, and **All** filters control which sections are visible; they do not change section membership.
- Each real inventory belongs to exactly one section and role in a given view, so totals are never duplicated.
- Items are combined whenever the game itself considers their contents stackable; otherwise, they remain separate entries.
- Mass, volume, search, amount dialogs, clicking, and drag-and-drop retain the normal Keen appearance where practical.

Each type section shows only items relevant to that type. Weapons show compatible ammunition, fueled power producers show their fuels, refinery input shows valid process inputs, and refinery output shows valid process results. If matching blocks exist but their relevant inventories are empty, keep an empty inventory row with the appropriate constraint icon so it remains a usable drop target.

Internally, the client uses one local-only "zombie" owner per pane scope with one synthetic `MyInventory` view per rendered section and role. It exists only to feed the stock GUI and is never registered, saved, replicated, or treated as real storage. Its GUI items retain mappings to the contributing real inventory stacks; transfers are intercepted before the game can treat a zombie inventory as an endpoint.

The first implementation should defer additional stack-grouping optimizations. Keen's own stackability result is the general rule and source of truth; special cases should be introduced only when testing demonstrates that they are necessary.

### Refinery controls

The **Refineries** section keeps its **Rebalance** button and separate Input and Output grids. The Input grid adds a compact ore-priority control with:

- An **Automatic priority** mode, which places explicitly pinned ores first and orders every other processable ore by current output scarcity.
- A **Manual priority** mode, in which the player orders the discovered ores directly.
- An **Auto-sort inputs** toggle and a **Sort now** action. Auto-sort is enabled by default for refineries participating in the unified scope, with per-machine exclusions for manual use; Sort now remains available after auto-sort is disabled.

The Refinery Input rows are rendered in the effective priority order rather than alphabetically or in the arbitrary order in which their contributing stacks were scanned. The same effective order is filtered for each real refinery's capabilities and, when sorting is enabled, applied to that refinery's physical input inventory. A row can show how many of the section's refineries accept that ore so a mixed Basic Refinery, Refinery, and modded-refinery section remains understandable.

Priority sorting and **Rebalance** are different operations. Sorting changes stack order inside each refinery and therefore its processing order; it does not move ore between refineries. Rebalance redistributes ore between refinery inputs using the selected placement policy and then schedules a priority-sort pass for the inventories it changed.

### Component target controls

The **Assemblers** section adds a **Component Targets** area above its real Input and Output grids. This is a stock-looking production control, not another synthetic inventory and not a drag-and-drop endpoint. Each component row contains:

```text
component icon and name | in stock | already queued | target | remaining/status
```

The list is built from loaded component and assembler-blueprint definitions, so craftable modded components appear without a hard-coded name list. Search filters the list. A zero or blank target disables that row without deleting its discovered definition. Rows with no usable blueprint or no accessible compatible assembler remain visible with a clear status instead of silently disappearing.

The header provides **Craft deficits**, an opt-in **Maintain targets** toggle, and a configurable start threshold. For example, a target of `10,000` with a `95%` threshold starts another batch below `9,500` and queues enough to return to `10,000`. This adopts ISY's useful margin concept without reproducing its LCD parser.

The existing **Rebalance** button still applies only to the Assemblers Input and Output inventories. It never edits production queues. Component-target actions have their own controls because queueing production and redistributing inventory are materially different commands.

### Management exclusions

Each block-type header has a small **Manage members** action that lists the real blocks and inventory roles contributing to that section. It replaces ISY's `[Locked]`, `[Hidden]`, and `!manual` name tags with explicit settings:

- **Manual block:** every automatic or bulk plugin action—including sorting, target maintenance, loadouts, refill, cleanup, and section-wide Rebalance—skips every inventory on that block. Direct user drag-and-drop remains available.
- **Reserved inventory:** the inventory remains visible, but its contents do not satisfy component or future loadout totals and no virtual, automatic, or bulk planner selects it as a source or destination. Show the reserved portion in the aggregated row's details and a **Reserved / not counted** badge so displayed and usable totals cannot be confused. The player can still manipulate it through the vanilla fallback or an explicitly selected concrete inventory.
- **Not a Unified Cargo destination:** a general-purpose cargo inventory remains visible and withdrawable but is not selected for automatic deposits or redistribution.

These switches are independent because protecting a manually operated assembler, protecting emergency stock, and preventing deposits into a particular cargo container are different intentions. Defaults leave every otherwise eligible inventory managed and counted. Store identities by block entity ID plus inventory index; never mutate block names or custom data.

## Logical inventory-owner discovery

Do not build the unified UI around the obsolete `MyInventoryOwnerTypeEnum` or `InventoryOwnerType()` result. Keen's fallback classifies unknown entities as `Storage`, which would make an unfamiliar modded weapon or machine look like a safe cargo destination.

Instead, describe every real inventory before rendering it:

```text
InventoryDescriptor
    owner entity ID
    block definition ID
    inventory index
    block-type section and inventory role
    accepted-item constraint signature
    discovery provider
```

The resolver order is:

1. Known vanilla definition and runtime families, which also cover mods using those object builders.
2. The live inventory's whitelist or blacklist constraint and send/receive flags.
3. A safe unknown-definition fallback.

Known inventories join a semantic block-type section such as Weapons, Power Producers, or Refineries. Their descriptors retain exact block definition, inventory index, role, and constraint information even when several definitions share a section. The section can therefore show the union of relevant items while calculating valid destinations separately for each item.

Unknown inventories use the exact block definition ID, inventory index, role, and constraint signature as their section key. This groups identical modded block definitions automatically without maintaining a subtype-name list. Two definitions with the same display name do not merge accidentally, and input/output inventories on one production block remain distinct. If two instances of one unknown definition expose different live constraints, split them rather than pretending they are interchangeable.

The deliberate exception is general-purpose cargo: compatible unconstrained cargo containers may share the single unified cargo group across block definitions. An unknown or constrained inventory must never enter that group merely because no specialized adapter recognized it.

Example grouping:

```text
Unified ship owner
  Unified Cargo
  Weapons × 11
    Ammunition
  Power Producers × 6
    Fuel
  Refineries × 3
    Input
    Output
  Unknown Mod Machine × 2
    Inventory 1
```

The unknown machine is still usable: its instances are grouped by exact definition, inventory slot, and constraint signature. Its section can be rebalanced using the live constraint, but it receives no broader semantic grouping until the client can prove its role.

## Consumable discovery

Definition metadata determines why an inventory exists and which item types should be offered for specialized distribution. Live inventory checks remain authoritative at transfer time.

### Vanilla and definition-compatible mods

- **Weapons:** for a `MyWeaponBlockDefinition`, resolve `WeaponDefinitionId` through `MyDefinitionManager` and read `MyWeaponDefinition.AmmoMagazinesId`. This discovers vanilla and modded conventional ammunition without checking subtype strings. `MyGunBase` builds the live ammo inventory constraint from the same list.
- **Reactors:** for a `MyReactorDefinition`, read every `FuelInfos[].FuelId`. This supports vanilla uranium and modded reactor fuels, including definitions with more than one required fuel. The reactor definition also builds its live whitelist from these IDs.
- **Refineries and assemblers:** use `MyProductionBlockDefinition.InputInventoryConstraint` and `OutputInventoryConstraint`, which Keen derives from the loaded blueprint classes' prerequisites and results. Preserve input and output as different roles even though they share a type header.
- **Other constrained systems:** use the same provider pattern for production inputs/outputs, gas generators, parachutes, tools, and future block families. Until a semantic provider exists, the generic constraint-based group remains safe and usable.

Never infer compatibility from an item already being present: an empty weapon or reactor still has a valid definition. Before moving anything, recheck the target's current constraint, `CanItemsBeAdded`, capacity, access, and conveyor path.

Example: a modded reactor whose loaded `FuelInfos` contains only `ThoriumIngot` appears under Energy with that fuel. Dropping uranium on the group produces no candidates. No special knowledge of the mod or its subtype name is required.

## Refinery ore-priority engine

Do not carry ISY's vanilla ore-name and static-yield tables into the plugin. For every refinery definition present in the current scope, enumerate its `MyProductionBlockDefinition.BlueprintClasses`. Each `MyBlueprintDefinitionBase` supplies the real prerequisite IDs, result IDs, amounts, production time, and priority loaded by the current game and mods. Keep only recipes usable by at least one live refinery in the section.

This produces a cached definition model:

```text
RefineryRecipe
    blueprint definition ID
    prerequisite item IDs and amounts
    result item IDs and amounts
    compatible refinery definition IDs
```

An input item enters the ore-priority list when it is a prerequisite of one of those recipes and a live refinery input accepts it. This naturally discovers modded ores, modded refinery outputs, multi-output recipes such as stone processing, and specialist refineries without subtype-name checks.

In **Automatic priority** mode, calculate one deterministic scope-wide order:

1. Valid pinned definition IDs appear first in the player's explicit order.
2. For every unpinned input, calculate the current amount of each recipe result in the ship scope and normalize it by that recipe's `result amount / prerequisite amount` yield.
3. Use the lowest normalized result stock as the input's scarcity score; lower scores refine first. A multi-output recipe therefore rises when any of its useful outputs is scarce.
4. Break ties by blueprint priority, localized display name, and finally full definition ID so the UI never flickers between equal scores.

In **Manual priority** mode, the stored definition-ID order is authoritative, with newly discovered inputs appended deterministically. Missing mod definitions remain stored but inactive so temporarily removing a mod does not destroy the player's configuration.

The UI displays the scope-wide order, while each real refinery receives only the subsequence it can process. Thus a Basic Refinery and a full Refinery can share one section without the client trying to put uranium or platinum into the Basic Refinery.

Example: Gold is pinned first and live normalized output stocks make Nickel scarcer than Cobalt, which is scarcer than Iron. The section displays `Gold, Nickel, Cobalt, Iron`. A full Refinery containing all four inputs is sorted to that order; a specialist refinery that accepts only Nickel and Iron is sorted to `Nickel, Iron`. Unsupported entries are filtered, not treated as transfer failures.

The physical sorter works only on a refinery's own input inventory. It compares the current stack sequence with the desired item-type sequence and, for each misplaced position, sends an ordinary `MyInventory.TransferByUser(input, input, itemId, destinationIndex)` request. Keen's server handler validates access and ownership and swaps or stacks the real items; the refinery then rebuilds its processing queue from the changed input order. The client must never call the server-only transfer implementation directly.

Run at most one reorder request per refinery at a time and wait for replicated inventory state before planning the next swap. Debounce content changes, skip inventories that already match, and cap work per update so conveyor pulls cannot create a request storm. Closing the terminal does not cancel an already queued bounded pass, but client-only automatic sorting exists only while that client is connected and the plugin is active.

## Component-target engine

Component targets use definition data rather than ISY-style recipe learning. Enumerate loaded blueprints, index every result whose type is `MyObjectBuilder_Component`, and retain candidates usable by at least one accessible assembler in the scope. Prefer Keen's canonical result-to-blueprint mapping when it is usable; otherwise prefer a primary, single-result blueprint. If several plausible modded recipes remain, show a blueprint picker and persist the player's choice instead of guessing from subtype names.

For each target component, derive:

```text
stock  = component amount in every accessible non-Reserved in-scope inventory, counted once
queued = sum of the component produced by all remaining assembly queue items
deficit = max(0, target - stock - queued)
```

Queue accounting uses each blueprint's actual result amount, including multi-result modded recipes. Co-products update the queued totals of their own component rows as well. Components and their targets are integral even though the game represents amounts with `MyFixedPoint`.

When **Craft deficits** is clicked, or **Maintain targets** observes that stock is below the configured threshold, convert the remaining deficit into whole blueprint runs and append those runs to accessible assembly-mode assemblers that report `CanUseBlueprint`. Prefer the eligible assembler with the least estimated queued base-production time, then recalculate after every accepted batch. Send `InsertQueueItemRequest` and wait for the replicated queue change before issuing more work.

Example: the Steel Plate target is `10,000`, accessible inventories contain `7,200`, and existing assembler queues will produce `800`. The remaining deficit is `2,000`, so the client appends only the blueprint runs needed for those `2,000` plates. If stock is `9,200` and `800` are already queued, it appends nothing even though the on-hand value alone is below a `95%` start threshold.

The first implementation is deliberately add-only:

- Never clear, move, shorten, or change the mode of an existing assembler queue.
- Never toggle cooperative, repeating, conveyor, or power settings.
- Skip disassembly-mode assemblers and allow per-block exclusions for machines the player is using manually.
- Do not implement automatic disassembly merely because a target was lowered.
- Keep one target batch in flight per scope and include all existing queue entries in the next deficit calculation.

These rules avoid claiming ownership of production work that may have been added by the player, another client, or another script. Two independent clients can still race after reading the same replicated deficit; the client-only implementation mitigates this with debouncing and one in-flight batch but cannot provide a distributed lock.

## Client-local settings and lifetime

The client-only plugin persists refinery and production intent in its own local configuration, keyed by server/world identity and a stable anchor grid entity ID selected for the mechanical group. The profile stores only intent:

```text
refinery mode, auto-sort toggle, pinned/manual ore definition IDs
component targets, maintain toggle, start threshold, blueprint overrides
block/inventory management exclusions
Phase 2 machine loadout rules
```

Derived recipe maps, automatic ore order, live stock, queues, inventory contents, and capacities are rebuilt from the current session and are never persisted as truth. If the anchor no longer exists or a grid is copied into another world, create a new local profile rather than applying settings by matching a mutable display name.

Local profiles are private to that player. Automatic work runs only while the player is connected, the plugin is active, and vanilla request validation grants access. The UI should label this state **Local automation** so users do not mistake it for offline or faction-wide control. The optional shared and unattended model is specified only in the server companion plan.

## Phase 2 machine loadouts

Generalize ISY's special containers and its separate uranium and ice balancers into one definition-driven loadout system. A rule targets an exact block, a block definition, or a semantic section and one inventory role:

```text
LoadoutRule
    target selector and inventory role
    item definition ID
    target mode: amount per member or total across members
    target amount
    distribution policy for a section total
    maintain enabled
    include non-working blocks: false by default
```

Phase 2 adds a **Loadouts** action beside **Manage members** on each applicable section header. Its editor chooses the member scope, relevant inventory role and item, per-member or section-total mode, amount, distribution policy, non-working-block behavior, and whether to maintain continuously. The section shows current deficit or excess and offers **Apply loadouts** without exposing the stored rule as a fake inventory item.

Only offer items accepted by the target inventories' loaded definitions and live constraints. This gives modded weapons, reactors, generators, tools, and other constrained blocks automatic support without dedicated uranium, ice, or ammunition code paths.

For **amount per member**, evaluate every compatible, non-excluded inventory separately. For **total across members**, calculate one section deficit or excess and use the rule's stored distribution policy, initially copied from the pane selector. Missing stock is sourced only from Unified Cargo; excess is returned only to Unified Cargo. A loadout never steals from another loadout, silently drains a manually managed block, or toggles conveyor, enabled, stockpile, or power settings.

Example: a Weapons rule requests `10 NATO_25x184mm` magazines per compatible member. Gatling weapons receive them, missile launchers are excluded by their constraints, and definition-derived modded magazines are offered as separate rules for the weapons that accept them. If Unified Cargo lacks enough stock, perform the valid partial transfer and show the remaining deficit.

Use the existing snapshot, target accounting, candidate filtering, placement policies, transfer queue, and local profile storage. Client-only **Maintain** runs only while the plugin is active; a one-shot **Apply loadouts** action remains available when continuous maintenance is disabled.

## Explicit utility actions

### Refill bottles

Expose **Refill Bottles** in the Unified Cargo toolbar only when the scope contains a partially filled compatible bottle and a usable tank or generator. This starts a bounded job:

1. Resolve non-Reserved bottles, their gas type, and compatible non-Manual filling inventories from loaded definitions and live constraints.
2. Move each selected bottle through ordinary client-requested transfers to a reachable working filler.
3. Wait for replicated bottle state to show full, failure, or a no-progress timeout.
4. Return it to its original inventory when still valid; otherwise deposit it into Unified Cargo using the selected placement policy.

Report each bottle as filled, returned unfilled, or stranded at the filler. Do not add continuous bottle-filling automation; the explicit job is the entire client feature.

### Drain idle assemblers

Add **Drain Idle Assemblers** to the Assemblers section. At execution time, an eligible assembler must have an empty queue, not be producing, and not be marked Manual. Move contents from its non-Reserved input and output inventories into Unified Cargo through the normal destination planner. Recheck idle state before every assembler so a newly queued machine is skipped rather than drained.

The action never clears a queue, changes assembler mode, or promises that every item will fit. Report skipped machines and partial transfers. There is no automatic idle cleanup loop.

## Deferred refinery-filling gate

Do not schedule ISY-style script-assisted refinery filling yet. First test the accepted priority sorter with vanilla conveyor pulling. Add a separate opt-in replenishment design only if testing shows that reachable refineries regularly run dry or vanilla pulls repeatedly defeat the requested physical input order. Even then, it must reuse ordinary transfers and must not toggle refinery conveyors or evict lower-priority ore merely to make room.

## Per-type rebalance controls

Every rendered block-type header has a **Rebalance** button, including Unified Cargo. The button applies the pane's currently selected policy to every relevant item shown in that section. Capture the selected policy when the player clicks; changing the selector afterward must not alter an operation already in the transfer queue.

The operation is strictly section-local:

- It redistributes items already present in that section's real member inventories.
- It does not silently pull stock from Unified Cargo or another type section.
- Each item is planned independently and only against member inventories whose live constraints accept it.
- Multiple roles under one type are planned independently. A refinery input item stays among refinery input inventories, while output items stay among output inventories.
- Items hidden because they are unrelated to the section are neither displayed nor moved.

Policy meaning is unchanged:

- **Existing stack first:** consolidate each item into the members holding its largest existing stacks before opening new stacks elsewhere.
- **Fill containers first:** pack each item into the fullest suitable member inventories first.
- **Distribute equally:** equalize each item across all suitable member inventories in the section and role.

Plan item identities in a stable order against projected amounts and capacity updated after every planned allocation. The executor still rechecks live state. This prevents one section-wide click from promising the same free volume to several independently planned items.

Example: the Weapons section contains Gatling and missile-launcher inventories. Rebalancing `NATO_25x184mm` considers only weapons accepting that magazine; `Missile200mm` is planned separately against the launchers. A mixed section is safe because section membership determines what is displayed, while per-item live constraints determine where it may go.

Example: a Refineries header owns separate Input and Output grids. Its single **Rebalance** button may equalize ores among refinery inputs and consolidate produced ingots among refinery outputs under the selected policy, but it never moves an ingot into an input inventory merely because both grids belong to Refineries.

An empty section remains a drop target but has nothing to rebalance. Disable its button when the section has no relevant items or no displayed item has at least two suitable member inventories. Disable repeated clicks while that section's bounded transfer queue is running, then report complete or partial results and refresh the affected section.

## Transfer architecture

The virtual inventory is a projection, not storage. It never owns an item and is never an endpoint of a game transfer. Each displayed row points back to the real stacks that contributed to it, while the virtual destination represents a set of eligible real inventories. A source reference should identify the inventory owner, inventory index, and game item ID plus enough snapshot metadata to validate it again; it must never depend on a GUI row or mutable list index.

Every user operation therefore has two separate decisions:

1. **Source selection:** decide which real stack or stacks will supply the requested amount.
2. **Destination placement:** decide which real inventory or inventories should receive it.

Only inventories permitted by ownership/access rules and usable through the game's normal inventory-transfer checks are candidates. Conveyor connectivity, sorter rules, inventory constraints, and available volume remain authoritative; the unified view does not bypass them.

```text
Real inventories -> cargo snapshot -> aggregated rows -> unified GUI
       ^                                            |
       |                                            v
       +---- native inventory transfers <- transfer plan
```

### Unified cargo to a real inventory (withdrawal)

The user drags an aggregated row from unified cargo to a concrete destination such as their character, a refinery, or a particular cargo container. The source selector expands that row into its contributing physical stacks and chooses enough reachable stacks to satisfy the requested amount. Prefer larger stacks initially to reduce the number of game transfer calls.

Example: the GUI shows one `5,300 Steel Plate` row backed by these stacks:

```text
Cargo A:   600
Cargo B:   800
Cargo C: 3,900
```

Dragging `1,000` plates to the character inventory normally produces one allocation: `Cargo C -> Character: 1,000`. If only `400` can be transferred from Cargo C, the planner may continue with `Cargo B -> Character: 600`. The GUI does not manufacture a 1,000-item stack; it executes one or more ordinary transfers and then rebuilds the displayed total from real inventory state.

### Real inventory to unified cargo (deposit)

The user drags a real stack onto unified cargo. Here the source is already known, but the destination is not. The destination planner filters the ship's inventories for valid, reachable containers with enough compatible space, then applies the configured distribution policy.

Example: the player deposits `900 Steel Plate` and three eligible cargo containers exist:

```text
Cargo A: already contains   100 plates
Cargo B: already contains   400 plates
Cargo C: contains             0 plates
```

Possible plans include:

- **Existing stack first:** place items into Cargo B and Cargo A before opening a stack in Cargo C, subject to their available space.
- **Fill containers first:** if Cargo A can accept 300 and is currently the fullest candidate, then Cargo B can accept the remaining 600, allocate `A +300, B +600`.
- **Distribute this item equally:** target approximately equal final plate counts. With unlimited space, allocate `A +367, B +67, C +466`, producing totals of roughly `467` in each container. Fixed-point rounding and capacity constraints may shift the final unit.

These policies choose destinations only. They do not change what the game considers transferable or stackable.

### Stack identity

Aggregation follows the game's own stackability result. If the game would combine two stacks, the GUI shows one row whose source map can contain several physical stacks. If the game would keep them separate, the unified GUI keeps them separate too. This naturally covers tools, bottles, datapads, damaged items, and other stateful items without hard-coding a permanent item-type list. More specialized grouping can be considered later if profiling or usability testing justifies it.

Example: stacks of identical components spread across ten containers appear as one aggregated row. Two rifles with different durability remain two rows if the game reports that they cannot stack.

### Execution, stale state, and partial success

A plan is based on a snapshot and is therefore only a proposal. Immediately before each allocation, the executor rechecks that the source still contains the item, the destination still has capacity, access is still valid, and the game still permits the transfer. A player, conveyor sorter, production block, block removal, or another plugin may have changed any of these after the GUI was drawn.

Example: the user requests a transfer of `1,000` plates, but only `650` still fit when execution begins. The executor moves at most `650`, reports a partial result such as `650 / 1,000 moved: destination full`, and refreshes the view. It must never compensate by editing the synthetic inventory or inventing/removing item amounts.

Transfer allocations run through a small bounded queue so a large aggregate action does not issue hundreds of mutations in one frame. Real inventory change events are the source of truth for refreshing the virtual view; optimistic GUI changes must not be treated as committed state.

Each allocation is an ordinary client-requested game transfer, so a multi-stack operation is not atomic. If one allocation fails or transfers less than requested, the client rechecks the remaining plan against live inventory state and either continues with another valid allocation or reports the partial result.

### Unified cargo to unified cargo

Transferring between two distinct mechanical grid groups is supported by the client plugin. Each group remains a separate scope even when a connector or another valid conveyor path lets items move between them.

The operation combines the two existing planning stages:

1. Select physical source stacks only from the source group.
2. Select physical destination inventories only from the target group, using the chosen deposit policy.
3. Pair sources with destinations for which the game permits a transfer.
4. Issue ordinary `MyInventory.TransferByUser` requests for those real inventory pairs.

Pair reachability matters: a source and a destination can each be valid within their own scope without being mutually connected. The final plan must therefore contain concrete source-to-destination allocations rather than assuming that independently calculated source and destination totals can always be combined.

Example: the source miner and target station contain:

```text
Miner Cargo A:       700 steel plates
Miner Cargo B:       300 steel plates

Station Cargo A:     100 steel plates, space for 400 more
Station Cargo B:       0 steel plates, space for 800 more
```

Dragging `600 Steel Plate` from the miner's unified cargo to the station's unified cargo with `ExistingStackFirst` produces destination allocations of `Station Cargo A +400` and `Station Cargo B +200`. Largest-source-first can supply both from Miner Cargo A, producing these physical requests:

```text
Miner Cargo A -> Station Cargo A: 400
Miner Cargo A -> Station Cargo B: 200
```

If the connector disconnects, a sorter rejects steel plates, or only part of the route remains valid, the client follows the normal stale-state and partial-success rules. It transfers only the amount accepted by the game, refreshes both unified views, and reports any remainder.

Dragging between two panes that resolve to the same mechanical grid group is not a transfer and should do nothing. The **Rebalance** button on Unified Cargo or a block-type section is the explicit way to redistribute items within that scope. Future scopes such as separate conveyor components or block groups use the same cross-scope transfer pipeline.

## Deployment assumptions

- Works against an unmodified server, including official-style environments.
- Uses normal `MyInventory.TransferByUser` requests for every real transfer.
- Uses normal `MyProductionBlock.InsertQueueItemRequest` calls for component production and same-inventory `TransferByUser` requests for refinery input ordering.
- Relies on Keen's existing server handlers to validate and synchronize mutations; no plugin code or programmable block is required on the server.
- Reproduces vanilla access, capacity, and conveyor checks before issuing transfers.

## Client-side transfer backend

Keep policy planning independent from game mutation inside the client plugin:

```text
CargoSnapshot
    -> PlanDeposit(policy, item, amount, candidates)
    -> allocations
    -> TransferQueue
```

The planner operates on lightweight inventory snapshots and returns destination and amount allocations. The executor rechecks live state and performs the actual transfers.

An enum plus a switch is sufficient initially; no large strategy framework is needed.

## Initial distribution policies

### `ExistingStackFirst`

Prefer containers already holding the item, reducing fragmentation.

### `FillFirst`

Fill the fullest suitable container first, minimizing the number of containers touched.

### `EvenByItem`

Equalize each item type across eligible containers for raid resistance.

Only ores and ingots may be divided fractionally. Components, ammunition, bottles, and tools must use whole amounts even though Space Engineers represents every amount with `MyFixedPoint`.

Knapsack-style global repacking is deferred. It is useful only for an explicit whole-storage rebalance and is unnecessary for ordinary deposits.

## Correctness and safety

Before executing a transfer, validate or reproduce the relevant vanilla rules:

- Player access and ownership.
- Source and destination inventory identity.
- Membership in the selected ship scope.
- Conveyor reachability and direction.
- Sorter filters.
- Small- and large-tube compatibility.
- Inventory send and receive flags.
- Inventory constraints and remaining capacity.
- Definition-derived consumable compatibility.
- Integral item amounts.
- Manual, Reserved, and destination exclusions captured in the operation snapshot.
- Loadout source isolation: missing stock comes only from Unified Cargo and never from another managed loadout.
- Bottle compatibility and fill progress before returning a staged bottle.
- Empty queue and non-producing state immediately before draining each assembler.

Live state may change after a plan is created. The executor must therefore tolerate partial transfers, stale stacks, destroyed blocks, and full destinations, then refresh the cargo snapshot.

## ISY-derived scope decisions

The linked [ISY's Inventory Manager source](https://github.com/dorimanx/Isys-Inventory-Manager/blob/master/Script.cs) is a feature reference only. The review produced these final decisions beyond the accepted refinery-priority and component-target systems:

- Keep type-section balancing only through the existing **Rebalance** action and placement policies.
- Replace locked, hidden, and manual name tags with the UI-managed exclusions defined above.
- Generalize special containers, reactor uranium limits, generator ice limits, and ammunition stocking into the Phase 2 machine-loadout system.
- Keep bottle filling only as the explicit bounded **Refill Bottles** action.
- Keep assembler cleanup only as the explicit **Drain Idle Assemblers** action.
- Defer script-assisted refinery filling behind the testing gate above; ordinary ore balancing is already Rebalance.

The following ISY behaviours are rejected from the roadmap:

- Physical type-container routing, automatic container assignment, combined container categories, and block-name fill percentages.
- Blueprint teaching by observing assembler output; loaded definition discovery replaces it.
- Automatic disassembly, special-loadout stealing, existing assembler-queue reordering, and automatic idle-assembler cleanup.
- Continuous bottle filling, standalone uranium or ice balancers, and silently changing conveyor or offline-machine settings.
- Physical sorting of general cargo; only refinery inputs are reordered because their order affects production.
- LCD inventory, crafting, warning, and performance pages.
- Survival-kit stone crafting, ship/station modes, connector protection tags, and programmable-block collision handling.

Connectivity and partial-transfer failures remain normal unified-UI diagnostics, not block-name mutations or separate ISY compatibility features.

## Performance expectations

The plugin should reduce:

- Terminal GUI controls and rendering.
- Manual inventory searching.
- Inventory-script polling.
- Player effort when moving items among distributed inventories.

Definition compatibility maps and section membership are cached per session and invalidated when relevant blocks or scopes change. Inventory contents and capacity remain live data.

Refinery scarcity scores, component deficits, and Phase 2 loadout deficits are recalculated from dirty inventory or queue snapshots on a debounce, not by scanning every inventory every frame. Definition-to-recipe indexes are built once per loaded definition set. Sorting, transfers, assembler additions, bottle jobs, and drain jobs all share bounded request queues and wait for replicated state before continuing.

It does not remove Space Engineers' underlying conveyor graph. Hundreds of cargo containers still create hundreds of conveyor endpoints, and automated assemblers, refineries, and sorters continue using the vanilla conveyor system.

## Implementation order

1. Build inventory descriptors, safe definition-based grouping, and generic constraint fallback.
2. Replace the stock grid inventory panels with the read-only multi-section owner UI and provide the vanilla fallback toggle.
3. Aggregate items according to Keen's own stackability result, deferring additional stack optimizations.
4. Add vanilla weapon and reactor consumable providers, then cover other constrained systems as needed.
5. Add refinery and assembler input/output role sections.
6. Add **Manage members**, persist the three exclusion settings, and enforce them in every bulk or automatic operation.
7. Build definition-derived refinery recipe indexes and render the read-only automatic priority order, including modded and mixed-capability refineries.
8. Add virtual-to-real withdrawal and real-to-virtual deposits.
9. Add the pane policy selector, three placement policies, and a Rebalance button for every rendered type section.
10. Add bounded client-requested physical refinery sorting, then auto-sort with debouncing and Manual exclusions.
11. Add the Component Targets UI, stock and queued accounting, blueprint resolution, and local profile persistence.
12. Add manual **Craft deficits**, then opt-in maintain mode after queue acknowledgement and race handling are tested.
13. Add unified-to-unified transfers between distinct mechanical grid groups.
14. Complete mouse, amount-dialog, search, drag-and-drop, and gamepad support.
15. Add Phase 2 machine loadouts by reusing target accounting and the existing transfer planner.
16. Add the explicit **Refill Bottles** bounded job.
17. Add the explicit **Drain Idle Assemblers** bounded job.
18. Add block-group and conveyor-component scopes.
19. Consider knapsack-style packing only for a later explicit policy.
20. Add integration and performance testing for grid splits, docking, cross-group transfers, sorters, full containers, concurrent users, and destroyed blocks.

Definition-compatibility tests must cover vanilla weapons, conventional modded weapons, vanilla and modded reactor fuels, empty inventories, identical display names with different definition IDs, multi-inventory production blocks, and unknown constrained blocks. UI and rebalance tests must cover type-section filtering, empty sections, input/output isolation, per-item candidate filtering, policy capture, repeated-click suppression, projected capacity, and partial execution.

Refinery tests must cover the actual physical input order, pinned and automatic priorities, live scarcity changes, stone or other multi-output recipes, modded ores and outputs, multiple refinery capability sets, repeated content-change events, rejected same-inventory requests, and a rebalance followed by re-sort. Component-target tests must cover integral rounding, blueprint result amounts greater than one, co-products, ambiguous recipes, uncraftable modded components, existing manual queues, disassembly-mode and excluded assemblers, target reductions, in-flight replication delay, and two clients observing the same deficit.

Exclusion tests must prove that excluded inventories remain visible while every affected planner obeys their exact flags. Loadout tests must cover per-member and section-total targets, constrained and modded inventories, partial stock, excess returns, non-working blocks, and the prohibition on stealing from another loadout. Bottle and drain tests must cover disconnection, timeout, destroyed blocks, destination-full partial results, changed bottle state, and an assembler receiving a queue after the action was requested.
