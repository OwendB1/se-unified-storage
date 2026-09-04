# Graph Report - /home/owendb/Documents/GitHub/se-unified-storage  (2026-09-04)

## Corpus Check
- Corpus is ~47,970 words - fits in a single context window. You may not need a graph.

## Summary
- 1004 nodes · 2028 edges · 50 communities (47 shown, 3 thin omitted)
- Extraction: 98% EXTRACTED · 2% INFERRED · 0% AMBIGUOUS · INFERRED: 33 edges (avg confidence: 0.82)
- Token cost: 23,800 input · 9,610 output

## Community Hubs (Navigation)
- Profile Management UI
- Inventory Session Scope
- Plugin Lifecycle Logging
- Unified Terminal Controller
- Transfer Planning Policies
- Terminal Inventory Patches
- IL Preloader Helpers
- Keybind Control Storage
- Component Target Engine
- Client Transfer Architecture
- Harmony Transpiler Helpers
- Inventory Role Projection
- Bottle Refill Coordinator
- Unified Inventory UI
- Inventory Descriptor Factory
- Client Plugin Modules
- Logging Formatting
- Refinery Priority Sorting
- Solution Projects
- Template Setup Script
- Transfer Execution
- Patch Verification
- Inventory Projection Building
- Settings Controls
- Companion Automation Architecture
- Settings Generation
- Configuration Persistence
- Distribution Planner
- Settings Dialog Example
- Project Documentation
- Slider Settings Control
- Dropdown Settings Control
- Settings Screen Layout
- Checkbox Settings Control
- Client Configuration
- Inventory Section Semantics
- Color Settings Control
- Simple Settings Layout
- Button Settings Control
- Textbox Settings Control
- Conveyor Reachability
- Assembler Drain Engine
- Settings Element Interface
- Settings Framework
- Base Settings Layout
- Unstyled Settings Layout
- Server Companion Transport
- Settings Attribute Metadata
- Cleanup Script
- Profile Identity

## God Nodes (most connected - your core abstractions)
1. `UnifiedTerminalController` - 47 edges
2. `InventoryDescriptor` - 46 edges
3. `MechanicalInventorySession` - 34 edges
4. `InventoryManagementFlags` - 24 edges
5. `ScopeProfile` - 24 edges
6. `Plugin` - 22 edges
7. `LoadoutScreen` - 22 edges
8. `UnifiedInventoryOwnerControl` - 22 edges
9. `InventorySectionKey` - 21 edges
10. `InventoryProjection` - 21 edges

## Surprising Connections (you probably didn't know these)
- `Projected Ship-Wide Inventory` --semantically_similar_to--> `Unified Cargo Projection`  [INFERRED] [semantically similar]
  README.md → Docs/CLIENT_PLUGIN_PLAN.md
- `Client-Only Operation` --semantically_similar_to--> `Client-Only Architectural Boundary`  [INFERRED] [semantically similar]
  README.md → Docs/CLIENT_PLUGIN_PLAN.md
- `LocalProfileStore` --references--> `IPluginLogger`  [EXTRACTED]
  ClientPlugin/Profiles/LocalProfileStore.cs → Shared/Logging/IPluginLogger.cs
- `Safety Boundary` --rationale_for--> `Vanilla-Equivalent Reachability Gate`  [EXTRACTED]
  README.md → Docs/CLIENT_PLUGIN_PLAN.md
- `Plugin` --references--> `IPluginConfig`  [EXTRACTED]
  ClientPlugin/Plugin.cs → Shared/Config/IPluginConfig.cs

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **Client Projection and Transfer Safety Flow** — docs_client_plugin_plan_unified_cargo_projection, docs_client_plugin_plan_transfer_planner, docs_client_plugin_plan_vanilla_equivalent_reachability_gate, docs_client_plugin_plan_bounded_acknowledgement_execution [EXTRACTED 1.00]
- **Cross-Runtime Planning Consistency** — docs_client_plugin_plan_placement_policies, docs_client_plugin_plan_refinery_priority_engine, docs_client_plugin_plan_component_target_engine, docs_server_companion_plan_shared_planning_logic, docs_server_companion_plan_server_owned_automation [EXTRACTED 1.00]
- **Design Verification Evidence** — docs_client_plugin_plan_review_review_of_client_plugin_plan, docs_client_plugin_test_matrix_unified_storage_client_verification_matrix, docs_server_companion_plan_review_review_of_server_companion_plan [INFERRED 0.85]
- **Config Field Editors** — docs_configdialogexample_toggle_editor, docs_configdialogexample_integer_slider, docs_configdialogexample_number_slider, docs_configdialogexample_text_editor, docs_configdialogexample_dropdown_editor, docs_configdialogexample_color_editor, docs_configdialogexample_color_with_alpha_editor, docs_configdialogexample_keybind_editor [EXTRACTED 1.00]

## Communities (50 total, 3 thin omitted)

### Community 0 - "Profile Management UI"
Cohesion: 0.06
Nodes (34): ScopeAutomation, DateTime, ComponentTargetRecord, InventoryManagementRecord, LoadoutRecord, LoadoutTargetKind, LocalProfileDocument, LocalProfileStore (+26 more)

### Community 1 - "Inventory Session Scope"
Cohesion: 0.06
Nodes (38): LocalAutomationService, Dictionary, long, MyCubeGrid, string, ProjectionOrdering, RefineryPriorityEngine, RefineryPriorityModel (+30 more)

### Community 2 - "Plugin Lifecycle Logging"
Cohesion: 0.06
Nodes (29): Plugin, bool, MethodImpl, string, Shared.Plugin, Shared.Config, Shared.Logging, ServerPlugin (+21 more)

### Community 3 - "Unified Terminal Controller"
Cohesion: 0.07
Nodes (28): Pane, UnifiedTerminalController, Action, bool, DateTime, Dictionary, EventArgs, Func (+20 more)

### Community 4 - "Transfer Planning Policies"
Cohesion: 0.11
Nodes (33): LoadoutEngine, DistributionPolicy, Func, IReadOnlyList, MyDefinitionId, MyFixedPoint, InventoryManagementFlags, QueuedOperation (+25 more)

### Community 5 - "Terminal Inventory Patches"
Cohesion: 0.07
Nodes (25): State, TerminalInventoryBridge, TerminalInventoryClosePatch, TerminalInventoryGetDefaultFocusPatch, TerminalInventoryHandleInputPatch, TerminalInventoryInitPatch, TerminalInventoryRefreshPatch, TerminalInventorySetSearchPatch (+17 more)

### Community 6 - "IL Preloader Helpers"
Cohesion: 0.08
Nodes (20): Collection, System.Runtime.CompilerServices, Shared.Tools, Exception, FieldReference, MethodDefinition, MethodReference, CodeInstruction (+12 more)

### Community 7 - "Keybind Control Storage"
Cohesion: 0.08
Nodes (18): ControlButtonData, KeybindAttribute, Action, Func, List, MyGuiControlButton, string, Type (+10 more)

### Community 8 - "Component Target Engine"
Cohesion: 0.14
Nodes (18): ComponentTargetEngine, ComponentTargetStatus, ProductionRequest, Dictionary, Func, IEnumerable, IReadOnlyList, MyAssembler (+10 more)

### Community 9 - "Client Transfer Architecture"
Cohesion: 0.07
Nodes (33): Bounded Acknowledgement-Driven Execution, Client-Only Architectural Boundary, Explicit Utility Actions, Machine Loadouts, Management Exclusions, Mechanical Grid Group Scope, Placement Policies, Projected Inventory UI (+25 more)

### Community 10 - "Harmony Transpiler Helpers"
Cohesion: 0.18
Nodes (11): FieldInfo, FieldInfoPredicate, Label, OpcodePredicate, CodeInstruction, CodeInstructionPredicate, IEnumerable, List (+3 more)

### Community 11 - "Inventory Role Projection"
Cohesion: 0.14
Nodes (15): InventoryRoleKind, InventoryRoleProjection, InventoryStackReference, ProjectedInventoryStack, RoleBucket, IReadOnlyList, List, MyDefinitionId (+7 more)

### Community 12 - "Bottle Refill Coordinator"
Cohesion: 0.14
Nodes (13): BottleStage, BottleStageState, BottleRefillCoordinator, BottleStage, DateTime, Func, MethodInfo, MyCubeBlock (+5 more)

### Community 13 - "Unified Inventory UI"
Cohesion: 0.13
Nodes (11): ProjectedGridContext, UnifiedInventoryOwnerControl, Action, EventArgs, float, Func, List, MyGuiControlButton (+3 more)

### Community 14 - "Inventory Descriptor Factory"
Cohesion: 0.21
Nodes (12): InventoryDescriptor, InventoryDescriptorFactory, InventoryDiscoverySource, InventoryRoleDescriptor, Func, IReadOnlyList, MyCubeBlock, MyDefinitionId (+4 more)

### Community 15 - "Client Plugin Modules"
Cohesion: 0.25
Nodes (7): BottleStageState, PaneFilter, ClientPlugin.UI, ClientPlugin.Automation, ClientPlugin.Inventory, ClientPlugin.Profiles, ClientPlugin.Transfers

### Community 16 - "Logging Formatting"
Cohesion: 0.22
Nodes (10): Exception, int, MethodImpl, string, LogFormatter, Exception, MethodImpl, PluginLogger (+2 more)

### Community 17 - "Refinery Priority Sorting"
Cohesion: 0.16
Nodes (12): RefinerySortExecutor, SortJob, DateTime, Func, IEnumerable, IReadOnlyDictionary, IReadOnlyList, MyDefinitionId (+4 more)

### Community 18 - "Solution Projects"
Cohesion: 0.11
Nodes (16): ClientPlugin, net10.0, net48, Lib.Harmony (2.4.2), Mono.Cecil (0.11.6), Microsoft.NET.Sdk, Shared, ServerPlugin (+8 more)

### Community 19 - "Template Setup Script"
Cohesion: 0.21
Nodes (16): Element, _generate_guid(), _get_install_locations(), _get_linux_steam_path(), _get_steam_path(), _get_windows_steam_path(), _input_plugin_name(), _input_question() (+8 more)

### Community 20 - "Transfer Execution"
Cohesion: 0.23
Nodes (9): InFlightTransfer, TransferExecutor, TransferOperationResult, TransferOperationStatus, DateTime, MyFixedPoint, Queue, InFlightTransfer (+1 more)

### Community 21 - "Patch Verification"
Cohesion: 0.19
Nodes (10): Assembly, ConstructorInfo, MethodInfo, string, CodeChange, IEnumerable, MethodInfo, string (+2 more)

### Community 22 - "Inventory Projection Building"
Cohesion: 0.30
Nodes (8): InventoryProjection, InventoryProjectionView, ProjectionViewBuilder, Func, IMyConveyorEndpointBlock, IReadOnlyList, MyCubeBlock, InventoryScopeMode

### Community 23 - "Settings Controls"
Cohesion: 0.15
Nodes (11): Control, float, MyGuiControlBase, Vector2, SeparatorAttribute, Action, Func, List (+3 more)

### Community 24 - "Companion Automation Architecture"
Cohesion: 0.15
Nodes (14): Component-Target Engine, Definition-Driven Discovery, Inventory Descriptor, Refinery Ore-Priority Engine, Content-Based Queue Acknowledgement, Swap-Based Refinery Sorting, Component Target Tests, Definition and Grouping Tests (+6 more)

### Community 25 - "Settings Generation"
Cohesion: 0.24
Nodes (6): SettingsGenerator, List, MethodInfo, MyGuiControlBase, Type, Delegate

### Community 26 - "Configuration Persistence"
Cohesion: 0.18
Nodes (5): PropertyChangedEventArgs, ConfigStorage, string, Config, ClientPlugin.Settings

### Community 27 - "Distribution Planner"
Cohesion: 0.38
Nodes (6): DistributionAllocationCore, DistributionCandidateCore, DistributionPlannerCore, State, IEnumerable, IReadOnlyList

### Community 28 - "Settings Dialog Example"
Cohesion: 0.17
Nodes (12): Action Button, Color Editor, Color with Alpha Editor, Config Demo Dialog, Dropdown Editor, Integer Slider, Keybind Editor, More settings (+4 more)

### Community 29 - "Project Documentation"
Cohesion: 0.22
Nodes (11): se-dev Skill, Space Engineers Plugin Guidance, ISY's Inventory Manager, Review of CLIENT_PLUGIN_PLAN.md, Unified Ship Storage Client Plugin Plan, Unified Storage Client Verification Matrix, Optional Server Companion Plan, Review of SERVER_COMPANION_PLAN.md (+3 more)

### Community 30 - "Slider Settings Control"
Cohesion: 0.20
Nodes (9): SliderAttribute, SliderType, Action, float, Func, List, string, Type (+1 more)

### Community 31 - "Dropdown Settings Control"
Cohesion: 0.24
Nodes (7): DropdownAttribute, Action, Func, int, List, string, Type

### Community 32 - "Settings Screen Layout"
Cohesion: 0.22
Nodes (5): SettingsScreen, Func, string, Vector2, MyGuiScreenBase

### Community 33 - "Checkbox Settings Control"
Cohesion: 0.25
Nodes (7): Attribute, CheckboxAttribute, Action, Func, List, string, Type

### Community 34 - "Client Configuration"
Cohesion: 0.31
Nodes (7): Config, DistributionPolicy, InventoryScopeMode, bool, int, string, ClientPlugin

### Community 35 - "Inventory Section Semantics"
Cohesion: 0.28
Nodes (3): InventorySectionKey, InventorySectionKind, IEquatable

### Community 36 - "Color Settings Control"
Cohesion: 0.25
Nodes (8): ColorAttribute, Action, bool, Color, Func, List, string, Type

### Community 37 - "Simple Settings Layout"
Cohesion: 0.22
Nodes (7): Simple, float, List, MyGuiControlBase, Vector2, MyGuiControlParent, MyGuiControlScrollablePanel

### Community 38 - "Button Settings Control"
Cohesion: 0.29
Nodes (6): ButtonAttribute, Action, Func, List, string, Type

### Community 39 - "Textbox Settings Control"
Cohesion: 0.29
Nodes (6): TextboxAttribute, Action, Func, List, string, Type

### Community 40 - "Conveyor Reachability"
Cohesion: 0.36
Nodes (6): ConveyorReachability, IMyConveyorEndpointBlock, List, MyDefinitionId, MyEntity, MyInventory

### Community 41 - "Assembler Drain Engine"
Cohesion: 0.38
Nodes (5): DrainAssemblerEngine, DrainAssemblerOperation, Func, IReadOnlyList, MyAssembler

### Community 42 - "Settings Element Interface"
Cohesion: 0.33
Nodes (5): IElement, Action, Func, List, Type

### Community 44 - "Base Settings Layout"
Cohesion: 0.33
Nodes (5): Layout, Func, List, MyGuiControlBase, Vector2

### Community 45 - "Unstyled Settings Layout"
Cohesion: 0.33
Nodes (4): None, List, MyGuiControlBase, Vector2

### Community 46 - "Server Companion Transport"
Cohesion: 0.33
Nodes (6): Optional Companion Transport Boundary, Capability Handshake, Magnetar Server Companion, Operator Configuration and Telemetry, Vanilla Mod-Message Transport Finding, Secure Mod-Message Transport

### Community 47 - "Settings Attribute Metadata"
Cohesion: 0.50
Nodes (4): AttributeInfo, Action, Func, string

## Knowledge Gaps
- **46 isolated node(s):** `BottleStageState`, `net10.0`, `net48`, `Lib.Harmony (2.4.2)`, `Mono.Cecil (0.11.6)` (+41 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **3 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `Plugin` connect `Plugin Lifecycle Logging` to `Profile Management UI`, `Inventory Session Scope`, `Component Target Engine`, `Bottle Refill Coordinator`, `Client Plugin Modules`, `Refinery Priority Sorting`, `Transfer Execution`, `Settings Generation`, `Configuration Persistence`?**
  _High betweenness centrality (0.166) - this node is a cross-community bridge._
- **Why does `ClientPlugin.Settings.Elements` connect `Settings Framework` to `Checkbox Settings Control`, `Client Configuration`, `Button Settings Control`, `Keybind Control Storage`, `Textbox Settings Control`, `Settings Element Interface`, `Settings Controls`, `Slider Settings Control`, `Dropdown Settings Control`?**
  _High betweenness centrality (0.125) - this node is a cross-community bridge._
- **Why does `SettingsGenerator` connect `Settings Generation` to `Settings Screen Layout`, `Plugin Lifecycle Logging`, `Settings Framework`, `Base Settings Layout`, `Settings Attribute Metadata`?**
  _High betweenness centrality (0.097) - this node is a cross-community bridge._
- **What connects `BottleStageState`, `net10.0`, `net48` to the rest of the system?**
  _60 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Profile Management UI` be split into smaller, more focused modules?**
  _Cohesion score 0.05859969558599695 - nodes in this community are weakly interconnected._
- **Should `Inventory Session Scope` be split into smaller, more focused modules?**
  _Cohesion score 0.055130784708249496 - nodes in this community are weakly interconnected._
- **Should `Plugin Lifecycle Logging` be split into smaller, more focused modules?**
  _Cohesion score 0.06187202538339503 - nodes in this community are weakly interconnected._