using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Inventory;

namespace ClientPlugin.Profiles;

public enum InventoryGroupSelector
{
    All, Family, BlockType, BlockDefinition, TerminalGroup, Block, RecipeOutput
}

// Each row is a conjunction; a group is the union of its rows.
public class InventoryGroupRule
{
    public InventoryGroupSelector Selector { get; set; } = InventoryGroupSelector.All;
    public InventorySectionKind Family { get; set; }
    public string Value { get; set; } = string.Empty;
    public bool AllRoles { get; set; } = true;
    public InventoryRoleKind Role { get; set; }
    public string ItemType { get; set; } = string.Empty;
    public string ItemDefinitionId { get; set; } = string.Empty;

    public InventoryGroupRule CopyRule() => new()
    {
        Selector = Selector, Family = Family, Value = Value, AllRoles = AllRoles,
        Role = Role, ItemType = ItemType, ItemDefinitionId = ItemDefinitionId
    };

    public bool AcceptsItem(string type, string definition) =>
        (string.IsNullOrEmpty(ItemType) || ItemType == type) &&
        (string.IsNullOrEmpty(ItemDefinitionId) || ItemDefinitionId == definition);

    public bool AcceptsRole(InventoryRoleKind role) => AllRoles || Role == role;
}

// Inherited fields only read legacy profiles/intents. Rules persist intent, never resolved membership.
public sealed class InventoryGroupRecord : InventoryGroupRule
{
    public const int SchemaVersion = 2;
    public const int MaxRules = 128;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New group";
    [System.Xml.Serialization.XmlIgnore]
    public List<InventoryGroupRule> Rules { get; set; }

    // XmlSerializer eagerly creates List<T> even when its element is absent. An array preserves
    // the distinction between a legacy missing list and a deliberately empty (match nothing) list.
    [System.Xml.Serialization.XmlArray("Rules")]
    [System.Xml.Serialization.XmlArrayItem("Rule")]
    public InventoryGroupRule[] SerializedRules
    {
        get => Rules?.ToArray();
        set => Rules = value?.ToList();
    }

    // null means legacy; an explicitly empty list matches nothing.
    [System.Xml.Serialization.XmlIgnore]
    public IEnumerable<InventoryGroupRule> EffectiveRules => Rules ?? new List<InventoryGroupRule> { CopyRule() };

    public InventoryGroupRecord Copy()
    {
        var copy = (InventoryGroupRecord)MemberwiseClone();
        copy.Rules = EffectiveRules.Select(rule => rule.CopyRule()).ToList();
        return copy;
    }
    public static string DefaultId(InventorySectionKind family) => "preset:" + family;

    public static List<InventoryGroupRecord> Defaults()
    {
        var result = new List<InventoryGroupRecord>();
        foreach (InventorySectionKind family in Enum.GetValues(typeof(InventorySectionKind)))
            result.Add(new InventoryGroupRecord
            {
                Id = DefaultId(family), Name = DisplayName(family),
                Rules = new() { new() { Selector = InventoryGroupSelector.Family, Family = family } }
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
        if (profile.GroupSchemaVersion >= SchemaVersion) return;
        if (profile.GroupSchemaVersion < 1)
        {
            profile.Groups = Defaults();
            foreach (var rule in profile.Loadouts)
            {
                rule.GroupId = DefaultId(rule.Section);
                rule.SupplyGroupId = DefaultId(InventorySectionKind.UnifiedCargo);
                rule.ReturnGroupId = rule.SupplyGroupId;
            }
        }
        foreach (var group in profile.Groups)
            group.Rules ??= new() { group.CopyRule() };
        profile.GroupSchemaVersion = SchemaVersion;
    }
}
