using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using ClientPlugin.Profiles;

namespace Shared.Companion;

public sealed class SharedScopeProfile
{
    public int SchemaVersion { get; set; } = 1;
    public Guid Id { get; set; }
    public long Revision { get; set; }
    public long AnchorEntityId { get; set; }
    public long OwnerIdentityId { get; set; }
    public bool FactionShared { get; set; }
    public ScopeProfile Settings { get; set; }
}

public static class ProfileCodec
{
    public const int MaxSettingsBytes = 32 * 1024;

    public static byte[] Encode<T>(T value)
    {
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
            Serializer<T>.Value.Serialize(writer, value);
        if (stream.Length > CompanionProtocol.MaxBodyBytes) throw new InvalidDataException("Profile exceeds the protocol size limit.");
        return stream.ToArray();
    }

    public static T Decode<T>(byte[] bytes)
    {
        if (bytes == null || bytes.Length > CompanionProtocol.MaxBodyBytes) throw new InvalidDataException("Profile too large.");
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = CompanionProtocol.MaxBodyBytes
        };
        using var stream = new MemoryStream(bytes, false);
        using (var check = XmlReader.Create(stream, settings))
            while (check.Read())
                if (check.Depth > 12) throw new InvalidDataException("Profile nesting too deep.");
        // XmlReader owns no stream by default.
        stream.Position = 0;
        using var reader = XmlReader.Create(stream, settings);
        return (T)Serializer<T>.Value.Deserialize(reader);
    }

    public static ScopeProfile Clone(ScopeProfile profile) => Decode<ScopeProfile>(Encode(profile));

    public static void Validate(ScopeProfile profile)
    {
        if (profile == null || profile.GroupSchemaVersion != 1 || !Defined(profile.Policy) ||
            profile.ComponentStartThreshold < 0 || profile.ComponentStartThreshold > 1 ||
            profile.Groups == null || profile.Groups.Count > 128 ||
            profile.Loadouts == null || profile.Loadouts.Count > 256 ||
            profile.ComponentTargets == null || profile.ComponentTargets.Count > 512 ||
            profile.InventoryManagement == null || profile.InventoryManagement.Count > 512 ||
            profile.RefineryPriority?.PinnedDefinitionIds == null || profile.RefineryPriority.ManualDefinitionIds == null)
            throw new InvalidDataException("Invalid profile schema or limits.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in profile.Groups)
            if (group == null || !Text(group.Id, 128, true) || !ids.Add(group.Id) || !Text(group.Name, 128, true) ||
                !Text(group.Value, 512) || !Text(group.ItemType, 256) || !Text(group.ItemDefinitionId, 512) ||
                !Defined(group.Selector) || !Defined(group.Family) || !Defined(group.Role))
                throw new InvalidDataException("Invalid or duplicate inventory group.");
        foreach (var target in profile.ComponentTargets)
            if (target == null || !Text(target.DefinitionId, 512, true) || !Text(target.BlueprintDefinitionId, 512) || !Amount(target.Amount))
                throw new InvalidDataException("Invalid component target.");
        if (profile.ComponentTargets.Select(t => t.DefinitionId).Distinct(StringComparer.Ordinal).Count() != profile.ComponentTargets.Count)
            throw new InvalidDataException("Duplicate component target.");
        foreach (var rule in profile.Loadouts)
            if (rule == null || !Text(rule.GroupId, 128, true) || !Text(rule.SupplyGroupId, 128) || !Text(rule.ReturnGroupId, 128) ||
                !Text(rule.ItemDefinitionId, 512, true) || !Text(rule.TargetBlockDefinitionId, 512) || !Amount(rule.Amount) ||
                !Defined(rule.Policy) || !Defined(rule.Role) || !Defined(rule.Section) || !Defined(rule.TargetKind))
                throw new InvalidDataException("Invalid loadout rule.");
        foreach (var record in profile.InventoryManagement)
            if (record == null || record.BlockEntityId == 0 || record.InventoryIndex < 0 || record.InventoryIndex > 255 ||
                (record.Flags & ~(InventoryManagementFlags.ManualBlock | InventoryManagementFlags.ReservedInventory |
                                  InventoryManagementFlags.NoUnifiedCargoDestination)) != 0)
                throw new InvalidDataException("Invalid management override.");
        foreach (var list in new[] { profile.RefineryPriority.PinnedDefinitionIds, profile.RefineryPriority.ManualDefinitionIds })
            if (list.Count > 512 || list.Any(id => !Text(id, 512, true))) throw new InvalidDataException("Invalid ore priorities.");
        if (Encode(profile).Length > MaxSettingsBytes) throw new InvalidDataException("Settings exceed 32 KiB. Reduce this profile before publishing.");
    }

    private static bool Defined<T>(T value) => Enum.IsDefined(typeof(T), value);
    private static bool Text(string value, int maximum, bool required = false) =>
        (value == null ? !required : value.Length <= maximum && (!required || !string.IsNullOrWhiteSpace(value)));
    private static bool Amount(decimal amount) => amount >= 0 && amount <= 1000000000000m;
    private static class Serializer<T> { internal static readonly XmlSerializer Value = new(typeof(T)); }
}
