using System;
using System.Collections.Generic;
using System.IO;
using ClientPlugin.Profiles;

namespace Shared.Companion;

// RefillBottles is reserved for wire compatibility; retired in favor of SE's native refill systems.
public enum ShipAction { Rebalance, SortRefineries, QueueComponents, ApplyLoadouts, DrainAssemblers, RefillBottles }

public sealed class ShipActionIntent
{
    public ShipAction Action { get; set; }
    public bool UseSharedSettings { get; set; }
    public ScopeProfile Settings { get; set; }
    public List<InventorySelection> Selections { get; set; } = new();
    public string GroupId { get; set; }

    public void Validate()
    {
        if (!Enum.IsDefined(typeof(ShipAction), Action) || Selections == null || Selections.Count > 16 ||
            GroupId?.Length > 128) throw new InvalidDataException("Invalid ship action.");
        ProfileCodec.Validate(Settings);
        foreach (var selection in Selections)
        {
            if (selection == null || selection.AnchorId == 0 || selection.Group == null ||
                !Enum.IsDefined(typeof(ClientPlugin.Inventory.InventoryRoleKind), selection.Role) ||
                selection.InventoryIndex < -1 || selection.InventoryIndex > 255 || selection.BlockDefinition?.Length > 512 ||
                selection.TerminalGroup?.Length > 128 || selection.NetworkRootId != 0 && !string.IsNullOrEmpty(selection.TerminalGroup))
                throw new InvalidDataException("Invalid action selection.");
            ProfileCodec.Validate(new ScopeProfile { GroupSchemaVersion = 1, Groups = new() { selection.Group } });
        }
        if (Action == ShipAction.Rebalance && Selections.Count == 0)
            throw new InvalidDataException("Rebalance requires explicit selected rows.");
    }

    public static CompanionCapabilities Capability(ShipAction action) => action switch
    {
        ShipAction.Rebalance => CompanionCapabilities.Transfers,
        ShipAction.SortRefineries => CompanionCapabilities.RefineryAutomation,
        ShipAction.QueueComponents => CompanionCapabilities.ComponentAutomation,
        ShipAction.ApplyLoadouts => CompanionCapabilities.LoadoutAutomation,
        _ => CompanionCapabilities.UtilityJobs
    };
}

public sealed class ActionReceipt
{
    public Guid JobId { get; set; }
    public int Mutations { get; set; }
    public long MovedRaw { get; set; }
    public TransferFailure Failure { get; set; }
    public string Detail { get; set; }
}

public enum UtilityJobState { Running, Complete, Partial, Cancelled, Interrupted }
public sealed class UtilityJobReceipt
{
    public Guid Id { get; set; }
    public UtilityJobState State { get; set; }
    public int Mutations { get; set; }
    public int CompletedItems { get; set; }
    public TransferFailure Failure { get; set; }
    public string Detail { get; set; }
}

// Only ownership metadata, never another player's settings or inventory contents.
public sealed class AutomationManifest
{
    public const CompanionCapabilities Modes = CompanionCapabilities.RefineryAutomation |
        CompanionCapabilities.ComponentAutomation | CompanionCapabilities.LoadoutAutomation;
    public List<AutomationClaim> Claims { get; set; } = new();

    public byte[] Encode()
    {
        if (Claims.Count > 256) throw new InvalidDataException("Too many automation claims.");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Claims.Count);
        foreach (var claim in Claims) { writer.Write(claim.Anchor); writer.Write((ulong)claim.Modes); }
        return stream.ToArray();
    }
    public static AutomationManifest Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 4 || bytes.Length > 4100) throw new InvalidDataException("Invalid claims.");
        using var reader = new BinaryReader(new MemoryStream(bytes, false));
        var count = reader.ReadInt32();
        if (count < 0 || count > 256 || bytes.Length != 4 + count * 16) throw new InvalidDataException("Invalid claim count.");
        var result = new AutomationManifest();
        for (var index = 0; index < count; index++)
        {
            var claim = new AutomationClaim { Anchor = reader.ReadInt64(), Modes = (CompanionCapabilities)reader.ReadUInt64() };
            if (claim.Anchor == 0 || (claim.Modes & ~Modes) != 0) throw new InvalidDataException("Invalid claim.");
            result.Claims.Add(claim);
        }
        return result;
    }
}

public sealed class AutomationClaim
{
    public long Anchor { get; set; }
    public CompanionCapabilities Modes { get; set; }
}

public sealed class AutomationStatus
{
    public CompanionCapabilities Owned { get; set; }
    public string State { get; set; }
    public ActionReceipt LastResult { get; set; }
}
