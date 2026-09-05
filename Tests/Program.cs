using ClientPlugin.Transfers;

WeaponCoreCompatibilityChecks.Run();

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
