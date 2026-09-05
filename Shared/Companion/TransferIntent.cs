using System;
using System.Collections.Generic;
using System.IO;
using ClientPlugin;
using ClientPlugin.Inventory;
using ClientPlugin.Profiles;

namespace Shared.Companion;

public sealed class InventoryAddress
{
    public long OwnerId { get; set; }
    public int Index { get; set; }
}

// A selector is user intent, never a trusted list of physical allocations.
public sealed class InventorySelection
{
    public long AnchorId { get; set; }
    public InventoryGroupRecord Group { get; set; }
    public InventoryRoleKind Role { get; set; }
    public string BlockDefinition { get; set; }
    public int InventoryIndex { get; set; } = -1;
    public long NetworkRootId { get; set; }
    public string TerminalGroup { get; set; }
}

public sealed class TransferIntent
{
    public InventorySelection Source { get; set; }
    public InventorySelection Destination { get; set; }
    public InventoryAddress Seed { get; set; }
    public uint SeedItemId { get; set; }
    public string ItemDefinition { get; set; }
    public InventoryAddress ConcreteDestination { get; set; }
    public long AmountRaw { get; set; }
    public DistributionPolicy Policy { get; set; }
    public List<InventoryManagementRecord> Exclusions { get; set; } = new();

    public void Validate()
    {
        if (Source == null && Destination == null || Seed == null || Seed.OwnerId == 0 ||
            Seed.Index < 0 || Seed.Index > 255 || AmountRaw <= 0 ||
            string.IsNullOrWhiteSpace(ItemDefinition) || ItemDefinition.Length > 512 ||
            !Enum.IsDefined(typeof(DistributionPolicy), Policy) ||
            (Destination == null ? ConcreteDestination == null || ConcreteDestination.OwnerId == 0 ||
                ConcreteDestination.Index < 0 || ConcreteDestination.Index > 255 : ConcreteDestination != null))
            throw new InvalidDataException("Invalid transfer intent.");
        var profile = new ScopeProfile { GroupSchemaVersion = 1, InventoryManagement = Exclusions };
        ProfileCodec.Validate(profile);
        foreach (var selection in new[] { Source, Destination })
        {
            if (selection == null) continue;
            if (selection.AnchorId == 0 || selection.Group == null ||
                !Enum.IsDefined(typeof(InventoryRoleKind), selection.Role) ||
                selection.InventoryIndex < -1 || selection.InventoryIndex > 255 ||
                selection.BlockDefinition?.Length > 512 || selection.TerminalGroup?.Length > 512 ||
                selection.NetworkRootId != 0 && !string.IsNullOrEmpty(selection.TerminalGroup))
                throw new InvalidDataException("Invalid inventory selector.");
            profile.Groups = new List<InventoryGroupRecord> { selection.Group };
            ProfileCodec.Validate(profile);
        }
    }
}

public enum TransferFailure
{
    None, AccessDenied, ScopeChanged, StackChanged, Excluded, Constraint,
    DestinationFull, NoConveyorPath, WorkLimit, InsufficientStock, InvalidIntent,
    PolicyDisabled, UnknownOutcome
}

public sealed class TransferReceipt
{
    public long RequestedRaw { get; set; }
    public long MovedRaw { get; set; }
    public long RejectedRaw => Math.Max(0, RequestedRaw - MovedRaw);
    public int Allocations { get; set; }
    public TransferFailure Failure { get; set; }
}
