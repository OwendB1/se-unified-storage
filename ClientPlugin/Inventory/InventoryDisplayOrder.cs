using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using ClientPlugin.Profiles;
using Sandbox.Common.ObjectBuilders.Definitions;
using VRage.Game;
using VRage.ObjectBuilders.Private;

namespace ClientPlugin.Inventory;

public sealed class DisplayOrderRecord
{
    public string WorldId { get; set; }
    public long Anchor { get; set; }
    public string View { get; set; }
    public string Section { get; set; }
    public List<string> Keys { get; set; } = new();
    public bool CustomOrder { get; set; }
}

// Presentation preferences stay local and never change physical inventories or shared profiles.
internal static class InventoryDisplayOrder
{
    internal static bool IsPriorityDriven(InventoryRoleProjection role) =>
        role.Role == InventoryRoleKind.ProductionInput && role.Members.Any(member =>
            member.Owner is Sandbox.Game.Entities.Cube.MyRefinery);

    internal static InventoryProjection Apply(InventoryProjection projection, ScopeProfile profile,
        string view, long accessedOwner)
    {
        var nativeOrder = projection.Scope.Inventories.OrderBy(member => member.OwnerEntityId == accessedOwner ? 0 : 1)
            .ThenBy(member => member.OwnerEntityId).ThenBy(member => member.InventoryIndex)
            .SelectMany(member => member.Inventory.GetItems().Select(item => (member.Inventory, item.ItemId)))
            .Distinct().Select((key, index) => (key, index)).ToDictionary(pair => pair.key, pair => pair.index);
        var roles = projection.Roles.Select(role =>
        {
            if (IsPriorityDriven(role)) return role;
            var order = Get(profile, view, role);
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            var stacks = role.Stacks.OrderBy(stack => stack.Sources.Min(source =>
                nativeOrder.TryGetValue((source.Inventory, source.ItemId), out var index) ? index : int.MaxValue)).ToArray();
            foreach (var stack in stacks)
            {
                var fingerprint = Fingerprint(stack);
                occurrences.TryGetValue(fingerprint, out var count);
                occurrences[fingerprint] = count + 1;
                stack.DisplayKey = fingerprint + ":" + count;
            }
            var known = new HashSet<string>(order.Keys, StringComparer.Ordinal);
            foreach (var stack in stacks)
                if (known.Add(stack.DisplayKey))
                {
                    order.Keys.Add(stack.DisplayKey);
                    Plugin.Instance.Profiles.DisplayOrderChanged();
                }
            // Remember absent items for return trips, without retaining unlimited history.
            if (order.Keys.Count > 8192)
            {
                var present = new HashSet<string>(stacks.Select(stack => stack.DisplayKey));
                var excess = order.Keys.Count - Math.Max(8192, present.Count);
                order.Keys.RemoveAll(key => !present.Contains(key) && excess-- > 0);
            }
            var ranks = order.Keys.Select((key, index) => (key, index)).ToDictionary(pair => pair.key, pair => pair.index);
            // Stable within each category; returning items keep their remembered position.
            // A deliberate drag opts this section into a fully custom layout.
            var sorted = order.CustomOrder ? stacks.OrderBy(stack => ranks[stack.DisplayKey]) :
                stacks.OrderBy(stack => Category(stack.DefinitionId.TypeId.ToString()), StringComparer.Ordinal)
                    .ThenBy(stack => ranks[stack.DisplayKey]);
            return new InventoryRoleProjection(role.Section, role.Role, role.Members,
                sorted.ToArray(), role.CurrentMass,
                role.CurrentVolume, role.MaxVolume, role.Group, role.Accepts);
        }).ToArray();
        return new InventoryProjection(projection.Scope, roles);
    }

    internal static void Move(ScopeProfile profile, string view, InventoryRoleProjection role,
        ProjectedInventoryStack source, ProjectedInventoryStack target)
    {
        if (source == target || source?.DisplayKey == null) return;
        var order = Get(profile, view, role);
        if (!order.Keys.Contains(source.DisplayKey)) return;
        if (!order.CustomOrder)
        {
            var visible = role.Stacks.Select(stack => stack.DisplayKey).ToArray();
            var present = new HashSet<string>(visible, StringComparer.Ordinal);
            order.Keys = visible.Concat(order.Keys.Where(key => !present.Contains(key))).ToList();
            order.CustomOrder = true;
        }
        var index = target?.DisplayKey == null ? order.Keys.Count : order.Keys.IndexOf(target.DisplayKey);
        order.Keys.Remove(source.DisplayKey);
        order.Keys.Insert(index < 0 ? order.Keys.Count : Math.Min(index, order.Keys.Count), source.DisplayKey);
        Plugin.Instance.Profiles.DisplayOrderChanged();
    }

    private static string Category(string type) => type switch
    {
        "MyObjectBuilder_Ore" => "01",
        "MyObjectBuilder_Ingot" => "02",
        "MyObjectBuilder_Component" => "03",
        "MyObjectBuilder_AmmoMagazine" => "04",
        "MyObjectBuilder_PhysicalGunObject" => "05",
        "MyObjectBuilder_GasContainerObject" or "MyObjectBuilder_OxygenContainerObject" => "06",
        "MyObjectBuilder_ConsumableItem" => "07",
        "MyObjectBuilder_Datapad" => "08",
        _ => "09:" + type
    };

    private static DisplayOrderRecord Get(ScopeProfile profile, string view, InventoryRoleProjection role)
    {
        var section = role.Section;
        var key = $"{section.GroupId}|{section.Kind}|{section.BlockDefinitionId}|{section.InventoryIndex}|{section.ConstraintSignature}|{role.Role}";
        var records = Plugin.Instance.Profiles.DisplayOrders;
        var order = records.FirstOrDefault(record => record.WorldId == profile.WorldId &&
            record.Anchor == profile.ScopeAnchorEntityId && record.View == view && record.Section == key);
        if (order != null) return order;
        order = new DisplayOrderRecord { WorldId = profile.WorldId, Anchor = profile.ScopeAnchorEntityId, View = view, Section = key };
        records.Add(order);
        Plugin.Instance.Profiles.DisplayOrderChanged();
        return order;
    }

    private static string Fingerprint(ProjectedInventoryStack stack)
    {
        var content = stack.Representative.Content;
        if (content is MyObjectBuilder_GasContainerObject)
        {
            content = (MyObjectBuilder_PhysicalObject)content.Clone();
            ((MyObjectBuilder_GasContainerObject)content).GasLevel = 0;
        }
        using var stream = new MemoryStream();
        if (!MyObjectBuilderSerializerKeen.SerializeXML(stream, content))
            throw new InvalidOperationException("Cannot identify inventory item for display ordering.");
        using var sha = SHA256.Create();
        return stack.DefinitionId + ":" + BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "");
    }
}
