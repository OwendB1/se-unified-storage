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

    [BoolOption("Enable experimental authoritative transfers after validating this server's permissions and conveyor rules")]
    public bool Transfers { get; set => SetField(ref field, value); }

    [BoolOption("Allow server refinery sorting; unattended execution also requires owner opt-in on each shared profile")]
    public bool RefineryAutomation { get; set => SetField(ref field, value); }

    [BoolOption("Allow server add-only component production; unattended execution requires profile owner opt-in")]
    public bool ComponentAutomation { get; set => SetField(ref field, value); }

    [BoolOption("Allow server loadouts; unattended execution requires profile owner opt-in")]
    public bool LoadoutAutomation { get; set => SetField(ref field, value); }

    [BoolOption("Allow explicit server utility jobs. Never runs automatically")]
    public bool UtilityJobs { get; set => SetField(ref field, value); }

    [BoolOption("Allow bottle refill jobs when utility jobs are enabled")]
    public bool BottleRefillJobs { get; set => SetField(ref field, value); } = true;

    [BoolOption("Allow idle assembler drain jobs when utility jobs are enabled")]
    public bool AssemblerDrainJobs { get; set => SetField(ref field, value); } = true;

    [BoolOption("Allow offline automation to use faction-shared blocks; otherwise require principal ownership")]
    public bool AutomationFactionAccess { get; set => SetField(ref field, value); }

    [IntOption(0, 3650, "Days to retain continuously missing profile anchors; zero retains forever. Deletion writes a recovery archive")]
    public int OrphanRetentionDays { get; set => SetField(ref field, value); }

    [IntOption(1, 16, "Maximum mutations per automation pass; one ship is serviced per update")]
    public int AutomationMutations { get; set => SetField(ref field, value); } = 4;

    [IntOption(2, 60, "Minimum seconds between dirty automation passes; topology audit is at least 15 seconds")]
    public int AutomationIntervalSeconds { get; set => SetField(ref field, value); } = 5;

    [BoolOption("Allow Existing Stack First deposits")]
    public bool ExistingStackFirst { get; set => SetField(ref field, value); } = true;

    [BoolOption("Allow Fill First deposits")]
    public bool FillFirst { get; set => SetField(ref field, value); } = true;

    [BoolOption("Allow Even By Item deposits")]
    public bool EvenByItem { get; set => SetField(ref field, value); } = true;

    [IntOption(1, 128, "Maximum physical transfers in one intent")]
    public int AllocationsPerIntent { get; set => SetField(ref field, value); } = 32;

    [IntOption(1, 128, "Maximum candidate-pair conveyor checks in one intent; each uses three native checks")]
    public int TransferPairsPerIntent { get; set => SetField(ref field, value); } = 32;

    [IntOption(1, 1024, "Maximum inventory members scanned per selected ship; larger requests fail without mutation")]
    public int InventoriesPerIntent { get; set => SetField(ref field, value); } = 256;

    [IntOption(1, 60, "Requests per authenticated player per ten seconds")]
    public int RequestsPerWindow { get; set => SetField(ref field, value); } = 12;

    [IntOption(1, 16, "Maximum queued messages processed per simulation update")]
    public int MessagesPerUpdate { get; set => SetField(ref field, value); } = 2;

    [IntOption(1, 256, "Maximum shared profiles in this world")]
    public int MaxProfiles { get; set => SetField(ref field, value); } = 128;

    [BoolOption("Log request outcomes without inventory contents or settings")]
    public bool LogRequests { get; set => SetField(ref field, value); }
}
