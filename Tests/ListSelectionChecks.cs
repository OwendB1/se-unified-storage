using ClientPlugin.UI;

internal static class ListSelectionChecks
{
    public static void Run()
    {
        var selected = new List<int>();
        int? anchor = null;
        void Click(int row, bool control = false, bool shift = false) => ListSelection.Click(selected, ref anchor, row, control, shift);
        void Expect(params int[] rows)
        {
            if (!selected.OrderBy(i => i).SequenceEqual(rows)) throw new Exception("Unexpected multi-selection");
        }
        Click(4); Expect(4);
        Click(1, shift: true); Expect(1, 2, 3, 4);
        Click(3, shift: true); Expect(3, 4); // Shrink without losing the original anchor.
        Click(7, control: true); Expect(3, 4, 7);
        Click(9, control: true, shift: true); Expect(3, 4, 7, 8, 9);
        Click(7, control: true); Expect(3, 4, 8, 9);
        Click(2); Expect(2);
        Click(2, control: true); Expect();

        var items = new List<int> { 0, 1, 2, 3, 4 };
        ListSelection.Move(items, new[] { 1, 2 }, -1);
        if (!items.SequenceEqual(new[] { 1, 2, 0, 3, 4 })) throw new Exception("Range order changed moving up");
        ListSelection.Move(items, new[] { 1, 2 }, -1);
        if (!items.SequenceEqual(new[] { 1, 2, 0, 3, 4 })) throw new Exception("Top boundary moved");
        ListSelection.Move(items, new[] { 1, 2 }, 1);
        if (!items.SequenceEqual(new[] { 0, 1, 2, 3, 4 })) throw new Exception("Range order changed moving down");
        ListSelection.Move(items, new[] { 1, 3 }, 1);
        if (!items.SequenceEqual(new[] { 0, 2, 1, 4, 3 })) throw new Exception("Non-adjacent selection moved incorrectly");
        ListSelection.Move(items, Array.Empty<int>(), -1);
        if (!items.SequenceEqual(new[] { 0, 2, 1, 4, 3 })) throw new Exception("Empty selection moved items");
        Console.WriteLine("Multi-selection checks passed.");
    }
}
