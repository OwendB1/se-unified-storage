using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Inventory;

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
    public string GroupId { get; set; }
    public string SupplyGroupId { get; set; }
    public string ReturnGroupId { get; set; }
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
    public int GroupSchemaVersion { get; set; }
    public List<InventoryGroupRecord> Groups { get; set; } = new();
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

