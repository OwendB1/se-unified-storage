using System;
using System.Linq;

namespace ClientPlugin.UI;

internal static class DefinitionLabels
{
    public static string SingleLine(string text, string fallback) =>
        (text ?? "").Split(new[] { '\r', '\n', '\u0085', '\u2028', '\u2029' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Replace('\t', ' ')).FirstOrDefault(line => line.Length > 0) ?? fallback;

    public static string Item(string name, string type, string subtype)
    {
        name = SingleLine(name, subtype);
        if (type != "MyObjectBuilder_PhysicalGunObject") return name;
        // Mods can give every quality the same display name. These are the game's
        // standard tool IDs; unknown/modded tools retain their exact subtype instead.
        foreach (var tool in new[] { "Welder", "AngleGrinder", "HandDrill" })
        {
            if (subtype == tool + "Item") return name + " (Tier 1)";
            for (var tier = 2; tier <= 4; tier++)
                if (subtype == tool + tier + "Item") return name + $" (Tier {tier})";
        }
        return name + " [" + subtype + "]";
    }
}
