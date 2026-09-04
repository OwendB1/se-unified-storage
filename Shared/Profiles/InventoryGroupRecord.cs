using System;
using System.Collections.Generic;
using ClientPlugin.Inventory;

namespace ClientPlugin.Profiles;

public enum InventoryGroupSelector
{
    All, Family, BlockType, BlockDefinition, TerminalGroup, Block, RecipeOutput
}

// Shared intent schema: a selector is persisted, never resolved membership.
public sealed class InventoryGroupRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New group";
    public InventoryGroupSelector Selector { get; set; } = InventoryGroupSelector.All;
    public InventorySectionKind Family { get; set; }
    public string Value { get; set; } = string.Empty;
    public bool AllRoles { get; set; } = true;
    public InventoryRoleKind Role { get; set; }
    public string ItemType { get; set; } = string.Empty;
    public string ItemDefinitionId { get; set; } = string.Empty;

    public InventoryGroupRecord Copy() => (InventoryGroupRecord)MemberwiseClone();
    public static string DefaultId(InventorySectionKind family) => "preset:" + family;

    public static List<InventoryGroupRecord> Defaults()
    {
        var result = new List<InventoryGroupRecord>();
        foreach (InventorySectionKind family in Enum.GetValues(typeof(InventorySectionKind)))
            result.Add(new InventoryGroupRecord
            {
                Id = DefaultId(family), Name = DisplayName(family),
                Selector = InventoryGroupSelector.Family, Family = family
            });
        return result;
    }

    public static string DisplayName(InventorySectionKind family) => family switch
    {
        InventorySectionKind.UnifiedCargo => "Unified Cargo",
        InventorySectionKind.PowerProducers => "Power Producers",
        InventorySectionKind.GasSystems => "Gas Systems",
        InventorySectionKind.ShipTools => "Ship Tools",
        InventorySectionKind.SafetySystems => "Safety Systems",
        InventorySectionKind.DefinitionFallback => "Other definitions",
        _ => family.ToString()
    };

    public static void Migrate(ScopeProfile profile)
    {
        if (profile.GroupSchemaVersion >= 1)
            return;
        profile.Groups = Defaults();
        foreach (var rule in profile.Loadouts)
        {
            rule.GroupId = DefaultId(rule.Section);
            rule.SupplyGroupId = DefaultId(InventorySectionKind.UnifiedCargo);
            rule.ReturnGroupId = rule.SupplyGroupId;
        }
        profile.GroupSchemaVersion = 1;
    }
}
