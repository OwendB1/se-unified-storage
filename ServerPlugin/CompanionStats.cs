using PluginSdk.Stats;

namespace ServerPlugin;

public sealed class CompanionStats
{
    [Counter("Refinery swaps", OverTime = TimeAggregation.Last)]
    public long RefinerySwaps { get; set; }
    [Counter("Assembler queue additions", OverTime = TimeAggregation.Last)]
    public long QueueAdditions { get; set; }
    [Gauge("Last intent processing time (milliseconds)")]
    public double LastRequestMilliseconds { get; set; }
    [Gauge("Server-owned automation profiles")]
    public int AutomationProfiles { get; set; }
    [Gauge("Active utility jobs")]
    public int UtilityJobs { get; set; }
    [Counter("Authoritative physical transfers", OverTime = TimeAggregation.Last)]
    public long TransferAllocations { get; set; }
    [Counter("Incomplete or uncertain transfer intents", OverTime = TimeAggregation.Last)]
    public long PartialTransfers { get; set; }
    [Counter("Requests completed", OverTime = TimeAggregation.Last)]
    public long Completed { get; set; }
    [Counter("Requests rejected", OverTime = TimeAggregation.Last)]
    public long Rejected { get; set; }
    [Counter("Duplicate requests served without executing", OverTime = TimeAggregation.Last)]
    public long CacheHits { get; set; }
    [Counter("Revision conflicts", OverTime = TimeAggregation.Last)]
    public long RevisionConflicts { get; set; }
    [Gauge("Inbound message backlog")]
    public int QueueDepth { get; set; }
    [Gauge("Cached request results")]
    public int CachedResults { get; set; }
    [Gauge("Persisted ship profiles")]
    public int Profiles { get; set; }
}
