using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClientPlugin.Profiles;

public sealed class LoadoutBomEntry
{
    public int Line { get; set; }
    public string DefinitionId { get; set; }
    public decimal Amount { get; set; }
    public string Error { get; set; }
}

// MGP Copy BoM / Isy's finite Special-container targets: Type/Subtype=quantity.
public static class LoadoutBom
{
    public const int MaximumLength = 65536;
    public static List<LoadoutBomEntry> Parse(string text)
    {
        var entries = new List<LoadoutBomEntry>();
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumLength)
        {
            entries.Add(new LoadoutBomEntry { Error = "Paste a BOM of up to 64 KiB." });
            return entries;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = text.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;
            // Isy writes this help block above its Special-container entries.
            if (line == "Special Container modes:" ||
                line == "Positive number: stores wanted amount, removes excess (e.g.: 100)" ||
                line == "Negative number: doesn't store items, only removes excess (e.g.: -100)" ||
                line == "Keyword 'all': stores all items of that subtype (like a type container)") continue;
            var entry = new LoadoutBomEntry { Line = i + 1 };
            entries.Add(entry);
            var separator = line.IndexOf('=');
            if (separator < 1 || separator != line.LastIndexOf('='))
            {
                entry.Error = "Expected Type/Subtype=quantity.";
                continue;
            }
            var id = line.Substring(0, separator).Trim();
            var slash = id.IndexOf('/');
            if (slash < 1 || slash == id.Length - 1 || slash != id.LastIndexOf('/'))
            {
                entry.Error = "Use a definition ID, such as Component/SteelPlate.";
                continue;
            }
            entry.DefinitionId = id.StartsWith("MyObjectBuilder_", StringComparison.Ordinal)
                ? id : "MyObjectBuilder_" + id;
            var quantity = line.Substring(separator + 1).Trim();
            if (!decimal.TryParse(quantity, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) ||
                amount < 0 || amount > 9223372036854m)
                entry.Error = "Use a non-negative quantity (decimal point: .). All, percentages and modifiers are not stock targets.";
            else if (!seen.Add(entry.DefinitionId))
                entry.Error = "Duplicate item; combine its quantities into one line.";
            else entry.Amount = amount;
        }
        if (entries.Count == 0) entries.Add(new LoadoutBomEntry { Error = "No BOM entries found." });
        return entries;
    }
}
