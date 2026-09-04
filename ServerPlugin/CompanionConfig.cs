using PluginSdk.Config;

namespace ServerPlugin;

public sealed class CompanionConfig : PluginConfig
{
    [BoolOption("Enable secure companion discovery and requests")]
    public bool Enabled { get; set => SetField(ref field, value); } = true;

    [BoolOption("Persist shared ship settings. Does not enable automation or transfers.")]
    public bool SharedProfiles { get; set => SetField(ref field, value); } = true;

    [BoolOption("Allow owners to publish profiles readable by their faction. Editing remains owner-only.")]
    public bool AllowFactionRead { get; set => SetField(ref field, value); } = true;

    [IntOption(1, 60, "Requests per authenticated player per ten seconds")]
    public int RequestsPerWindow { get; set => SetField(ref field, value); } = 12;

    [IntOption(1, 16, "Maximum queued messages processed per simulation update")]
    public int MessagesPerUpdate { get; set => SetField(ref field, value); } = 2;

    [IntOption(1, 256, "Maximum shared profiles in this world")]
    public int MaxProfiles { get; set => SetField(ref field, value); } = 128;

    [BoolOption("Log request outcomes without inventory contents or settings")]
    public bool LogRequests { get; set => SetField(ref field, value); }
}
