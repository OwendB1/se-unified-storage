# Unified Ship Storage Plan

## Goal

Give every ship one virtual cargo inventory that combines items scattered across its real inventories. This removes the need for inventory-management scripts and makes distributed storage practical without changing where items physically exist.

## First-pass scope

- One virtual cargo per mechanical grid group: the main grid plus rotors, pistons, hinges, and attached subgrids.
- Do not merge a docked ship with a station merely because connectors are locked.
- Read all accessible inventories normally shown in the terminal.
- Use regular cargo-capable inventories as automatic deposit targets.
- Preserve all physical inventories and vanilla destruction and raid behaviour.

Separate conveyor networks, sorter rules, special storage, logical groups, and player-defined block groups come later.

## Client UI

A Pulsar client plugin adds a **Unified** toggle to each grid-side inventory pane.

When enabled:

- Hundreds of inventory panels are replaced by one stock-looking owner panel.
- Its title is the ship or grid name, such as "Basic Refinery".
- Compatible stacks appear as one combined entry.
- Tools, bottles, datapads, damaged items, and other stateful items remain separate.
- Mass, volume, search, amount dialogs, clicking, and drag-and-drop retain the normal Keen appearance where practical.

Internally, the client uses a local-only "zombie" entity containing a synthetic `MyInventory`. It exists only to feed the stock GUI and is never registered, saved, replicated, or treated as real storage.

## Transfer architecture

The virtual inventory never owns items. Every operation resolves to transfers between real inventories.

```text
Real inventories -> cargo snapshot -> virtual GUI
                         |
User operation -> transfer planner -> real inventory transfers
```

- **Virtual to real:** Select physical source stacks, preferably largest first to minimize calls.
- **Real to virtual:** Select destination inventories using the configured distribution policy.
- **Virtual to virtual:** Initially disabled; later this becomes an explicit rebalance operation.
- Process transfers through a small queue rather than sending hundreds in one frame.
- Refresh the virtual view from real inventory change events.

## Deployment modes

### Client-only MVP

- Works technically against an unmodified server, making it suitable for official-style environments where a server companion is unavailable.
- Uses normal `MyInventory.TransferByUser` requests for every real transfer.
- Reproduces vanilla access, capacity, and conveyor checks before issuing transfers.

### Optional Magnetar server companion

- Rebuilds plans from authoritative server state.
- Batches transfers.
- Validates scope, access, conveyor routes, sorter rules, tube sizes, and capacity.
- Adds rate limits, clearer partial-transfer results, server configuration, and telemetry.
- Shares the same planning logic as the client.

## Backend separation

Keep policy planning independent from game mutation:

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
- Integral item amounts.

Live state may change after a plan is created. The executor must therefore tolerate partial transfers, stale stacks, destroyed blocks, and full destinations, then refresh the cargo snapshot.

## Performance expectations

The plugin should reduce:

- Terminal GUI controls and rendering.
- Manual inventory searching.
- Inventory-script polling.
- Player effort and, with a server companion, multi-stack transfer traffic.

It does not remove Space Engineers' underlying conveyor graph. Hundreds of cargo containers still create hundreds of conveyor endpoints, and automated assemblers, refineries, and sorters continue using the vanilla conveyor system.

## Implementation order

1. Build a read-only unified client view.
2. Implement correct stack aggregation and stateful-item handling.
3. Add virtual-to-real withdrawal.
4. Add real-to-virtual deposits.
5. Add the three placement policies.
6. Complete mouse, amount-dialog, search, drag-and-drop, and gamepad support.
7. Add the optional server companion and capability handshake.
8. Add block-group and conveyor-component scopes.
9. Add explicit storage rebalance and optional knapsack experiments.
10. Add integration and performance testing for grid splits, docking, sorters, full containers, concurrent users, and destroyed blocks.
