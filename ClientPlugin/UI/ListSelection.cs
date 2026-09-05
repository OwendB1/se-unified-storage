using System;
using System.Collections.Generic;
using System.Linq;

namespace ClientPlugin.UI;

internal static class ListSelection
{
    public static void Click(List<int> selected, ref int? anchor, int row, bool control, bool shift)
    {
        if (shift && anchor.HasValue)
        {
            if (!control) selected.Clear();
            for (var i = Math.Min(anchor.Value, row); i <= Math.Max(anchor.Value, row); i++)
                if (!selected.Contains(i)) selected.Add(i);
        }
        else
        {
            anchor = row;
            if (!control) selected.Clear();
            if (!selected.Remove(row)) selected.Add(row);
        }
    }

    public static void Move<T>(IList<T> items, IEnumerable<T> selection, int direction)
    {
        var selected = new HashSet<T>(selection);
        var indices = Enumerable.Range(0, items.Count);
        if (direction > 0) indices = indices.Reverse();
        foreach (var index in indices)
        {
            var next = index + direction;
            if (next < 0 || next >= items.Count || !selected.Contains(items[index]) || selected.Contains(items[next])) continue;
            (items[index], items[next]) = (items[next], items[index]);
        }
    }
}
