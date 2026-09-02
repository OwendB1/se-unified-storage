# Unified Storage client verification matrix

This matrix separates checks that run without Space Engineers from checks which require a live Pulsar client and a synchronized world. A build alone cannot prove terminal patch compatibility, replication acknowledgement, or conveyor behavior.

## Automated checks

Run on every change:

```sh
dotnet run --project Tests/UnifiedStorage.CoreTests.csproj -c Release
dotnet build ClientPlugin/ClientPlugin.csproj -c Debug -p:RunPostBuildEvent=Never
dotnet build ClientPlugin/ClientPlugin.csproj -c Release -p:RunPostBuildEvent=Never
```

The core suite verifies deterministic greedy allocation, equalization from unequal starting quantities, capacity redistribution, and integral-item rounding. The client builds compile every terminal patch, definition adapter, planner, executor, automation coordinator, and management screen against the locally installed game assemblies.

## Required in-game smoke test

Use both a local world and an unmodified multiplayer server.

1. Open a cargo terminal. Confirm Unified is initially enabled, both panes render, and disabling Unified restores the complete vanilla inventory page without reopening the terminal.
2. Re-enable Unified. Exercise All, Energy, Ship, Storage, System, search, Hide Empty, scrolling, mouse dragging, right-click amount entry, double-click, and gamepad A transfer.
3. Verify identical components aggregate while damaged tools, bottles with different fill states, and other game-nonstackable content remain separate.
4. Test character-to-cargo, cargo-to-character, and docked mechanical-group-to-mechanical-group transfers. Disconnect during an operation and confirm a partial/timeout notification rather than an optimistic UI mutation.
5. Test a one-way sorter in both directions, a whitelist and blacklist, and an item requiring a large tube. Confirm no request is sent over a route the vanilla terminal rejects.
6. Destroy or close a source and fill a destination after planning but before execution. Confirm the operation safely skips or reports partial completion.
7. Mark inventories Manual, Reserved, and No Unified Cargo Destination. Confirm they stay visible, Reserved rows show an R and reserved amount, bulk actions skip the correct members, and vanilla fallback still permits explicit manipulation.

## Definitions and grouping

- Vanilla Gatling and missile weapons show only their compatible ammunition.
- A conventional modded weapon built from `MyWeaponBlockDefinition` discovers its magazines without a subtype rule.
- Vanilla reactors show uranium; a modded reactor with multiple or replacement fuels shows exactly its `FuelInfos` fuels.
- Empty constrained inventories remain visible drop targets.
- Different definition IDs with identical display names remain separate fallback sections.
- Refinery and assembler inputs/outputs are distinct roles.
- A gas generator's shared inventory renders ice/fuel and bottles once each in their respective roles.
- An unknown constrained mod block is grouped by exact definition, inventory index, and constraint signature and is never treated as Unified Cargo.

## Rebalance and scope views

- Exercise Existing Stack First, Fill First, and Even By Item on components and fractional ores.
- Verify one click does not overbook a container's volume across several item types.
- Verify refinery input/output and mixed weapon ammunition candidates never cross roles or incompatible constraints.
- Confirm repeated Rebalance is disabled while the transfer queue is active and the captured policy does not change mid-operation.
- Switch to terminal block-group and conveyor-component scope modes. Transfer between distinct views and confirm same-view drops are no-ops.
- Split and merge a mechanical construct, attach rotor/piston/hinge/suspension subgrids, and connector-dock a second construct. Confirm mechanical scopes rebuild without merging the docked constructs.

## Refinery priority

- Test automatic scarcity changes, pinned inputs, manual ordering, mixed Basic/standard/modded refineries, stone multi-output recipes, and a modded ore/output.
- Confirm Sort Now creates the physical order using bounded same-inventory swaps, including merge cases.
- Confirm Manual refineries are skipped even when marked after a job was queued.
- Rebalance refinery inputs and confirm the sort runs only after transfer acknowledgements.
- Leave vanilla conveyor pulling enabled long enough to determine whether it persistently defeats the requested order; refinery filling remains deferred unless this gate fails.

## Component targets

- Test output amounts greater than one, co-products, ambiguous recipes, uncraftable modded components, and explicit blueprint overrides.
- Confirm current stock plus all existing manual queue output satisfies a target before new work is appended.
- Confirm cooperative, disassembly-mode, inaccessible, incompatible, missing-item, full-output, and maximum-queue assemblers are not selected.
- Verify merged queue entries are acknowledged by blueprint-and-amount delta and false-positive queue events cannot issue a second request.
- Reduce a target and confirm no queue is cleared or shortened.
- Test maintain threshold and two clients observing the same target; duplicate work is a documented client-only race, not silently hidden.

## Loadouts and utilities

- Test per-member and section-total rules on vanilla and modded constrained blocks, partial cargo stock, excess returns, non-working opt-in, and Manual/Reserved exclusions.
- Confirm a loadout sources deficits only from Unified Cargo and never steals from another loadout.
- Refill empty oxygen and hydrogen bottles through a tank and generator. Confirm the explicit refill request, replicated gas-level wait, original-inventory return, fallback/stranded report, disconnection, and timeout paths. Partially filled bottles remain excluded until both filler paths are verified.
- Drain an idle assembly-mode assembler, then repeat while adding a queue entry or switching to disassembly after clicking. Confirm the live guard skips it without changing its queue or mode.

## Performance capture

On a representative large station, record the same inventory-page open/close sequence in vanilla and Unified modes. Capture terminal-open wall time, rendered control count, allocations, average frame time, and worst frame. Repeat after an inventory content change and after a block add/remove. Confirm routine updates are event/debounce driven and transfer, sort, production, and bottle work issue at most one mutation while waiting for replicated state.
