using ClientPlugin.Transfers;

WeaponCoreCompatibilityChecks.Run();
ListSelectionChecks.Run();
InventoryGroupChecks.Run();

var roundShares = DistributionPlannerCore.PreferWholeUnits(10_000_000,
    Enumerable.Range(0, 3).Select(i => new DistributionCandidateCore(i, 0, 20_000_000)), 1, true);
True(roundShares.Select(a => a.Amount).SequenceEqual(new long[] { 4_000_000, 3_000_000, 3_000_000 }),
    "even fractional resources prefer whole shares");
var fractionalShares = DistributionPlannerCore.PreferWholeUnits(10_500_000,
    Enumerable.Range(0, 3).Select(i => new DistributionCandidateCore(i, 0, 20_000_000)), 1, true);
Equal(10_500_000, fractionalShares.Sum(a => a.Amount), "explicit fractional amount is conserved");
Equal(1, fractionalShares.Count(a => a.Amount % 1_000_000 != 0), "only one share needs a fractional tail");
var tightShares = DistributionPlannerCore.PreferWholeUnits(1_000_000, new[]
{
    new DistributionCandidateCore(0, 0, 400_000), new DistributionCandidateCore(1, 0, 600_000)
}, 1, false);
Equal(1_000_000, tightShares.Sum(a => a.Amount), "fractional capacity remains usable for explicit transfers");

var rateBudget = new OperationRateBudget();
True(rateBudget.Available(0, 1), "first transfer can start immediately");
rateBudget.Consume();
True(!rateBudget.Available(0.5, 1), "one per second waits across frames");
True(!rateBudget.Available(0.999, 1), "one per second does not send early");
True(rateBudget.Available(1, 1), "one per second refills at the boundary");
rateBudget.Consume();
True(rateBudget.Available(120, 1), "idle budget allows the next transfer");
rateBudget.Consume();
True(!rateBudget.Available(120, 1), "idle time cannot accumulate a burst");
True(rateBudget.Available(121, 480), "higher budgets refill");
for (var i = 0; i < 8; i++)
{
    True(rateBudget.Available(121, 480), "validation budget permits a bounded frame batch");
    rateBudget.Consume();
}
True(!rateBudget.Available(121, 480), "validation burst is capped at one nominal frame");

var crafting = ClientPlugin.Automation.AutomationPlannerCore.Production(new[]
{
    new ClientPlugin.Automation.ProductionTargetCore
    {
        Item = "MyObjectBuilder_AmmoMagazine/ModMagazine", Deficit = 7,
        Outputs = new Dictionary<string, decimal> { ["MyObjectBuilder_AmmoMagazine/ModMagazine"] = 3, ["MyObjectBuilder_Ingot/ModIngot"] = 0.5m },
        Assemblers = new[] { (1L, 0d, 1d) }
    },
    new ClientPlugin.Automation.ProductionTargetCore
    {
        Item = "MyObjectBuilder_Ingot/ModIngot", Deficit = 1,
        Outputs = new Dictionary<string, decimal> { ["MyObjectBuilder_Ingot/ModIngot"] = 0.5m },
        Assemblers = new[] { (1L, 0d, 1d) }
    },
    new ClientPlugin.Automation.ProductionTargetCore
    {
        Item = "MyObjectBuilder_PhysicalGunObject/Welder2Item", Deficit = 2,
        Outputs = new Dictionary<string, decimal> { ["MyObjectBuilder_PhysicalGunObject/Welder2Item"] = 1 },
        Assemblers = new[] { (1L, 0d, 1d) }
    }
});
True(crafting.Count == 2 && crafting[0].Runs == 3 && crafting[1].TargetIndex == 2 && crafting[1].Runs == 2,
    "crafting supports ammo batches and tool tiers while crediting fractional coproducts");

True(ClientPlugin.UI.DefinitionLabels.SingleLine("\r\nBulletproof Glass\r\nBuild time: 0.5s\nRequires:\n5x Silicon", "fallback") == "Bulletproof Glass", "recipe dropdown uses one line");
True(ClientPlugin.UI.DefinitionLabels.SingleLine("\u2028\t\u2029", "fallback") == "fallback", "empty recipe title falls back");
True(ClientPlugin.UI.DefinitionLabels.Item("Welder", "MyObjectBuilder_PhysicalGunObject", "Welder2Item") == "Welder (Tier 2)", "tool tier remains selectable with shared names");
True(ClientPlugin.UI.DefinitionLabels.Item("Welder", "MyObjectBuilder_PhysicalGunObject", "ModWelder") == "Welder [ModWelder]", "unknown mod quality retains subtype");

static void Equal(long expected, long actual, string message)
{
    if (expected != actual)
        throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
}

static void True(bool value, string message)
{
    if (!value)
        throw new InvalidOperationException(message);
}

static long Amount(IReadOnlyList<DistributionAllocationCore> allocations, long key) =>
    allocations.FirstOrDefault(allocation => allocation.Key == key).Amount;

var greedy = DistributionPlannerCore.Greedy(900,
    new[]
    {
        new DistributionCandidateCore(0, 100, 300),
        new DistributionCandidateCore(1, 400, 600),
        new DistributionCandidateCore(2, 0, 800)
    }, 1);
Equal(300, Amount(greedy, 0), "greedy first destination");
Equal(600, Amount(greedy, 1), "greedy second destination");

var even = DistributionPlannerCore.Even(900,
    new[]
    {
        new DistributionCandidateCore(0, 100, 900),
        new DistributionCandidateCore(1, 400, 900),
        new DistributionCandidateCore(2, 0, 900)
    }, 1);
Equal(367, Amount(even, 0), "even allocation A");
Equal(67, Amount(even, 1), "even allocation B");
Equal(466, Amount(even, 2), "even allocation C");

var capped = DistributionPlannerCore.Even(12,
    new[]
    {
        new DistributionCandidateCore(0, 0, 2),
        new DistributionCandidateCore(1, 0, 20)
    }, 1);
Equal(2, Amount(capped, 0), "capacity-limited allocation");
Equal(10, Amount(capped, 1), "capacity redistribution");

var integral = DistributionPlannerCore.Even(9_500_000,
    new[]
    {
        new DistributionCandidateCore(0, 0, 20_000_000),
        new DistributionCandidateCore(1, 0, 20_000_000)
    }, 1_000_000);
Equal(9_000_000, integral.Sum(allocation => allocation.Amount), "integral normalization");

var deterministicInput = new[]
{
    new DistributionCandidateCore(9, 200, 550),
    new DistributionCandidateCore(2, 100, 175),
    new DistributionCandidateCore(7, 0, 800)
};
var firstRun = DistributionPlannerCore.Even(1_000, deterministicInput, 1);
var secondRun = DistributionPlannerCore.Even(1_000, deterministicInput.Reverse(), 1);
True(firstRun.Select(value => (value.Key, value.Amount))
        .SequenceEqual(secondRun.Select(value => (value.Key, value.Amount))),
    "even planning must be deterministic regardless of input order");
True(firstRun.Sum(value => value.Amount) == 1_000, "even planning must allocate the complete feasible request");
foreach (var allocation in firstRun)
{
    var candidate = deterministicInput.Single(value => value.Key == allocation.Key);
    True(allocation.Amount >= 0 && allocation.Amount <= candidate.Capacity,
        "even planning exceeded destination capacity");
}

var insufficient = DistributionPlannerCore.Greedy(100,
    new[]
    {
        new DistributionCandidateCore(0, 0, 20),
        new DistributionCandidateCore(1, 0, 30)
    }, 1);
Equal(50, insufficient.Sum(value => value.Amount), "greedy capacity-limited partial plan");

var noCapacity = DistributionPlannerCore.Even(100,
    new[] { new DistributionCandidateCore(0, 0, 0) }, 1);
Equal(0, noCapacity.Sum(value => value.Amount), "zero-capacity plan");

Console.WriteLine("Unified Storage core tests passed.");
