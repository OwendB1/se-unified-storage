using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using ClientPlugin.Inventory;
using Shared.Logging;
using VRage.FileSystem;
using VRage.Game;

namespace ClientPlugin.Profiles;

[Flags]
public enum InventoryManagementFlags
{
    None = 0,
    ManualBlock = 1,
    ReservedInventory = 2,
    NoUnifiedCargoDestination = 4
}

public sealed class InventoryManagementRecord
{
    public long BlockEntityId { get; set; }
    public int InventoryIndex { get; set; }
    public InventoryManagementFlags Flags { get; set; }
}

public sealed class ComponentTargetRecord
{
    public string DefinitionId { get; set; }
    public string BlueprintDefinitionId { get; set; }
    public decimal Amount { get; set; }
}

public sealed class RefineryPriorityRecord
{
    public bool Automatic { get; set; } = true;
    public bool AutoSortInputs { get; set; } = true;
    public List<string> PinnedDefinitionIds { get; set; } = new();
    public List<string> ManualDefinitionIds { get; set; } = new();
}

public sealed class LoadoutRecord
{
    public LoadoutTargetKind TargetKind { get; set; }
    public long TargetBlockEntityId { get; set; }
    public string TargetBlockDefinitionId { get; set; }
    public InventorySectionKind Section { get; set; }
    public InventoryRoleKind Role { get; set; }
    public string ItemDefinitionId { get; set; }
    public decimal Amount { get; set; }
    public bool PerMember { get; set; } = true;
    public bool Maintain { get; set; }
    public bool IncludeNonWorking { get; set; }
    public DistributionPolicy Policy { get; set; } = DistributionPolicy.EvenByItem;
}

public enum LoadoutTargetKind
{
    Section,
    BlockDefinition,
    Block
}

public sealed class ScopeProfile
{
    public string WorldId { get; set; }
    public long ScopeAnchorEntityId { get; set; }
    public DistributionPolicy Policy { get; set; } = DistributionPolicy.ExistingStackFirst;
    public bool MaintainComponentTargets { get; set; }
    public decimal ComponentStartThreshold { get; set; } = 0.95m;
    public RefineryPriorityRecord RefineryPriority { get; set; } = new();
    public List<ComponentTargetRecord> ComponentTargets { get; set; } = new();
    public List<InventoryManagementRecord> InventoryManagement { get; set; } = new();
    public List<LoadoutRecord> Loadouts { get; set; } = new();

    public InventoryManagementFlags GetFlags(long blockEntityId, int inventoryIndex) =>
        InventoryManagement.FirstOrDefault(record =>
            record.BlockEntityId == blockEntityId && record.InventoryIndex == inventoryIndex)?.Flags ??
        InventoryManagementFlags.None;

    public void SetFlags(long blockEntityId, int inventoryIndex, InventoryManagementFlags flags)
    {
        var record = InventoryManagement.FirstOrDefault(candidate =>
            candidate.BlockEntityId == blockEntityId && candidate.InventoryIndex == inventoryIndex);
        if (flags == InventoryManagementFlags.None)
        {
            if (record != null)
                InventoryManagement.Remove(record);
            return;
        }
        if (record == null)
        {
            record = new InventoryManagementRecord
            {
                BlockEntityId = blockEntityId,
                InventoryIndex = inventoryIndex
            };
            InventoryManagement.Add(record);
        }
        record.Flags = flags;
    }
}

[XmlRoot("UnifiedStorageProfiles")]
public sealed class LocalProfileDocument
{
    public List<ScopeProfile> Profiles { get; set; } = new();
}

public sealed class LocalProfileStore
{
    private readonly IPluginLogger log;
    private readonly string path;
    private LocalProfileDocument document;

    public LocalProfileStore(IPluginLogger log, string path = null)
    {
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.path = path ?? Path.Combine(MyFileSystem.UserDataPath, "Storage", "UnifiedStorage.profiles.xml");
        document = Load();
    }

    public IReadOnlyList<ScopeProfile> Profiles => document.Profiles;

    public ScopeProfile GetOrCreate(string worldId, MechanicalInventoryScope scope)
    {
        if (scope == null)
            throw new ArgumentNullException(nameof(scope));
        var anchor = scope.Grids.Min(grid => grid.EntityId);
        var profile = document.Profiles.FirstOrDefault(candidate =>
            string.Equals(candidate.WorldId, worldId, StringComparison.Ordinal) &&
            candidate.ScopeAnchorEntityId == anchor);
        if (profile != null)
            return profile;
        profile = new ScopeProfile
        {
            WorldId = worldId ?? string.Empty,
            ScopeAnchorEntityId = anchor,
            Policy = Config.Current.DefaultPolicy
        };
        document.Profiles.Add(profile);
        return profile;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var serializer = new XmlSerializer(typeof(LocalProfileDocument));
        using var writer = File.CreateText(path);
        serializer.Serialize(writer, document);
    }

    private LocalProfileDocument Load()
    {
        if (!File.Exists(path))
            return new LocalProfileDocument();
        try
        {
            var serializer = new XmlSerializer(typeof(LocalProfileDocument));
            using var reader = File.OpenText(path);
            return serializer.Deserialize(reader) as LocalProfileDocument ?? new LocalProfileDocument();
        }
        catch (Exception exception)
        {
            log.Error(exception, "Failed to load local profile store: {0}", path);
            return new LocalProfileDocument();
        }
    }
}
