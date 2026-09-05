# Unified Storage client verification matrix

This matrix separates checks that run without Space Engineers from checks which require a live Pulsar client and a synchronized world. A build alone cannot prove terminal patch compatibility, replication acknowledgement, or conveyor behavior.

Results and outstanding limits from the local configurable-group/loadout UI run are recorded in [UI_E2E_VERIFICATION.md](UI_E2E_VERIFICATION.md).

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

1. Open a cargo terminal. Confirm both columns have unlabeled Unified toggles as their rightmost icons, including in character mode with hidden filters. Test all four on/off combinations; switching one column must leave the other column's layout, scope, filter, focus and scroll position unchanged. An off column in mixed mode shows individual native inventory panels; both off restores the complete original inventory controller without reopening the terminal.
   Repeat each transition three times. In both layouts, switch character/grid on both sides and exercise every filter, search/reset and Hide Empty. Confirm each selection changes the pane and re-enabling stays enabled. Close/reopen from each combination; no duplicate toggles, shifted filter positions or callbacks attached to a closed vanilla controller may remain. Cross-column double-click, drag, amount dialog and gamepad transfer must work in both mixed directions; concrete-to-concrete moves and same-inventory rearrangement must also work.
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
- Scope dropdowns: access different hatches/constructs, check the accessed network is selected and ordered first, duplicate ship names are distinguishable, and each pane renders only its chosen scope. Check whole-construct versus network totals, independent left/right selection, search/filter and suit/grid retention, and clean vanilla fallback. With one construct and one network, no extra row should appear. With multiple choices, verify search/selector/inventory bounds do not overlap.
- Change conveyor topology while the terminal is open, including opposing sorter branches. Confirm network membership updates, inventories do not appear in two networks, and a vanished selection falls back safely. A network label must never bypass per-item access, sorter or tube-size checks.
- Portless inventories (including flight seats that implement a conveyor endpoint with zero ports) must not create network dropdown entries. Disconnected single blocks with actual conveyor ports remain selectable. Whole-construct views still include eligible portless inventories.
- Split and merge a mechanical construct, attach rotor/piston/hinge/suspension subgrids, and connector-dock a second construct. Confirm mechanical scopes rebuild without merging the docked constructs.

## Refinery priority

- Test automatic scarcity changes, pinned inputs, manual ordering, mixed Basic/standard/modded refineries, stone multi-output recipes, and a modded ore/output.
- Confirm Sort Now creates the physical order using bounded same-inventory swaps, including merge cases.
- Confirm Manual refineries are skipped even when marked after a job was queued.
- Rebalance refinery inputs and confirm the sort runs only after transfer acknowledgements.
- Leave vanilla conveyor pulling enabled long enough to determine whether it persistently defeats the requested order; refinery filling remains deferred unless this gate fails.

## Component targets

- Verify the 12-row viewport, separate target/blueprint editor, maintenance/threshold row and evenly spaced footer buttons. Search, clear, scroll, save/reopen and empty results must keep the selected editor consistent; empty results disable target editing/saving.
- List only positive component outputs of blueprint classes supported by the scope's actual vanilla/modded assemblers. Loot-only components (including plushies without a supported mod recipe) must not appear. Temporary machine state must not hide supported components. Previously saved unsupported targets remain persisted but are not offered or queued until support returns.
- Test output amounts greater than one, co-products, ambiguous recipes, uncraftable modded components, and explicit blueprint overrides.
- Confirm current stock plus all existing manual queue output satisfies a target before new work is appended.
- Confirm cooperative, disassembly-mode, inaccessible, incompatible, missing-item, full-output, and maximum-queue assemblers are not selected.
- Verify merged queue entries are acknowledged by blueprint-and-amount delta and false-positive queue events cannot issue a second request.
- Reduce a target and confirm no queue is cleared or shortened.
- Test maintain threshold and two clients observing the same target; duplicate work is a documented client-only race, not silently hidden.

## Loadouts and utilities

- Test per-member and section-total rules on vanilla and modded constrained blocks, partial cargo stock, excess returns, non-working opt-in, and Manual/Reserved exclusions.
- Confirm a loadout uses its configured supply and excess-return groups, respects disabled directions, and never steals from another loadout's targets.
- Exercise per-inventory and group-total targets, overlapping-target conflicts, missing groups, target/role changes, and maintained rules with the terminal closed.
- Confirm the loadout table updates its status after transfers without closing the screen or losing row selection.
- No plugin Refill button/coordinator remains. Native gas inventories and the drain action remain accessible; legacy companion refill requests fail closed.
- Drain an idle assembly-mode assembler, then repeat while adding a queue entry or switching to disassembly after clicking. Confirm the live guard skips it without changing its queue or mode.
- Drain ingots from active and idle vanilla/modded refineries. Verify only output ingots move to general cargo, ore input and any non-ingot outputs remain unchanged, and totals are conserved. Exercise empty outputs, full cargo, blocked sorters, disconnect/removal, and Manual/Reserved changes after enqueue. Repeat without a companion and with the currently deployed older companion; no unsupported wire intent should be sent. Confirm Priority and Drain ingots reserve separate header rows without overlapping inventory titles.

## Performance capture

On a representative large station, record the same inventory-page open/close sequence in vanilla and Unified modes. Capture terminal-open wall time, rendered control count, allocations, average frame time, and worst frame. Repeat after an inventory content change and after a block add/remove. Confirm routine updates are event/debounce driven and transfer, sort, production, and bottle work issue at most one mutation while waiting for replicated state.

## UX ordering/toggle acceptance

- Same-section drag changes display order without native inventory mutations, in both directions and into empty slots. Search and refresh preserve it; close/reopen and client reload retain it. Remove/re-add a tool without moving it to a new rank.
- Refinery input remains ore-priority driven. Stateful rows retain native stack distinctions.
- Two-state native icon toggles off/on independently per column; vanilla and unified suit/grid tabs still work. Compare bounds and focus with adjacent stock icons; no label, rightmost placement in both columns, bright hover and darker retained focus.
- Double-click has immediate pending feedback and no trailing-refresh starvation. Verify actual source/destination quantities; do not fake optimistic counts.

### Live UX results — 2026-09-05

Verified on NewTest with the client devfolder and existing companion, SE 1.210.14, 3440×1440 fullscreen:

- Same-section backward, adjacent-forward and empty-slot drags changed display order. Native cargo item IDs, amounts and physical order were unchanged by those drags.
- Search/clear and terminal close/reopen preserved ranks. The grinder was deposited, moved to a chosen slot, withdrawn and re-deposited through the UI; it returned to that slot. A full client restart retained custom Computer/Grinder ordering.
- Left-pane drag refreshed both panes when they showed the same view. Three disable/re-enable cycles passed on the final glyph build; both panes' vanilla and unified suit/grid controls continued working and ranks survived toggles.
- The icon's reported width/height exactly matched a native filter button. The symmetric on glyph and struck-through off glyph were visually inspected. No plugin Refill button remained; native gas inventories remained visible.
- One companion-backed grinder withdrawal took about 0.33 seconds from Remote's queued double-click gesture to the observed UI update, including input synthesis and polling. This is not a vanilla comparison or network-latency benchmark. Multi-source standalone acknowledgement cost is unchanged.
- The test grinder was returned to the character. Original cargo display order and native character order (Grinder, Hand Drill, Welder) were restored; the latter used vanilla's swap gesture. No additional stock was spawned for this UX pass.

Both Release builds, existing core tests and the temporary companion harness passed; no repository regression suite was added. Old XML containing the removed bottle option loaded through PluginSdk while retaining other operator settings, and the new Quasar schema omitted that option. The new server removal is build/config tested, not yet deployed or live-tested. Refinery priority precedence remains in code; a fresh multi-ore live sorting test and other resolutions/gamepad input were not repeated in this UX pass.
