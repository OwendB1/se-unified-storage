using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Inventory;
using ClientPlugin.Profiles;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Cube;
using VRage;
using VRage.Game;

namespace ClientPlugin.Automation;

public sealed class ComponentTargetStatus
{
    public MyDefinitionId ComponentId { get; set; }
    public MyFixedPoint Target { get; set; }
    public MyFixedPoint Stock { get; set; }
    public MyFixedPoint Queued { get; set; }
    public MyFixedPoint Deficit { get; set; }
    public MyBlueprintDefinitionBase Blueprint { get; set; }
    public IReadOnlyList<MyBlueprintDefinitionBase> BlueprintChoices { get; set; }
    public IReadOnlyList<MyAssembler> EligibleAssemblers { get; set; }
    public string Status { get; set; }
}

public readonly struct ProductionRequest
{
    public ProductionRequest(
        MyAssembler assembler,
        MyBlueprintDefinitionBase blueprint,
        MyFixedPoint runs,
        MyFixedPoint queuedAtPlan)
    {
        Assembler = assembler;
        Blueprint = blueprint;
        Runs = runs;
        QueuedAtPlan = queuedAtPlan;
    }

    public MyAssembler Assembler { get; }
    public MyBlueprintDefinitionBase Blueprint { get; }
    public MyFixedPoint Runs { get; }
    public MyFixedPoint QueuedAtPlan { get; }
}

public static class ComponentTargetEngine
{
    private static readonly Dictionary<string, IReadOnlyList<MyBlueprintDefinitionBase>> BlueprintCache = new();

    public static IReadOnlyList<ComponentTargetStatus> Evaluate(
        MechanicalInventoryScope scope,
        ScopeProfile profile,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags = null)
    {
        if (scope == null)
            throw new ArgumentNullException(nameof(scope));
        if (profile == null)
            throw new ArgumentNullException(nameof(profile));
        getFlags ??= _ => InventoryManagementFlags.None;
        var assemblers = scope.Inventories.Select(descriptor => descriptor.Owner)
            .OfType<MyAssembler>().Distinct().ToArray();
        var allBlueprints = GetBlueprints(assemblers)
            .Where(blueprint => assemblers.Any(assembler => assembler.CanUseBlueprint(blueprint)))
            .ToArray();
        // Discover physical outputs from actual assembler capabilities, not the global
        // item catalog. Loot-only items remain excluded, including modded definitions.
        var componentIds = allBlueprints.SelectMany(blueprint => blueprint.Results)
            .Where(output => output.Amount > MyFixedPoint.Zero &&
                MyDefinitionManager.Static.TryGetPhysicalItemDefinition(output.Id, out _))
            .Select(output => output.Id)
            .Distinct()
            .OrderBy(DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(id => id.ToString(), StringComparer.Ordinal)
            .ToArray();
        var targets = ParseTargets(profile.ComponentTargets);
        var stock = CountStock(scope, getFlags);
        var queued = CountQueued(assemblers);
        var result = new List<ComponentTargetStatus>();
        foreach (var componentId in componentIds)
        {
            var choices = allBlueprints.Where(blueprint =>
                    blueprint.Results.Any(output => output.Id == componentId && output.Amount > MyFixedPoint.Zero))
                .OrderByDescending(blueprint => blueprint.IsPrimary)
                .ThenBy(blueprint => blueprint.Results.Length == 1 ? 0 : 1)
                .ThenByDescending(blueprint => blueprint.Priority)
                .ThenBy(blueprint => blueprint.Id.ToString(), StringComparer.Ordinal)
                .ToArray();
            var targetRecord = profile.ComponentTargets.FirstOrDefault(candidate =>
                string.Equals(candidate.DefinitionId, componentId.ToString(), StringComparison.Ordinal));
            var selected = SelectBlueprint(componentId, choices, targetRecord?.BlueprintDefinitionId);
            var eligible = selected == null
                ? Array.Empty<MyAssembler>()
                : assemblers.Where(assembler => IsEligible(assembler, selected, scope, getFlags))
                    .OrderBy(EstimatedQueueSeconds)
                    .ThenBy(assembler => assembler.EntityId)
                    .ToArray();
            var target = targets.TryGetValue(componentId, out var targetAmount)
                ? MyFixedPoint.Floor(targetAmount)
                : MyFixedPoint.Zero;
            var stockAmount = stock.TryGetValue(componentId, out var value) ? value : MyFixedPoint.Zero;
            var queuedAmount = queued.TryGetValue(componentId, out value) ? value : MyFixedPoint.Zero;
            var deficit = MyFixedPoint.Max(target - stockAmount - queuedAmount, MyFixedPoint.Zero);
            result.Add(new ComponentTargetStatus
            {
                ComponentId = componentId,
                Target = target,
                Stock = stockAmount,
                Queued = queuedAmount,
                Deficit = deficit,
                Blueprint = selected,
                BlueprintChoices = choices,
                EligibleAssemblers = eligible,
                Status = target <= MyFixedPoint.Zero
                    ? "Target off"
                    : selected == null
                        ? choices.Length == 0 ? "No usable blueprint" : "Choose blueprint"
                        : eligible.Length == 0
                            ? "No eligible assembler: " + string.Join(", ", assemblers.Where(assembler => assembler.CanUseBlueprint(selected))
                                .Select(assembler => AssemblerStatus(assembler, scope, getFlags)).Distinct())
                            : deficit > MyFixedPoint.Zero ? QueueStatus(eligible) : "On target"
            });
        }
        return result;
    }

    private static IReadOnlyList<MyBlueprintDefinitionBase> GetBlueprints(IReadOnlyList<MyAssembler> assemblers)
    {
        var definitions = assemblers.GroupBy(assembler => assembler.BlockDefinition.Id)
            .OrderBy(group => group.Key.ToString(), StringComparer.Ordinal)
            .ToArray();
        var key = string.Join("|", definitions.Select(group => group.Key.ToString()));
        if (BlueprintCache.TryGetValue(key, out var cached))
            return cached;
        cached = definitions.SelectMany(group =>
                ((MyProductionBlockDefinition)group.First().BlockDefinition).BlueprintClasses)
            .SelectMany(blueprintClass => blueprintClass)
            .Distinct()
            .Where(blueprint => blueprint.Results.Any(result => result.Amount > MyFixedPoint.Zero &&
                MyDefinitionManager.Static.TryGetPhysicalItemDefinition(result.Id, out _)))
            .ToArray();
        BlueprintCache[key] = cached;
        return cached;
    }

    public static IReadOnlyList<ProductionRequest> PlanDeficits(IEnumerable<ComponentTargetStatus> statuses)
    {
        var orderedStatuses = statuses?.ToArray() ?? Array.Empty<ComponentTargetStatus>();
        var requests = new List<ProductionRequest>();
        var snapshots = orderedStatuses.Select(status => new ProductionTargetCore
        {
            Item = status.ComponentId.ToString(), Deficit = (decimal)status.Deficit,
            Outputs = status.Blueprint?.Results.GroupBy(output => output.Id.ToString())
                .ToDictionary(group => group.Key, group => group.Sum(output => (decimal)output.Amount)),
            Assemblers = status.EligibleAssemblers.Select(assembler => (assembler.EntityId, EstimatedQueueSeconds(assembler),
                status.Blueprint.BaseProductionTimeInSeconds / ProductionSpeed(assembler))).ToArray()
        }).ToArray();
        foreach (var addition in AutomationPlannerCore.Production(snapshots))
        {
            var status = orderedStatuses[addition.TargetIndex];
            var runs = (MyFixedPoint)addition.Runs;
            var assembler = status.EligibleAssemblers.First(candidate => candidate.EntityId == addition.Assembler);
            var queuedAtPlan = assembler.Queue.Where(item => ReferenceEquals(item.Blueprint, status.Blueprint))
                .Aggregate(MyFixedPoint.Zero, (sum, item) => sum + item.Amount);
            requests.Add(new ProductionRequest(assembler, status.Blueprint, runs, queuedAtPlan));
        }
        return requests;
    }

    private static MyBlueprintDefinitionBase SelectBlueprint(
        MyDefinitionId componentId,
        IReadOnlyList<MyBlueprintDefinitionBase> choices,
        string overrideId)
    {
        var canonical = MyDefinitionManager.Static.TryGetBlueprintDefinitionByResultId(componentId);
        var index = AutomationPlannerCore.Blueprint(overrideId, canonical?.Id.ToString(),
            choices.Select(choice => (choice.Id.ToString(), choice.IsPrimary && choice.Results.Length == 1)).ToArray());
        // With no recipe editor, prefer the game default, then the highest-priority
        // supported recipe in the deterministic ordering above. Keep saved overrides.
        return index < 0 ? choices.FirstOrDefault() : choices[index];
    }

    private static bool IsEligible(
        MyAssembler assembler,
        MyBlueprintDefinitionBase blueprint,
        MechanicalInventoryScope scope,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags)
    {
        if (assembler.Closed || assembler.DisassembleEnabled || assembler.IsSlave ||
            !assembler.UseConveyorSystem || !assembler.CanUseBlueprint(blueprint) ||
            assembler.Queue.Count() >= Sandbox.Game.World.MySession.Static.MaxProductionQueueLength ||
            assembler.CurrentState is MyAssembler.StateEnum.InventoryFull or MyAssembler.StateEnum.MissingItems)
            return false;
        return !scope.Inventories.Where(descriptor => ReferenceEquals(descriptor.Owner, assembler))
            .Any(descriptor => (getFlags(descriptor) & (InventoryManagementFlags.ManualBlock |
                                                        InventoryManagementFlags.ReservedInventory)) != 0);
    }

    private static string QueueStatus(IReadOnlyList<MyAssembler> assemblers)
    {
        var first = assemblers[0];
        return first.CurrentState is MyAssembler.StateEnum.Disabled or MyAssembler.StateEnum.NotWorking or MyAssembler.StateEnum.NotEnoughPower
            ? "Queueable: " + first.CurrentState
            : "Ready";
    }

    private static string AssemblerStatus(MyAssembler assembler, MechanicalInventoryScope scope,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags)
    {
        if (assembler.Closed) return "Removed";
        if (scope.Inventories.Where(member => ReferenceEquals(member.Owner, assembler)).Any(member =>
            (getFlags(member) & (InventoryManagementFlags.ManualBlock | InventoryManagementFlags.ReservedInventory)) != 0))
            return "Manual / reserved";
        if (assembler.DisassembleEnabled) return "Disassembly";
        if (assembler.IsSlave) return "Cooperative";
        if (!assembler.UseConveyorSystem) return "Conveyors off";
        if (assembler.Queue.Count() >= Sandbox.Game.World.MySession.Static.MaxProductionQueueLength) return "Queue full";
        return assembler.CurrentState.ToString();
    }

    private static double ProductionSpeed(MyAssembler assembler) => Math.Max(0.001f,
            Sandbox.Game.World.MySession.Static.AssemblerSpeedMultiplier *
            (((MyAssemblerDefinition)assembler.BlockDefinition).AssemblySpeed +
             (assembler.UpgradeValues.TryGetValue("Productivity", out var productivity) ? productivity : 0f)));

    private static double EstimatedQueueSeconds(MyAssembler assembler)
    {
        var speed = ProductionSpeed(assembler);
        return assembler.Queue.Sum(item => (double)item.Amount *
            item.Blueprint.BaseProductionTimeInSeconds / speed);
    }

    private static Dictionary<MyDefinitionId, MyFixedPoint> CountStock(
        MechanicalInventoryScope scope,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags)
    {
        var result = new Dictionary<MyDefinitionId, MyFixedPoint>();
        foreach (var descriptor in scope.Inventories)
        {
            if ((getFlags(descriptor) & InventoryManagementFlags.ReservedInventory) != 0)
                continue;
            foreach (var item in descriptor.Inventory.GetItems())
            {
                var id = item.Content.GetObjectId();
                result[id] = (result.TryGetValue(id, out var value) ? value : MyFixedPoint.Zero) + item.Amount;
            }
        }
        return result;
    }

    private static Dictionary<MyDefinitionId, MyFixedPoint> CountQueued(IEnumerable<MyAssembler> assemblers)
    {
        var result = new Dictionary<MyDefinitionId, MyFixedPoint>();
        foreach (var queueItem in assemblers.Where(assembler => !assembler.DisassembleEnabled).SelectMany(assembler => assembler.Queue))
        foreach (var output in queueItem.Blueprint.Results.Where(output => output.Amount > MyFixedPoint.Zero))
            result[output.Id] = (result.TryGetValue(output.Id, out var value) ? value : MyFixedPoint.Zero) +
                                output.Amount * queueItem.Amount;
        return result;
    }

    private static Dictionary<MyDefinitionId, MyFixedPoint> ParseTargets(
        IEnumerable<ComponentTargetRecord> records)
    {
        var result = new Dictionary<MyDefinitionId, MyFixedPoint>();
        foreach (var record in records ?? Enumerable.Empty<ComponentTargetRecord>())
            if (MyDefinitionId.TryParse(record.DefinitionId, out var id) && record.Amount > 0)
                result[id] = MyFixedPoint.Floor((MyFixedPoint)record.Amount);
        return result;
    }

    private static string DisplayName(MyDefinitionId id) =>
        MyDefinitionManager.Static.GetPhysicalItemDefinition(id)?.DisplayNameText ?? id.SubtypeName;
}
