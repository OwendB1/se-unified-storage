// Keep existing namespaces and enum order for local XML compatibility.
namespace ClientPlugin
{
    public enum DistributionPolicy { ExistingStackFirst, FillFirst, EvenByItem }
}

namespace ClientPlugin.Inventory
{
    public enum InventorySectionKind
    {
        UnifiedCargo, Weapons, PowerProducers, Refineries, Assemblers,
        GasSystems, ShipTools, SafetySystems, DefinitionFallback, Connectors
    }

    public enum InventoryRoleKind
    {
        GeneralCargo, Ammunition, Fuel, ProductionInput, ProductionOutput,
        GasGeneratorFuel, Bottles, ToolInventory, ParachuteMaterial, Unknown
    }
}
