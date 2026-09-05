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

public sealed class RefineryRecipe
{
    public RefineryRecipe(
        MyBlueprintDefinitionBase blueprint,
        IReadOnlyList<MyDefinitionId> compatibleRefineries)
    {
        Blueprint = blueprint;
        CompatibleRefineries = compatibleRefineries;
    }

    public MyBlueprintDefinitionBase Blueprint { get; }
    public IReadOnlyList<MyDefinitionId> CompatibleRefineries { get; }
    public IReadOnlyList<MyBlueprintDefinitionBase.Item> Prerequisites => Blueprint.Prerequisites;
    public IReadOnlyList<MyBlueprintDefinitionBase.Item> Results => Blueprint.Results;
}

public sealed class RefineryPriorityModel
{
    public RefineryPriorityModel(
        IReadOnlyList<RefineryRecipe> recipes,
        IReadOnlyList<MyDefinitionId> orderedInputs,
        IReadOnlyDictionary<MyDefinitionId, int> acceptingRefineryCounts)
    {
        Recipes = recipes;
        OrderedInputs = orderedInputs;
        AcceptingRefineryCounts = acceptingRefineryCounts;
    }

    public IReadOnlyList<RefineryRecipe> Recipes { get; }
    public IReadOnlyList<MyDefinitionId> OrderedInputs { get; }
    public IReadOnlyDictionary<MyDefinitionId, int> AcceptingRefineryCounts { get; }
}

public static class RefineryPriorityEngine
{
    private static readonly Dictionary<string, IReadOnlyList<RefineryRecipe>> RecipeCache = new();

    public static RefineryPriorityModel Build(
        MechanicalInventoryScope scope,
        ScopeProfile profile,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags = null)
    {
        if (scope == null)
            throw new ArgumentNullException(nameof(scope));
        if (profile == null)
            throw new ArgumentNullException(nameof(profile));
        getFlags ??= _ => InventoryManagementFlags.None;
        var refineries = scope.Inventories
            .Select(descriptor => descriptor.Owner)
            .OfType<MyRefinery>()
            .Distinct()
            .Where(refinery => !IsExcludedFromSorting(refinery, scope, getFlags))
            .ToArray();
        var recipes = GetRecipes(refineries);
        var inputs = recipes.SelectMany(recipe => recipe.Prerequisites)
            .Select(item => item.Id)
            .Distinct()
            .ToArray();
        var counts = inputs.ToDictionary(
            input => input,
            input => refineries.Count(refinery =>
                refinery.InputInventory.CheckConstraint(input) &&
                recipes.Any(recipe => recipe.Prerequisites.Any(item => item.Id == input) &&
                                      refinery.CanUseBlueprint(recipe.Blueprint))));

        var automatic = profile.RefineryPriority.Automatic;
        var stock = automatic ? GetScopeStock(scope, getFlags) : null;
        var byId = inputs.ToDictionary(input => input.ToString());
        var ordered = AutomationPlannerCore.OreOrder(inputs.Select(input =>
            (input.ToString(), DisplayName(input), automatic ? Scarcity(input, recipes, stock) : 0d,
                recipes.Where(recipe => recipe.Prerequisites.Any(item => item.Id == input)).Max(recipe => recipe.Blueprint.Priority))),
            automatic ? profile.RefineryPriority.PinnedDefinitionIds : profile.RefineryPriority.ManualDefinitionIds, automatic)
            .Select(id => byId[id]).ToArray();
        return new RefineryPriorityModel(recipes, ordered, counts);
    }

    private static IReadOnlyList<RefineryRecipe> GetRecipes(IReadOnlyList<MyRefinery> refineries)
    {
        var definitions = refineries.GroupBy(refinery => refinery.BlockDefinition.Id)
            .OrderBy(group => group.Key.ToString(), StringComparer.Ordinal)
            .ToArray();
        var key = string.Join("|", definitions.Select(group => group.Key.ToString()));
        if (RecipeCache.TryGetValue(key, out var cached))
            return cached;
        cached = definitions.SelectMany(group =>
                ((MyProductionBlockDefinition)group.First().BlockDefinition).BlueprintClasses)
            .SelectMany(blueprintClass => blueprintClass)
            .Distinct()
            .Select(blueprint => new RefineryRecipe(
                blueprint,
                definitions.Where(group => group.First().CanUseBlueprint(blueprint))
                    .Select(group => group.Key)
                    .ToArray()))
            .Where(recipe => recipe.CompatibleRefineries.Count > 0 &&
                             recipe.Prerequisites.Count > 0 && recipe.Results.Count > 0)
            .ToArray();
        RecipeCache[key] = cached;
        return cached;
    }

    public static IReadOnlyList<MyDefinitionId> ForRefinery(
        RefineryPriorityModel model,
        MyRefinery refinery) =>
        model.OrderedInputs.Where(input => refinery.InputInventory.CheckConstraint(input) &&
            model.Recipes.Any(recipe => recipe.Prerequisites.Any(item => item.Id == input) &&
                                        refinery.CanUseBlueprint(recipe.Blueprint))).ToArray();

    private static double Scarcity(
        MyDefinitionId input,
        IEnumerable<RefineryRecipe> recipes,
        IReadOnlyDictionary<MyDefinitionId, MyFixedPoint> stock)
    {
        var scores = from recipe in recipes
            from prerequisite in recipe.Prerequisites
            where prerequisite.Id == input && prerequisite.Amount > MyFixedPoint.Zero
            from result in recipe.Results
            where result.Amount > MyFixedPoint.Zero
            let output = stock.TryGetValue(result.Id, out var amount) ? amount : MyFixedPoint.Zero
            select (double)output / ((double)result.Amount / (double)prerequisite.Amount);
        return scores.DefaultIfEmpty(double.MaxValue).Min();
    }

    private static IReadOnlyDictionary<MyDefinitionId, MyFixedPoint> GetScopeStock(
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
                result[id] = (result.TryGetValue(id, out var current) ? current : MyFixedPoint.Zero) + item.Amount;
            }
        }
        return result;
    }

    public static bool IsExcludedFromSorting(
        MyRefinery refinery,
        MechanicalInventoryScope scope,
        Func<InventoryDescriptor, InventoryManagementFlags> getFlags) =>
        scope.Inventories.Where(descriptor => ReferenceEquals(descriptor.Owner, refinery))
            .Any(descriptor => (getFlags(descriptor) & InventoryManagementFlags.ManualBlock) != 0 ||
                               (ReferenceEquals(descriptor.Inventory, refinery.InputInventory) &&
                                (getFlags(descriptor) & InventoryManagementFlags.ReservedInventory) != 0));

    private static IEnumerable<MyDefinitionId> ParseIds(IEnumerable<string> values)
    {
        foreach (var value in values ?? Enumerable.Empty<string>())
            if (MyDefinitionId.TryParse(value, out var id))
                yield return id;
    }

    private static string DisplayName(MyDefinitionId id) =>
        MyDefinitionManager.Static.GetPhysicalItemDefinition(id)?.DisplayNameText ?? id.SubtypeName;
}
