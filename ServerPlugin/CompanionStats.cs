using PluginSdk.Stats;

namespace ServerPlugin;

public sealed class CompanionStats
{
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
