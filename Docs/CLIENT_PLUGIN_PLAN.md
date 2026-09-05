# Unified Ship Storage Client Plugin Plan

This document covers only the Pulsar client plugin operating against an unmodified Space Engineers server. The optional server-side extension is outside this implementation scope and is documented separately in [SERVER_COMPANION_PLAN.md](SERVER_COMPANION_PLAN.md). Its initial discovery/shared-profile integration is tracked in [SERVER_COMPANION_IMPLEMENTATION.md](SERVER_COMPANION_IMPLEMENTATION.md); it does not change the standalone requirements below.

## Goal

Give every ship one virtual cargo inventory that combines items scattered across its real inventories. This removes the need for inventory-management scripts and makes distributed storage practical without changing where items physically exist.

## Architectural boundary

ISY's Inventory Manager is a source of useful behaviours, not an implementation base. A programmable block runs its inventory and production logic through a server-synchronized block. This plugin must instead remain fully functional as a client-only Pulsar plugin against an unmodified server, including official-style servers.

Consequently, the client may read replicated definitions and inventory state, build local projections and plans, and send only the same requests that the vanilla client can send. Keen's server handlers validate ownership, access, proximity, amounts, and destination constraints, but they do **not** validate the conveyor path for `TransferByUser`; matching the vanilla terminal's client-side reachability checks is therefore a hard security invariant of this plugin. It must not call server-only mutation methods, assume a programmable block exists, require block-name tags, or depend on the optional companion. Shared persistence and unattended server execution are separate augmentations in [SERVER_COMPANION_PLAN.md](SERVER_COMPANION_PLAN.md).

## First-pass scope

- One virtual cargo per mechanical grid group: the main grid plus rotors, pistons, hinges, suspensions, and attached subgrids.
- Do not merge a docked ship with a station merely because connectors are locked. They remain distinct unified cargo scopes, and the user can transfer between them when the game permits it.
- Read all accessible inventories normally shown in the terminal.
- Use regular cargo-capable inventories as automatic unified-cargo deposit targets. Constrained functional inventories appear in their own block-type sections, not as general storage.
- Provide UI-managed block and inventory exclusions without requiring block-name or custom-data tags.
- Derive refinery ore priorities from loaded refinery blueprints, show refinery inputs in that order, and optionally keep each accessed refinery's real input stacks in the same order.
- Provide component stock targets and add missing production to compatible assemblers without clearing or taking ownership of existing queues.
- Preserve all physical inventories and vanilla destruction and raid behaviour.

The implemented UI now exposes separate conveyor-component scopes and player-defined block groups. Custom semantics for mods that expose neither definitions nor useful inventory constraints remain deferred. Sorter direction, filters, and tube-size rules are always honored by transfers.

After that first pass is stable, Phase 2 may add generic machine loadout targets plus an explicit **Drain Idle Assemblers** action. They are extensions of the same projection and transfer planner, not prerequisites for Unified Cargo.

## Client UI

A Pulsar client plugin replaces the existing grid-side inventory panels and UI with the unified view by default. Each column has its own unlabeled, native-sized Unified icon toggle, always rightmost in the icon row, including when filters are hidden. Each toggle only changes that column's layout. In mixed mode, the off column renders individual native inventory-owner panels with the same scope/search controls; transfers work between projected and concrete inventories in either direction. Turning both toggles off restores the original vanilla controller as the safety fallback.

Each inventory pane resolves its selected block to a mechanical grid group and displays that group's unified cargo. The left and right panes may therefore show two different unified cargo scopes, such as a docked miner and its station.

When multiple mechanical constructs or conveyor networks are available, show an independent scope dropdown below each pane's search bar. Reserve a dedicated row by moving the inventory viewport down and shortening it, keeping its bottom edge fixed. Hide the row on character panes and remove it completely in vanilla fallback.

Each pane renders only its selected scope. Offer each **Whole construct**, its disconnected **Network N** entries (identified by a representative block), and terminal block-group views when that mode is configured. Order the accessed construct first and default to the network containing the accessed inventory hatch; otherwise default to that construct. Mark the accessed network and local construct explicitly. Distinguish duplicate ship names using deterministic ship numbers, with full names and construct entity IDs in tooltips. For the accessed network, prefer the hatch's own block name.

Only inventories whose live conveyor endpoint has actual model ports participate in network entries. Do not show portless lockers, flight seats or similar blocks as singleton networks merely because they have an inventory or implement the conveyor-endpoint interface. A disconnected block with real ports remains a valid singleton network. Portless inventories remain available through **Whole construct**; this filter changes network presentation, not inventory contents.

Keep selections by scope ID, independently per pane, through inventory refreshes, search/filter changes and character/grid switching. If the selected scope disappears, fall back to the accessed network/construct. Rescan network membership with the existing structural poll only for sessions that use network views; do not repeat graph traversal for every inventory-content change or per pane. Network grouping is a presentation aid, not an item-transfer guarantee. Two network entries must not contain the same physical inventory, even where opposing sorter branches converge. All concrete transfers still pass the existing vanilla-equivalent reachability gate.

In the unified view:

- Hundreds of per-block owner panels are replaced by one stock-looking owner panel containing several inventory-grid sections, matching the way vanilla renders multiple inventories inside one owner.
- The first section is **Unified Cargo**, containing general-purpose storage.
- Later sections represent block types such as **Weapons**, **Power Producers**, **Refineries**, **Assemblers**, and **Ship Tools**. Only sections present in the current mechanical grid group are rendered.
- Production types may have separate role grids under one type header, such as **Refinery Input** and **Refinery Output**, matching the visual separation in the reference UI.
- Each type header displays its name and member count and has a **Rebalance** button. A pane-wide policy selector shows the policy that every button will use.
- The vanilla **Storage**, **Energy**, **System**, and **All** filters control which sections are visible; they do not change section membership. Derive known-section visibility from the plugin's semantic roles and use obsolete `InventoryOwnerType()` only as the display-filter fallback for unknown sections.
- A physical stack contributes once to each matching configurable group and role. Groups may overlap intentionally; ship totals deduplicate physical inventories and automation never sums overlapping display rows. One physical inventory may advertise multiple non-overlapping roles, as gas generators do for ice and bottles.
- Items are combined whenever the game itself considers their contents stackable; otherwise, they remain separate entries.
- Mass, volume, search, amount dialogs, clicking, and drag-and-drop retain the normal Keen appearance where practical.

Each type section shows only items relevant to that type. Weapons show compatible ammunition, fueled power producers show their fuels, refinery input shows valid process inputs, and refinery output shows valid process results. If matching blocks exist but their relevant inventories are empty, keep an empty inventory row with the appropriate constraint icon so it remains a usable drop target.

The unified pane uses its own owner control, built from `MyGuiControlGrid` with the stock `Inventory` visual style and Keen's item-icon construction, rather than a synthetic `MyEntity` or `MyInventory`. Each grid row carries a plugin projection object that maps back to contributing real stacks. This avoids `MyInventory.AddItems`, which is a no-op on multiplayer clients, and prevents a fake inventory from leaking into replication, save code, `ContentsChanged`, or `MyInventory.OnTransferByUser` subscribers. Keep Keen's `MyGuiControlInventoryOwner` unchanged for the character pane and the vanilla fallback.

Patch the terminal at the controller boundary: when **Unified** is active, prefix-and-skip the vanilla `MyTerminalInventoryController.Init` and `Refresh` path for that inventory page and mount the plugin controller on the same tab created by `MyGuiScreenTerminal.CreateInventoryPageControls`. The plugin controller still uses Keen's real owner control for the character pane and its own projected control for the grid pane. Switching the toggle cleanly unsubscribes and detaches one controller before activating the other without rebuilding unrelated terminal tabs. Mouse and gamepad handlers must consume plugin row objects and resolve concrete inventory endpoints themselves; none of Keen's transfer handlers may receive a projected row as `MyInventory` or `MyPhysicalInventoryItem`.

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

The list contains only positive component outputs from blueprint classes supported by actual accessible assemblers in the selected scope. This includes modded recipes without a hard-coded name list and excludes loot-only components, such as plushies without a supported recipe. Search filters the list. A zero or blank target disables that row; previously saved unsupported targets remain persisted but inactive. Temporary machine state does not hide supported components: show power/working-state and eligibility reasons instead. The separate stock-style panel uses a 12-row table, target/blueprint editor, maintenance/threshold row and spaced footer actions. Live stock/queue/status updates preserve unsaved edits and selection.

The header provides **Craft deficits**, an opt-in **Maintain targets** toggle, and a configurable start threshold. For example, a target of `10,000` with a `95%` threshold starts another batch below `9,500` and queues enough to return to `10,000`. This adopts ISY's useful margin concept without reproducing its LCD parser.

The existing **Rebalance** button still applies only to the Assemblers Input and Output inventories. It never edits production queues. Component-target actions have their own controls because queueing production and redistributing inventory are materially different commands.

### Management exclusions

Each block-type header has a small **Manage members** action that lists the real blocks and inventory roles contributing to that section. It replaces ISY's `[Locked]`, `[Hidden]`, and `!manual` name tags with explicit settings:

- **Manual block:** every automatic or bulk plugin action—including sorting, target maintenance, loadouts, cleanup, and section-wide Rebalance—skips every inventory on that block. Direct user drag-and-drop remains available.
- **Reserved inventory:** the inventory remains visible, but its contents do not satisfy component or future loadout totals and no virtual, automatic, or bulk planner selects it as a source or destination. Show the reserved portion in the aggregated row's details and a **Reserved / not counted** badge so displayed and usable totals cannot be confused. The player can still manipulate it through the vanilla fallback or an explicitly selected concrete inventory.
- **Not a Unified Cargo destination:** a general-purpose cargo inventory remains visible and withdrawable but is not selected for automatic deposits or redistribution.

These switches are independent because protecting a manually operated assembler, protecting emergency stock, and preventing deposits into a particular cargo container are different intentions. Defaults leave every otherwise eligible inventory managed and counted. Store identities by block entity ID plus inventory index; never mutate block names or custom data.

## Logical inventory-owner discovery

Do not build the unified UI around the obsolete `MyInventoryOwnerTypeEnum` or `InventoryOwnerType()` result. Keen's fallback classifies unknown entities as `Storage`, which would make an unfamiliar modded weapon or machine look like a safe cargo destination.

Reuse the terminal's own inventory discovery rules instead of maintaining a second interpretation of scope. Enumerate `MyCubeGridGroups.Static.Mechanical.GetGroup(interactedGrid).Nodes`, then invoke each member grid's `MyGridConveyorSystem.GetGridInventories(interactedEntity, owners, identityId)` and subscribe to that conveyor system's `BlockAdded` and `BlockRemoved` events. Preserve the originally interacted entity argument: vanilla uses it to include that block even when `ShowInInventory` is false. This inherits the terminal's access and inventory-visibility filtering and gives mechanical split, merge, and suspension changes the same behavior as vanilla. Connector-docked logical grids remain separate mechanical scopes.

Instead, describe every real inventory before rendering it:

```text
InventoryDescriptor
    owner entity ID
    block definition ID
    inventory index
    block-type section
    one or more inventory roles with item predicates
    accepted-item constraint signature
    discovery provider
```

The resolver order is:

1. Known vanilla definition and runtime families, which also cover mods using those object builders.
2. The live inventory's whitelist or blacklist constraint. Send/receive flags describe conveyor automation and help discovery; they must not reject otherwise valid manual transfers (for example, withdrawing fuel from a receive-only reactor).
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
- **Other constrained systems:** use the same provider pattern for production inputs/outputs, gas generators, parachutes, tools, and future block families. A gas generator's one physical input inventory exposes separate ore/fuel and bottle roles using its live `m_oreConstraint` and `m_containersConstraint`, while each stack is rendered only in its matching role. Until a semantic provider exists, the generic constraint-based group remains safe and usable.

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

The physical sorter works only on a refinery's own input inventory. Same-inventory transfers are swaps, not list insertions: when the destination contains an incompatible stack, `TransferByUser(input, input, itemId, destinationIndex)` swaps the two slots; stackable contents merge. Plan the desired order as selection-sort-style pairwise swaps, requiring at most `n - 1` swaps for `n` stacks, and recompute from the replicated order after every request. Keen's server handler validates access and ownership, applies the swap or merge, and marks the refinery queue for rebuild from the changed input order. The client must never call the server-only transfer implementation directly.

Run at most one reorder request per refinery at a time and wait for replicated inventory state before planning the next swap. Debounce content changes, skip inventories that already match, and cap work per update because refineries with **Use conveyor system** enabled append newly pulled ore to the input and can otherwise create a request storm. Closing the terminal does not cancel an already queued bounded pass, but client-only automatic sorting exists only while that client is connected and the plugin is active.

## Component-target engine

Component targets use definition data rather than ISY-style recipe learning. Enumerate loaded blueprints, index every result whose type is `MyObjectBuilder_Component`, and retain candidates usable by at least one accessible assembler in the scope. Prefer Keen's canonical result-to-blueprint mapping when it is usable; otherwise prefer a primary, single-result blueprint. If several plausible modded recipes remain, show a blueprint picker and persist the player's choice instead of guessing from subtype names.

For each target component, derive:

```text
stock  = component amount in every accessible non-Reserved in-scope inventory, counted once
queued = sum of the component produced by all remaining assembly queue items
deficit = max(0, target - stock - queued)
```

Queue accounting uses each blueprint's actual result amount, including multi-result modded recipes. Co-products update the queued totals of their own component rows as well. Components and their targets are integral even though the game represents amounts with `MyFixedPoint`.

When **Craft deficits** is clicked, or **Maintain targets** observes that stock is below the configured threshold, convert the remaining deficit into whole blueprint runs and append those runs only to accessible assemblers matching the terminal's own eligibility rules: assembly mode, **Use conveyor system** enabled, not cooperative/slave, and `CanUseBlueprint` true. Exclude an assembler whose replicated `CurrentState` is `InventoryFull` or `MissingItems` for the proposed work and show its other states (`Disabled`, `NotWorking`, `NotEnoughPower`) in the status column.

Prefer the eligible assembler with the least estimated queued production time, using each blueprint's `BaseProductionTimeInSeconds / (MySession.Static.AssemblerSpeedMultiplier * (AssemblySpeed + UpgradeValues["Productivity"]))`, then recalculate after every accepted batch. Respect `MySession.Static.MaxProductionQueueLength`. Send `InsertQueueItemRequest`, but acknowledge success only when the replicated queue's amount for that blueprint increases; the success broadcast can occur even when nothing was inserted, and insertion at `-1` may merge with the last queue item and reuse its item ID. Track blueprint-and-amount deltas, never a queue event or queue item ID, before issuing more work.

Example: the Steel Plate target is `10,000`, accessible inventories contain `7,200`, and existing assembler queues will produce `800`. The remaining deficit is `2,000`, so the client appends only the blueprint runs needed for those `2,000` plates. If stock is `9,200` and `800` are already queued, it appends nothing even though the on-hand value alone is below a `95%` start threshold.

Within a planned client batch, add each assignment's estimated production time to that assembler's projected workload before choosing the next assignment. The server rereads all queues after each synchronous addition. This avoids assigning every component deficit to the same initially idle machine. Targets accept whole quantities from zero through 1,000,000,000,000; invalid values do not overwrite the saved target.

The first implementation is deliberately add-only:

- Never clear, move, shorten, or change the mode of an existing assembler queue.
- Never toggle cooperative, repeating, conveyor, or power settings.
- Skip disassembly-mode and cooperative assemblers and allow per-block exclusions for machines the player is using manually.
- Do not implement automatic disassembly merely because a target was lowered.
- Keep one target batch in flight per scope and include all existing queue entries in the next deficit calculation.

These rules avoid claiming ownership of production work that may have been added by the player, another client, or another script. Two independent clients can still race after reading the same replicated deficit; the client-only implementation mitigates this with debouncing and one in-flight batch but cannot provide a distributed lock.

## Client-local settings and lifetime

The client-only plugin persists refinery and production intent in its own local configuration, keyed by a stable world identity plus an anchor grid entity ID selected for the mechanical group. Use the checkpoint session ID for a local world; in multiplayer use the server Steam ID plus the world name or checkpoint ID. Do not use mutable `MySession.Static.Name` alone. The profile stores only intent:

```text
refinery mode, auto-sort toggle, pinned/manual ore definition IDs
component targets, maintain toggle, start threshold, blueprint overrides
block/inventory management exclusions
Phase 2 machine loadout rules
```

Derived recipe maps, automatic ore order, live stock, queues, inventory contents, and capacities are rebuilt from the current session and are never persisted as truth. If the anchor no longer exists or a grid is copied into another world, create a new local profile rather than applying settings by matching a mutable display name.

Local profiles are private to that player. Automatic work runs only while the player is connected, the plugin is active, and vanilla request validation grants access. The UI should label this state **Local automation** so users do not mistake it for offline or faction-wide control. The optional shared and unattended model is specified only in the server companion plan.

## Configurable inventory groups and generic loadouts

The former fixed sections and machine-specific loadouts are now editable presets over one grouping and loadout system. This section supersedes fixed-section assumptions in the original first-pass scope above. The client remains standalone; no server capability is required.

Each ship-local profile stores an ordered list of groups with stable IDs, display names, block selectors, optional inventory-role filters, and optional item-category/exact-item filters. Select blocks by all blocks, known family, object-builder type, exact block definition, terminal block-group name, specific block entity, or a loaded production blueprint's result item. Block selection, role and item filters are combined. Recipe-output selection identifies production blocks capable of that output; their input/output role and material filters remain separate choices.

Save terminal group **names**, scoped to the current mechanical ship, never a snapshot of their block IDs. Resolve current members when displaying or planning; newly added members participate automatically. Equal names on connected but mechanically distinct ships are never merged. Missing or renamed groups show **Group not found** and pause rules. Specific-block selectors alone intentionally save entity IDs.

The **Groups** button opens the ordered group list: new, edit/rename, duplicate, move up/down, delete, and restore defaults. Built-in cargo, weapons, power, refinery, assembler, gas, tool, safety, connectors and unknown-definition entries are presets. Unknown-definition presets retain exact definition/inventory/constraint separation. Editing groups only changes views; it does not transfer stock. Restore defaults resets built-in presets after confirmation while preserving custom groups and loadouts. Removing a referenced group retains the inactive rule for repair.

Definition adapters remain responsible for ammo, fuel, blueprint and live inventory constraints. Display names never determine capabilities. Mixed/custom groups expose relevant production actions from their actual members. Ore-priority and component-target settings remain ship-wide; rebalance operates on the selected group's role inventories. Idle-drain utilities are explicitly ship-wide.

The pane-level **Loadouts** button opens all rules, including rules for groups with no current members; section shortcuts filter that list. The list provides new/edit/delete and explicit apply, with a separate editor so routing options do not crowd the inventory table. A rule stores:

```text
LoadoutRule
    target group ID and inventory role
    supply group ID (or None to disable supply)
    excess-return group ID (or None to retain excess)
    item definition ID
    target mode: amount per member or total across members
    target amount
    distribution policy
    maintain enabled
    include non-working blocks: false by default
```

Legacy exact-block and definition-specific loadout restrictions are retained during migration. Editing their quantity or policy keeps those restrictions; changing their target group replaces them with that group's selector. Schema version 1 migrates the former section targets to deterministic preset IDs, with Unified Cargo as the initial supply and excess-return group. Groups and new loadouts subsequently use stable IDs independent of display names and ordering.

Only offer items accepted by the target inventories' loaded definitions and live constraints. This gives modded weapons, reactors, generators, tools, and other constrained blocks automatic support without dedicated uranium, ice, or ammunition code paths.

For **amount per member**, evaluate every compatible, non-excluded physical inventory separately. For **total across members**, calculate one group deficit or excess and use the rule's distribution policy. Source deficits from the selected supply group and send excess to the selected return group. Exclude every same-item loadout target from supply and return candidates, including overlapping views. Two rules targeting the same item in the same inventory are both visibly blocked, regardless of list order or maintenance mode. Reserve source quantities across a batch, cap transfers against current target deficits/excesses, and revalidate saved rules, group membership, working state, exclusions and conveyor access before execution. No rule toggles conveyor, enabled, stockpile or power settings.

Example: a Weapons rule requests `10 NATO_25x184mm` magazines per compatible member. Gatling weapons receive them, missile launchers are excluded by their constraints, and definition-derived modded magazines are offered as separate rules for the weapons that accept them. If Unified Cargo lacks enough stock, perform the valid partial transfer and show the remaining deficit.

Use the existing snapshot, target accounting, candidate filtering, placement policies, transfer queue, and local profile storage. Client-only **Maintain** runs only while the plugin is active; a one-shot **Apply loadouts** action remains available when continuous maintenance is disabled.

## Explicit utility actions

### Native bottle refill — custom job retired

Custom bottle refill is retired on SE 1.210+. Use native generator bottle pulling/auto-refill or a supplied Medical Room, Survival Kit or Refill Station. These are not identical to the removed job: native pulling does not promise to return bottles to their original cargo. The plugin leaves native settings untouched.

### Drain idle assemblers

Add **Drain Idle Assemblers** to the Assemblers section. At execution time, an eligible assembler must be in assembly mode, have an empty queue, not be producing, and not be marked Manual. Disassembly-mode assemblers are excluded even with an empty queue because their output contains stock deliberately staged for disassembly. Move contents from eligible non-Reserved input and output inventories into Unified Cargo through the normal destination planner. Recheck mode and idle state before every assembler so a changed or newly queued machine is skipped rather than drained.

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

### Vanilla-equivalent reachability gate

No transfer request may leave the client until it passes the same two-stage reachability test as the vanilla terminal:

1. `MyGridConveyorSystem.AppendReachableEndpoints(sourceEndpoint, playerId, results, itemId, predicate)` must include the candidate destination, applying player access, sorter direction and filters, and `NeedsLargeTube`.
2. The plain conveyor-system `Reachable(from, to)` check must also pass.

For character transfers, use the terminal's interacted block as the character-side conveyor endpoint, even when the source pane resolves to another mechanical group. Cache results only for the current operation, keyed at minimum by source endpoint and item definition, invalidate the cache on relevant grid or conveyor changes, and cap reachability queries per frame because the pathfinder takes a global lock. Recheck ownership, proximity, and reachability immediately before every request. This gate is mandatory on unmodified servers because their `TransferByUser` handler does not enforce conveyor connectivity.

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

Example: the user requests a transfer of `1,000` plates, but only `650` still fit when execution begins. The executor moves at most `650`, reports a partial result such as `650 / 1,000 moved: destination full`, and refreshes the view. It must never compensate by editing the projected GUI or inventing/removing item amounts.

Transfer allocations run through a small bounded queue so a large aggregate action does not issue hundreds of mutations or conveyor queries in one frame. Real inventory change events are the source of truth for refreshing the virtual view; optimistic GUI changes must not be treated as committed state. Every wait for replicated inventory or queue state has a timeout. Repeated timeouts or failed preflight checks stop the automatic operation instead of continuing to issue requests that the server will silently drop and record as failed validation.

Each allocation calls `MyInventory.TransferByUser` normally so other plugins observing `MyInventory.OnTransferByUser` remain compatible; the plugin never raises that event itself. A multi-stack operation is not atomic. If one allocation fails or transfers less than requested, the client rechecks the remaining plan against live inventory state and either continues with another valid allocation or reports the partial result. If `CheckConstraint` rejects a locally planned allocation before a request is sent, treat it as a planner defect, log it, skip that allocation, and do not wait for replication that cannot arrive.

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

Dragging between distinct configurable groups on the same mechanical ship uses the normal transfer pipeline, with self-inventory pairs skipped. Item and role filters constrain deposits. **Rebalance** explicitly redistributes within a selected group. Transfers between separate mechanical ships remain supported through valid vanilla conveyor paths. Changing or deleting a named group while work is queued cancels stale work; the next request resolves current membership.

## Deployment assumptions

- Works against an unmodified server, including official-style environments.
- Uses normal `MyInventory.TransferByUser` requests for every real transfer.
- Uses normal `MyProductionBlock.InsertQueueItemRequest` calls for component production and same-inventory `TransferByUser` requests for refinery input ordering.
- Relies on Keen's existing server handlers to validate ownership, access, proximity, amounts, constraints, and synchronize accepted mutations; no plugin code or programmable block is required on the server.
- Treats the complete vanilla client reachability pair as a hard pre-request invariant because Keen's server handler does not validate conveyor connectivity, sorter rules, or tube size for `TransferByUser`.

## Optional companion transport boundary

Optional companion discovery uses the vanilla mod-message channel exposed by `MyAPIGateway.Multiplayer` / `MyModAPIHelper.MyMultiplayer`, not plugin-defined network events. An unmodified server silently drops an unknown message-channel ID, preserving the client-only fallback without requiring matching event tables.

Companion acknowledgements never replace replicated game state as the UI's source of truth. After a batched result, refresh from the real replicated inventories and queues. If a known companion times out on an in-flight request, do not replay that request through vanilla transfers: refresh state and report **unknown outcome**. Only later, newly initiated operations may use the client-only path, which prevents a slow companion response from double-moving items.

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
- Loadout source isolation: missing stock comes from the configured supply group (Unified Cargo by default), excluding other loadouts' target inventories.
- Bottle compatibility and fill progress before returning a staged bottle.
- Empty queue and non-producing state immediately before draining each assembler.

Live state may change after a plan is created. The executor must therefore tolerate partial transfers, stale stacks, destroyed blocks, and full destinations, then refresh the cargo snapshot.

## ISY-derived scope decisions

The linked [ISY's Inventory Manager source](https://github.com/dorimanx/Isys-Inventory-Manager/blob/master/Script.cs) is a feature reference only. The review produced these final decisions beyond the accepted refinery-priority and component-target systems:

- Keep type-section balancing only through the existing **Rebalance** action and placement policies.
- Replace locked, hidden, and manual name tags with the UI-managed exclusions defined above.
- Generalize special containers, reactor uranium limits, generator ice limits, and ammunition stocking into the Phase 2 machine-loadout system.
- Rely on native bottle pulling/refill; do not maintain a custom refill coordinator.
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

Refinery scarcity scores, component deficits, and Phase 2 loadout deficits are recalculated from dirty inventory or queue snapshots on a debounce, not by scanning every inventory every frame. Definition-to-recipe indexes are built once per loaded definition set. Sorting, transfers, assembler additions, bottle jobs, and drain jobs all share bounded request queues and wait for replicated state before continuing. Reachability uses a per-operation cache and a separate per-frame query budget because the conveyor pathfinder serializes on a global lock.

Measure the first read-only unified pane on a representative large station before adding automation. Compare terminal-open time, control count, layout/update time, allocations, and frame time against vanilla: replacing hundreds of `MyGuiControlInventoryOwner` instances and their `ContentsChanged` subscriptions is the primary expected performance win.

It does not remove Space Engineers' underlying conveyor graph. Hundreds of cargo containers still create hundreds of conveyor endpoints, and automated assemblers, refineries, and sorters continue using the vanilla conveyor system.

## Implementation order

1. Reuse the terminal's mechanical-scope enumeration and build inventory descriptors, safe definition-based grouping, multi-role inventories, and generic constraint fallback.
2. Build the plugin-owned `MyGuiControlGrid`-based read-only multi-section owner UI, patch the terminal controller boundary, and provide the vanilla fallback toggle; do not create a zombie inventory.
3. Aggregate items according to Keen's own stackability result, deferring additional stack optimizations.
4. Add vanilla weapon and reactor consumable providers, then cover other constrained systems as needed.
5. Add refinery and assembler input/output role sections.
6. Add **Manage members**, persist the three exclusion settings, and enforce them in every bulk or automatic operation.
7. Build definition-derived refinery recipe indexes and render the read-only automatic priority order, including modded and mixed-capability refineries.
8. Build virtual-to-real withdrawal and real-to-virtual deposit planning without enabling mutations.
9. Implement and test the mandatory vanilla reachability pair, interacted-block character proxy, operation cache, and per-frame query budget; only then enable `TransferByUser` execution.
10. Add the pane policy selector, three placement policies, and a Rebalance button for every rendered type section.
11. Add swap-based bounded refinery sorting, then auto-sort with pull-aware debouncing and Manual exclusions.
12. Add the Component Targets UI, stock and queued accounting, blueprint resolution, assembler eligibility/status, and local profile persistence.
13. Add manual **Craft deficits**, then opt-in maintain mode after content-based queue acknowledgement, queue limits, and race handling are tested.
14. Add unified-to-unified transfers between distinct mechanical grid groups.
15. Complete mouse, amount-dialog, search, drag-and-drop, and the substantial custom gamepad transfer/help paths.
16. Add Phase 2 machine loadouts by reusing target accounting and the existing transfer planner.
17. Retired: custom bottle refill; native SE 1.210+ systems cover this workflow.
18. Add the explicit **Drain Idle Assemblers** bounded job with disassembly-mode exclusion.
19. Add block-group and conveyor-component scopes.
20. Consider knapsack-style packing only for a later explicit policy.
21. Add integration and performance testing for grid splits, docking, cross-group transfers, sorters, full containers, concurrent users, destroyed blocks, timeouts, and repeated validation failures.

Definition-compatibility tests must cover vanilla weapons, conventional modded weapons, vanilla and modded reactor fuels, empty inventories, identical display names with different definition IDs, multi-inventory production blocks, one-index multi-role gas generators, and unknown constrained blocks. UI and rebalance tests must cover semantic filter mapping and unknown fallback, empty sections, input/output isolation, per-item candidate filtering, policy capture, repeated-click suppression, projected capacity, partial execution, the vanilla fallback toggle, and mouse/gamepad parity.

Refinery tests must cover pairwise swap planning, merge behavior for stackable inputs, the actual physical input order, pinned and automatic priorities, live scarcity changes, stone or other multi-output recipes, modded ores and outputs, multiple refinery capability sets, repeated conveyor pulls and content-change events, rejected same-inventory requests, and a rebalance followed by re-sort. Component-target tests must cover integral rounding, blueprint result amounts greater than one, co-products, ambiguous recipes, uncraftable modded components, existing manual queues, cooperative and disassembly-mode assemblers, `CurrentState`, maximum queue length, false-positive success broadcasts, merged queue entries, target reductions, in-flight replication delay, and two clients observing the same deficit.

Exclusion tests must prove that excluded inventories remain visible while every affected planner obeys their exact flags. Transfer tests must prove both reachability stages, sorter direction and filters, large-tube requirements, the interacted-block character proxy, cache invalidation, query budgeting, local constraint rejection, silent server timeout, and stop-on-repeated-failure behavior. Loadout tests must cover per-member and section-total targets, constrained and modded inventories, partial stock, excess returns, non-working blocks, and the prohibition on stealing from another loadout. Drain tests must cover disassembly exclusion and an assembler receiving a queue or mode change after the action was requested. Drain tests also cover disconnection, timeout, destroyed blocks, and destination-full partial results.

## UX feedback pass — 2026-09-05

- Seed each section from native inventory order (accessed block first); retain local ranks by world, mechanical scope, view, section and item content. Removing/re-adding an item reuses its rank. Absent ranks are bounded; new item kinds append. Stateful items still use Keen's stackability, never this display key, for aggregation. Identical non-stacking items use occurrence ranks; refilling a bottle does not change its ordering identity.
- Same-section drag moves a visible entry to the target position, including append into empty slots. This only edits local presentation preferences and never moves physical stacks. Refinery input remains controlled by Ore Priority. Search does not rewrite ranks. Redraws defer while dragging so indices cannot retarget a live drag.
- Non-trailing inventory refresh coalescing, capped at 50 ms (new default zero). Immediate transfer-pending feedback; authoritative item counts remain untouched until replication. Client-only multi-source moves still wait for acknowledgement; batching without an authoritative receipt is not safe.
- Replace the fallback checkbox with native-sized per-column icon buttons: a symmetric shared-container glyph, with a diagonal strike-through when off, state/action tooltip and keyboard focus. Mirror integer-pixel geometry to avoid uneven cell spacing. No text label; each button stays rightmost. Preserve the restore-vanilla safety path when both columns are off.
- Refineries expose a ship-wide **Drain ingots** action alongside Priority, using the same extra utility row as assembler Drain idle. Only ingot stacks in refinery output inventories are candidates; input ores remain untouched and refining may continue. Respect Manual/Reserved source exclusions and cargo destination flags, current distribution policy, access, conveyor routes, capacity and replication acknowledgement. This action uses bounded client-side vanilla requests, so it also works without a companion or with an older companion; it does not introduce a new wire action.
- Display ranks are private client preferences, not shared ship settings. No server companion is required.
