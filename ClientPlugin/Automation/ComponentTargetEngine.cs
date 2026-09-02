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
        var allBlueprints = GetBlueprints(assemblers);
        var componentIds = MyDefinitionManager.Static.GetPhysicalItemDefinitions()
            .Select(definition => definition.Id)
            .Where(id => id.TypeId == typeof(MyObjectBuilder_Component))
            .Concat(allBlueprints.SelectMany(blueprint => blueprint.Results)
                .Where(result => result.Id.TypeId == typeof(MyObjectBuilder_Component))
                .Select(result => result.Id))
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
                    blueprint.Results.Any(output => output.Id == componentId) &&
                    assemblers.Any(assembler => assembler.CanUseBlueprint(blueprint)))
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
                    ? "Disabled"
                    : selected == null
                        ? choices.Length == 0 ? "No usable blueprint" : "Choose blueprint"
                        : eligible.Length == 0
                            ? "No eligible assembler"
                            : deficit > MyFixedPoint.Zero ? "Ready" : "On target"
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
            .Where(blueprint => blueprint.Results.Any(result =>
                result.Id.TypeId == typeof(MyObjectBuilder_Component)))
            .ToArray();
        BlueprintCache[key] = cached;
        return cached;
    }

    public static IReadOnlyList<ProductionRequest> PlanDeficits(IEnumerable<ComponentTargetStatus> statuses)
    {
        var orderedStatuses = statuses?.ToArray() ?? Array.Empty<ComponentTargetStatus>();
        var remaining = orderedStatuses.ToDictionary(status => status.ComponentId, status => status.Deficit);
        var requests = new List<ProductionRequest>();
        foreach (var status in orderedStatuses.Where(status =>
                     remaining[status.ComponentId] > MyFixedPoint.Zero && status.Blueprint != null &&
                     status.EligibleAssemblers.Count > 0))
        {
            var resultAmount = status.Blueprint.Results
                .Where(result => result.Id == status.ComponentId)
                .Aggregate(MyFixedPoint.Zero, (sum, result) => sum + result.Amount);
            if (resultAmount <= MyFixedPoint.Zero)
                continue;
            var runs = MyFixedPoint.Ceiling((MyFixedPoint)
                ((decimal)remaining[status.ComponentId] / (decimal)resultAmount));
            var assembler = status.EligibleAssemblers.OrderBy(EstimatedQueueSeconds)
                .ThenBy(candidate => candidate.EntityId).First();
            var queuedAtPlan = assembler.Queue.Where(item => ReferenceEquals(item.Blueprint, status.Blueprint))
                .Aggregate(MyFixedPoint.Zero, (sum, item) => sum + item.Amount);
            requests.Add(new ProductionRequest(assembler, status.Blueprint, runs, queuedAtPlan));
            foreach (var output in status.Blueprint.Results)
                if (remaining.TryGetValue(output.Id, out var outputDeficit))
                    remaining[output.Id] = MyFixedPoint.Max(
                        outputDeficit - output.Amount * runs,
                        MyFixedPoint.Zero);
        }
        return requests;
    }

    private static MyBlueprintDefinitionBase SelectBlueprint(
        MyDefinitionId componentId,
        IReadOnlyList<MyBlueprintDefinitionBase> choices,
        string overrideId)
    {
        if (!string.IsNullOrEmpty(overrideId) && MyDefinitionId.TryParse(overrideId, out var id))
        {
            var selected = choices.FirstOrDefault(candidate => candidate.Id == id);
            if (selected != null)
                return selected;
        }
        var canonical = MyDefinitionManager.Static.TryGetBlueprintDefinitionByResultId(componentId);
        if (canonical != null && choices.Contains(canonical))
            return canonical;
        var safe = choices.Where(choice => choice.IsPrimary && choice.Results.Length == 1).ToArray();
        return safe.Length == 1 ? safe[0] : choices.Count == 1 ? choices[0] : null;
    }

    private static bool IsEligible(
        MyAssembler assembler,
        MyBlueprintDefinitionBase blueprint,
        MechanicalInventoryScope scope,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags)
    {
        if (assembler.Closed || assembler.DisassembleEnabled || assembler.IsSlave ||
            !assembler.UseConveyorSystem || !assembler.CanUseBlueprint(blueprint) ||
            assembler.CurrentState is MyAssembler.StateEnum.InventoryFull or MyAssembler.StateEnum.MissingItems)
            return false;
        return !scope.Inventories.Where(descriptor => ReferenceEquals(descriptor.Owner, assembler))
            .Any(descriptor => (getFlags(descriptor) & (InventoryManagementFlags.ManualBlock |
                                                        InventoryManagementFlags.ReservedInventory)) != 0);
    }

    private static double EstimatedQueueSeconds(MyAssembler assembler)
    {
        var speed = Math.Max(0.001f,
            Sandbox.Game.World.MySession.Static.AssemblerSpeedMultiplier *
            (((MyAssemblerDefinition)assembler.BlockDefinition).AssemblySpeed +
             assembler.UpgradeValues["Productivity"]));
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
                if (id.TypeId != typeof(MyObjectBuilder_Component))
                    continue;
                result[id] = (result.TryGetValue(id, out var value) ? value : MyFixedPoint.Zero) + item.Amount;
            }
        }
        return result;
    }

    private static Dictionary<MyDefinitionId, MyFixedPoint> CountQueued(IEnumerable<MyAssembler> assemblers)
    {
        var result = new Dictionary<MyDefinitionId, MyFixedPoint>();
        foreach (var queueItem in assemblers.SelectMany(assembler => assembler.Queue))
        foreach (var output in queueItem.Blueprint.Results.Where(output =>
                     output.Id.TypeId == typeof(MyObjectBuilder_Component)))
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
