# Configurable groups and loadouts: live verification

Run: 2026-09-04, Linux Pulsar, Space Engineers 1.210.014, 3440×1440.
The original `Ship Core test world` was saved to a separate `Unified Storage E2E checkpoint` before inventory tests. Tests used the Sidewinder construct. These are live local-world results, not multiplayer certification.

## Verified

- Group editor layout, native keyboard navigation, create, rename, edit, duplicate, move up/down and delete.
- Named terminal-group selection persists its name in XML and survives a complete client restart. `ECM Repair Welders` resolves five inventories; the existing remote-control-only group resolves zero rather than broadening to all blocks.
- Exact block-definition selection resolves the inset connector independently from the other three connectors, including the structural connector.
- Loadout list and separate editor layout; target/supply/return selectors; per-inventory and group-total settings; invalid negative quantity rejected.
- Changing target from cargo to power producers shows uranium, not the previously selected ammunition (fixed during verification).
- Per-inventory supply: one construction component reached the connected inset connector. Three disconnected connectors were skipped with `no valid conveyor path`; the job reported `Partial: 1 / 4 moved`.
- Physical quantities checked in saved inventory data: cargo construction components 3,084 → 3,083 and connector 0 → 1. Group-total target 1 was then already satisfied and did not move more items.
- Reducing the group-total target to zero returned the component through the normal transfer executor: `Complete: 1 / 1 moved`; cargo 3,084, connectors zero again.
- Exact-definition target: `Complete: 2 / 2 moved`. Disabling excess returns left both components in the connector even with target zero.
- Disabling supply prevented a deficit from issuing transfers.
- Two overlapping construction-component rules displayed conflict and issued no transfer when Apply loadouts was clicked.
- Deleting a referenced group left the rule visible as `Group not found`; applying it issued no transfers. The editor preserved the missing selection for repair. Deleting the rule worked.
- Opt-in local maintenance supplied one additional component with the terminal closed: cargo 3,081, connector 3. Maintenance was explicitly disabled afterward.
- After a second client restart, the saved rule/group reopened correctly. Returning all three components reported `Complete: 3 / 3 moved`; status changed from `Has excess` to `On target` without reopening the screen, and selected row remained selected. A repeat Apply was a no-op.
- Applying an ammo-category filter to the target group displayed `Item excluded by group` for the construction-component rule and prevented transfers.
- Deleted/restored the Weapons preset, then restored the original preset order. Removed every temporary group and loadout; the profile contains ten defaults and no loadouts.
- Both unified panes render and their top-level Loadouts buttons respond. Vanilla fallback can be toggled off/on without reopening the terminal.
- Repeated vanilla/unified handover: 102/102 live assertions passed over three cycles, covering both selectors, both panes, and every filter in both modes. The first expanded test incorrectly expected vanilla Ship to reduce the inventory count; game source confirms it selects the construct without a category restriction. Corrected that expectation and reran the complete check successfully.
- Closing the terminal from vanilla and from unified, then reopening it, restored the enabled unified view and working selectors on both sides. Final dual-pane screenshot checked at 3440×1440.
- Original `Ship Core test world` restored, terminal closed, fullscreen restored, and session confirmed unpaused. Temporary groups/loadouts removed; the separate E2E save remains available.
- Debug and Release builds: zero warnings/errors. Existing core tests passed; no new regression tests were added.

## Fixes found during testing

1. The loadout item picker retained incompatible items after target/role changes. It now retains an unavailable item only when initially loading an existing saved rule for repair.
2. Loadout status cells were snapshots and stayed stale after transfers. They now refresh once per second while focused, retaining the table and its selection. Live recheck passed.
3. The fallback label/checkbox overlapped the left filter buttons when both panes showed grids. Centered them in the measured gap between the left-anchored selector and right-anchored filter bounds; size and vertical alignment are unchanged. Dual-pane screenshot recheck passed.
4. Controller handover left stale radio buttons selected, so later suit/grid clicks did not fire selection events in either mode. Keen's `Close()` also leaves anonymous filter callbacks attached; resetting selection exposed those callbacks accessing the closed controller's cleared controls and forced fallback. Binding now removes only callbacks belonging to that vanilla controller and clears selected flags before creating the new groups. Deactivation clears selected flags before handing the controls back to vanilla. Full live handover recheck passed; no Unified Storage errors appeared in the final runtime log.

## Inventory-slot follow-up (2026-09-05)

With approval, extended the sibling `CometWorks/se-remote` plugin with live control/item IDs, bounded grid-slot inspection, and native click/double-click/drag gestures. Selection uses the native focus/selection properties. Mouse gestures use slot geometry, the safe GUI rectangle and logical mouse dimensions; no caller-supplied pixel workaround or direct inventory mutation was used.

Tests ran in a separate `Unified Storage slot E2E checkpoint` save:

- Right-drag opened the native amount dialog. The initial cockpit-based transfer reported `Partial: 0 / 5 moved: no valid conveyor path` and left quantities unchanged.
- Exited the cockpit, walked to Small Cargo Container 36 and opened its physical inventory access point. Right-drag with amount 5 reported `Complete: 5 / 5 moved`; left-drag returned all five through the normal executor.
- Double-click withdrew 16 of 20 displays, correctly skipping four in two disconnected inventories. Double-click return reported `Complete: 16 / 16 moved`.
- Reversed the panes (unified on left, character on right): right-drag amount 3 and return left-drag both completed.
- Dragged the hand drill into unified cargo and back. Saved physical content retained the original gun entity ID `77045134237251221`, with exactly one drill in the suit afterward.
- Cancelling an amount dialog left quantities unchanged.
- Remote input rejected a missing control ID, negative slot, wrong item ID, wrong focused screen, clipped slot, and a grid ID invalidated by toggling to vanilla. No request moved items.
- Parsed saved physical inventories, counting only the component-container representation (the character save also contains a legacy duplicate inventory representation): construction cargo/suit 3,079/5 after withdrawal and 3,084/0 after returns; displays 4/16 after partial withdrawal and 20/0 after return. All three saved snapshots retained the original drill.
- Final Remote build reloaded successfully: grid data contains proper lists rather than shared-array circular-reference markers, native single-click selects the requested slot, and right-drag still opens the amount dialog. Cancelled that final dialog without moving items. Debug/Release builds, Python CLI compilation/help, live OpenAPI schema and the existing Unified Storage core suite passed. Original world restored fullscreen and unpaused.

This verifies local-world native mouse transfer handling in both pane arrangements, not multiplayer replication or successful transfers between separate mechanical constructs. No connected second mechanical construct was available in the tested ship scope.

## Docked-construct follow-up (2026-09-05)

The user supplied two docked Sidewinders and physical conveyor-port access. Saved the current original world before testing; did not reload an older checkpoint or change the docking fixture.

- Both mechanical constructs appear as separate unified owners in both panes. Storage filtering plus Hide empty exposes both cargo sections for native slot gestures.
- Saved grid IDs are `74429149051380254` and `76945956625167004`. Each starts with 3,084 construction components.
- The saved docking link is reciprocal: Structural Platform Connector `80657221941943522` ↔ Inset Connector `99656094321668297`. Both are enabled, with trading disabled. A locked dock alone does not establish cargo reachability.
- Native right-drag between distinct unified owners opened the amount dialog in both directions. Each request for five construction components reached the executor and reported `Partial: 0 / 5 moved: no valid conveyor path`.
- Cross-checked with unified disabled: searched both vanilla panes for Small Cargo Container 36 and dragged construction components between the two distinct containers. Vanilla greyed out the other ship's cargo and refused the drop without opening an amount dialog. This corroborates the unreachable cargo route for the tested pair; it does not establish that structural connectors inherently cannot convey items.
- Parsed baseline, forward, reverse and final saved physical inventories. Both ships remained at 3,084 construction components; all other grids' construction totals also remained unchanged. No items needed returning.
- Cleared diagnostic searches and restored unified storage in both panes. The original world remains saved, fullscreen and unpaused, with the supplied dock intact. No implementation changes were made for this check.

Successful cross-construct movement remains blocked on a usable conveyor route between the two cargo networks. This run verifies safe rejection and native UI/backend wiring, not a successful transfer.

## Per-pane scope selector (2026-09-05)

Implemented independent native dropdowns below the search bars. Each pane now renders one selected whole construct/network rather than stacking indistinguishable construct panels. The accessed construct sorts first, its accessed inventory network defaults first, and duplicate ship names have deterministic ship numbers with full IDs in tooltips. Search/filter changes and suit/grid switches retain the selected scope ID. The viewport is shortened by the selector row instead of overlapping existing controls.

- Live at 3440×1440: accessed network showed 24 cargo containers, while Whole construct showed 27. Selecting the other ship changed only that pane; both scope labels remained visible above their respective panels.
- Native keyboard selection, expanded-menu rendering, live refresh retention, search/filter retention, suit/grid switching, and three complete vanilla/unified fallback cycles passed 47 assertions. Repeated the same 47 checks successfully after the final port-filter build/restart.
- Exclude portless inventories using the live endpoint's model-port count. Flight seats that implement an endpoint with zero ports no longer appear as singleton networks. Disconnected reactors, cargo and other blocks with real ports remain selectable. Whole-construct projection is unchanged. The fixture's dropdown decreased from 51 to 39 entries; inspected the filtered menu and selected the other ship's Whole construct entry successfully.
- Opened a different cargo hatch after restart; the selected network label used the new hatch name (Small Cargo Container 01), rather than an arbitrary member. Original-world docking setup retained; no item transfer, rebalance, or loadout job was issued in this UI run.
- Debug and Release builds passed with zero warnings/errors; existing core suite and `git diff --check` passed. No new repository regression suite was added. Final game remains fullscreen and unpaused.

Network membership is cached between content refreshes and reconsidered by the structural poll only for sessions that request network views. Overlapping reachability groups are merged to avoid assigning one inventory to two networks. Live topology mutation, the single-construct/single-network no-row case, and opening from the other mechanical construct still require explicit fixture checks; do not treat these UI results as certification of those paths or successful cross-dock movement.

Screenshots: `Screenshots/20260905_0029_scopes.png`, `20260905_0031_whole_construct.png`, `20260905_0041_filtered_menu.png`, `20260905_0043_filtered_menu_mid2.png`, and `20260905_0044_filtered_other_ship.png` under the se-remote skill folder. Temporary live-check driver: `/tmp/se_scope_ui_verify.py`.

## Component-target panel and assembler catalog (2026-09-05)

Live on the NewTest Magnetar server, accessing Static Grid 3889, with the client devfolder build at 3440×1440:

- Replaced the one-row table with a 12-row viewport, widened the component column, and separated the target/blueprint editor, maintenance/threshold row and three footer actions. Inspected the expanded blueprint menu and the last table rows; no overlapping controls.
- Component discovery now uses positive component outputs of blueprint classes accepted by actual scoped assemblers, including modded definitions. The live scope lists 23 supported components; searching `plush` returns zero rows. There is no plushie blacklist: a supported mod recipe can legitimately add that component. Unsupported saved targets remain stored, not executable while unsupported.
- Search, native X clear, table scrolling, automatic first-row/editor initialization, recipe population, save-selection/search retention, and empty-result disabled editor/save controls checked live. Explicitly load the editor and scroll to the selection after repopulating: the game's first `SetSelectedRow` after `Clear` does not send its selection event.
- Saved a Steel Plate target of 367 (current stock), reopened and verified the quantity and selected recipe. Craft deficits was exercised with this already-satisfied target; this is a no-op check, not proof of deficit production. Reset the temporary target to zero afterward.
- Maintenance and threshold settings survived close/reopen. Restored maintenance off and threshold 0.95. No nonzero production targets were left enabled.
- Client and companion Release builds passed with no warnings/errors; existing core tests and diff whitespace checks passed. No new regression suite. Source changes are local; the running published companion has not been repinned for this UI pass.

Screenshots: `/tmp/unified-targets-layout.png`, `/tmp/unified-targets-dropdown.png`, `/tmp/unified-targets-scroll.png`, and `/tmp/unified-targets-empty.png`. These checks cover the loaded world, not every mod pack, gamepad input, or alternate UI scale. The catalog uses native capabilities rather than hardcoded vanilla subtype lists; separate restricted/modded-assembler fixtures remain in the broader matrix.

The earlier search-X report was corrected by the user: compositor/window geometry misdirected clicks. The speculative search-handler patch was removed; vanilla handles search clearing. KWin fullscreen was restored without changing the game's configured resolution.

## Remaining verification

- Successful unified-to-unified transfers between distinct mechanical constructs remain unverified: the supplied docked pair has no usable conveyor route for the tested cargo (also rejected by vanilla). The previous se-remote slot-input blocker is resolved.
- Unmodified multiplayer, controller hardware, dock/undock during pending work, new-block membership changes, modded refinery outputs/fuels, and the broader matrix in `CLIENT_PLUGIN_TEST_MATRIX.md` are not certified by this run.

Screenshots are under `CometWorks/se-remote/skills/se-remote/Screenshots`; physical inventory snapshots for these runs are in `/tmp/se-e2e-*.sbs` and `/tmp/se-slot-*.sbs`. Those temporary artifacts are not repository fixtures.
Docked follow-up evidence is in `/tmp/se-docked*.png` and `/tmp/se-dock-*.sbs`.
