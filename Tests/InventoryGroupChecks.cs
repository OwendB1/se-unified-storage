using ClientPlugin.Inventory;
using ClientPlugin.Profiles;
using Shared.Companion;

internal static class InventoryGroupChecks
{
    internal static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        static void Invalid(ScopeProfile profile)
        {
            try { ProfileCodec.Validate(profile); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Invalid group schema accepted");
        }

        var oldXml = System.Text.Encoding.UTF8.GetBytes("<ScopeProfile><GroupSchemaVersion>1</GroupSchemaVersion><Groups>" +
            "<InventoryGroupRecord><Id>old</Id><Name>Old guns</Name><Selector>Family</Selector><Family>Weapons</Family>" +
            "<AllRoles>false</AllRoles><Role>Ammunition</Role><ItemType>MyObjectBuilder_AmmoMagazine</ItemType>" +
            "</InventoryGroupRecord></Groups></ScopeProfile>");
        var oldOrder = ProfileCodec.DecodeDocument<ScopeProfile>(oldXml);
        ProfileCodec.Validate(oldOrder);
        InventoryGroupRecord.Migrate(oldOrder);
        Check(oldOrder.Groups[0].Id == "old" && oldOrder.Groups[0].Rules.Single().Family == InventorySectionKind.Weapons &&
            !oldOrder.Groups[0].Rules.Single().AllRoles, "Original XML field order must remain readable");

        var legacy = new ScopeProfile
        {
            GroupSchemaVersion = 1,
            Groups = new()
            {
                new() { Id = "weapons", Name = "Weapons", Selector = InventoryGroupSelector.TerminalGroup,
                    Value = "New guns", AllRoles = false, Role = InventoryRoleKind.Ammunition,
                    ItemType = "MyObjectBuilder_AmmoMagazine", ItemDefinitionId = "MyObjectBuilder_AmmoMagazine/ModAmmo" }
            },
            Loadouts = new() { new() { GroupId = "weapons", SupplyGroupId = "cargo", ItemDefinitionId = "ammo" } }
        };
        // Exercise the actual XML serializer, including inherited legacy fields and absent Rules.
        legacy = ProfileCodec.Clone(legacy);
        Check(legacy.Groups[0].Rules == null, "Absent legacy Rules must not become an empty list");
        ProfileCodec.Validate(legacy);
        InventoryGroupRecord.Migrate(legacy);
        var migrated = legacy.Groups[0];
        Check(legacy.GroupSchemaVersion == InventoryGroupRecord.SchemaVersion && migrated.Rules.Count == 1, "Single rule migration");
        var first = migrated.Rules[0];
        Check(first.Selector == InventoryGroupSelector.TerminalGroup && first.Value == "New guns" &&
            first.Role == InventoryRoleKind.Ammunition && !first.AllRoles && first.ItemDefinitionId.EndsWith("/ModAmmo"), "Migration must preserve filters");
        Check(legacy.Loadouts[0].GroupId == "weapons" && legacy.Loadouts[0].SupplyGroupId == "cargo", "Migration must preserve group references");
        InventoryGroupRecord.Migrate(legacy);
        Check(migrated.Rules.Count == 1, "Migration must be idempotent");

        var draft = migrated.Copy();
        draft.Rules[0].Value = "Other guns";
        draft.Rules.Add(new() { Selector = InventoryGroupSelector.Family, Family = InventorySectionKind.PowerProducers,
            AllRoles = false, Role = InventoryRoleKind.Fuel, ItemType = "MyObjectBuilder_Ingot" });
        Check(migrated.Rules.Count == 1 && first.Value == "New guns", "Draft/copy edits must not leak to saved group");
        legacy.Groups[0] = draft;
        var roundTrip = ProfileCodec.Clone(legacy);
        ProfileCodec.Validate(roundTrip);
        Check(roundTrip.Groups[0].Rules.Count == 2 && roundTrip.Groups[0].Rules[1].Role == InventoryRoleKind.Fuel, "Multi-rule round trip");
        var schemaTwo = ProfileCodec.Clone(roundTrip);
        schemaTwo.GroupSchemaVersion = 2;
        ProfileCodec.Validate(schemaTwo);
        InventoryGroupRecord.Migrate(schemaTwo);
        Check(schemaTwo.GroupSchemaVersion == InventoryGroupRecord.SchemaVersion &&
            schemaTwo.Groups[0].Rules.Count == 2 && schemaTwo.Groups[0].Rules.All(rule => !rule.Exclude),
            "Schema 2 migration must preserve include rules");
        var excluded = new InventoryGroupRule { Exclude = true, Selector = InventoryGroupSelector.BlockDefinition,
            Value = "MyObjectBuilder_Refinery/ShieldGenerator" };
        draft.Rules.Add(excluded);
        roundTrip = ProfileCodec.Clone(legacy);
        ProfileCodec.Validate(roundTrip);
        Check(roundTrip.Groups[0].Rules[2].Exclude && roundTrip.Groups[0].Rules[2].Value == excluded.Value,
            "Definition exclusion XML round trip");
        var exclusionDraft = draft.Copy();
        exclusionDraft.Rules[2].Exclude = false;
        Check(draft.Rules[2].Exclude, "Draft edits must not change saved exclusions");
        schemaTwo.Groups[0].Rules.Add(excluded.CopyRule());
        schemaTwo.GroupSchemaVersion = 2;
        Invalid(schemaTwo); // Exclusions cannot masquerade as the older include-only schema.

        var include = new InventoryGroupRule { Selector = InventoryGroupSelector.Family, Family = InventorySectionKind.Refineries };
        var matching = new[] { include, excluded };
        Check(!InventoryGroupRule.Includes(matching, InventoryRoleKind.ProductionInput) &&
            !InventoryGroupRule.Includes(matching.Reverse(), InventoryRoleKind.ProductionInput),
            "Exclusions override family includes regardless of row order");
        Check(InventoryGroupRule.Includes(new[] { include, include.CopyRule() }, InventoryRoleKind.ProductionInput),
            "Overlapping include rules still include a normal refinery");
        Check(!InventoryGroupRule.Includes(new[] { excluded }, InventoryRoleKind.ProductionInput) &&
            !InventoryGroupRule.Includes(Array.Empty<InventoryGroupRule>(), InventoryRoleKind.ProductionInput),
            "Exclusion-only and empty groups must match nothing");
        var roleExclusion = excluded.CopyRule();
        roleExclusion.AllRoles = false;
        roleExclusion.Role = InventoryRoleKind.ProductionInput;
        matching = new[] { include, roleExclusion };
        Check(!InventoryGroupRule.Includes(matching, InventoryRoleKind.ProductionInput) &&
            InventoryGroupRule.Includes(matching, InventoryRoleKind.ProductionOutput), "Role exclusions preserve other roles");
        roleExclusion.ItemType = "MyObjectBuilder_Ore";
        roleExclusion.ItemDefinitionId = "MyObjectBuilder_Ore/Iron";
        Check(InventoryGroupRule.Includes(matching, InventoryRoleKind.ProductionInput),
            "Item exclusions must retain inventory membership for other materials");
        Check(!InventoryGroupRule.Includes(matching, InventoryRoleKind.ProductionInput, "MyObjectBuilder_Ore", "MyObjectBuilder_Ore/Iron") &&
            InventoryGroupRule.Includes(matching, InventoryRoleKind.ProductionInput, "MyObjectBuilder_Ore", "MyObjectBuilder_Ore/Nickel") &&
            InventoryGroupRule.Includes(matching, InventoryRoleKind.ProductionOutput, "MyObjectBuilder_Ore", "MyObjectBuilder_Ore/Iron"),
            "Item exclusions must keep their role, category and exact-item filters together");
        roleExclusion.ItemDefinitionId = string.Empty;
        Check(!InventoryGroupRule.Includes(matching, InventoryRoleKind.ProductionInput, "MyObjectBuilder_Ore", "MyObjectBuilder_Ore/Nickel") &&
            InventoryGroupRule.Includes(matching, InventoryRoleKind.ProductionInput, "MyObjectBuilder_Ingot", "MyObjectBuilder_Ingot/Iron"),
            "Category exclusions must preserve other categories");
        var selection = new InventorySelection { AnchorId = 10, Group = draft.Copy(), Role = InventoryRoleKind.Ammunition };
        var intent = new TransferIntent
        {
            Source = selection, Destination = selection,
            Seed = new() { OwnerId = 20 }, ItemDefinition = "MyObjectBuilder_AmmoMagazine/ModAmmo", AmountRaw = 1
        };
        intent = ProfileCodec.Decode<TransferIntent>(ProfileCodec.Encode(intent));
        intent.Validate();
        Check(intent.Source.Group.Rules.Count == 3 && intent.Destination.Group.Rules[1].Role == InventoryRoleKind.Fuel &&
            intent.Source.Group.Rules[2].Exclude && intent.Destination.Group.Rules[2].Exclude,
            "Transfer intent must preserve complete rule rows");
        var action = new ShipActionIntent { Action = ShipAction.Rebalance, Settings = roundTrip, Selections = new() { selection } };
        action = ProfileCodec.Decode<ShipActionIntent>(ProfileCodec.Encode(action));
        action.Validate();
        Check(action.Selections[0].Group.Rules.Count == 3 && action.Selections[0].Group.Rules[2].Exclude,
            "Action selection exclusions round trip");
        // New server must still understand a legacy client's single-selector intent.
        intent.Source.Group = intent.Destination.Group = new() { Selector = InventoryGroupSelector.Family, Family = InventorySectionKind.Weapons };
        ProfileCodec.Decode<TransferIntent>(ProfileCodec.Encode(intent)).Validate();
        Check(first.AcceptsRole(InventoryRoleKind.Ammunition) && !first.AcceptsRole(InventoryRoleKind.Fuel), "Role filtering");
        Check(first.AcceptsItem("MyObjectBuilder_AmmoMagazine", "MyObjectBuilder_AmmoMagazine/ModAmmo") &&
            !first.AcceptsItem("MyObjectBuilder_AmmoMagazine", "MyObjectBuilder_AmmoMagazine/OtherAmmo") &&
            !first.AcceptsItem("MyObjectBuilder_Ingot", "MyObjectBuilder_AmmoMagazine/ModAmmo"), "Category and exact item must both match");

        draft.Rules.Clear();
        var empty = ProfileCodec.Clone(legacy);
        ProfileCodec.Validate(empty);
        Check(empty.Groups[0].Rules != null && !empty.Groups[0].EffectiveRules.Any(), "Empty group must stay empty, not become All blocks");
        var defaults = new ScopeProfile();
        InventoryGroupRecord.Migrate(defaults);
        ProfileCodec.Validate(defaults);
        Check(defaults.Groups.All(group => group.Rules.Count == 1 && group.Rules[0].Selector == InventoryGroupSelector.Family), "Default groups need explicit family rules");
        defaults.GroupSchemaVersion = 1;
        Invalid(defaults); // Never silently interpret a rule list as legacy scalar fields.
        defaults.GroupSchemaVersion = InventoryGroupRecord.SchemaVersion;
        defaults.Groups[0].Rules = null;
        Invalid(defaults);
        defaults.Groups[0].Rules = new() { null };
        Invalid(defaults);
        defaults.Groups[0].Rules = Enumerable.Range(0, InventoryGroupRecord.MaxRules + 1).Select(_ => new InventoryGroupRule()).ToList();
        Invalid(defaults);
        defaults.Groups[0].Rules = new() { new() { Selector = (InventoryGroupSelector)999 } };
        Invalid(defaults);
        ProfileCodec.ValidateGroup(new InventoryGroupRecord { Selector = InventoryGroupSelector.Family, Family = InventorySectionKind.Weapons });
        ProfileCodec.ValidateGroup(draft);
        Console.WriteLine("Inventory group migration, drafts, XML, exclusions, filters and bounds passed.");
    }
}
