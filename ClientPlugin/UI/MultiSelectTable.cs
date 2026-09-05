using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using VRage.Audio;
using VRage.Input;

namespace ClientPlugin.UI;

internal sealed class MultiSelectTable : MyGuiControlMultiSelectTable
{
    public const string SelectionHelp = "Click selects one row. Ctrl-click toggles rows; Shift-click selects a range; Ctrl+Shift adds a range. Ctrl+A selects visible results.";
    private int? anchor;
    private int[] reportedSelection = Array.Empty<int>();
    public event Action SelectionChanged;

    public MultiSelectTable()
    {
        ItemSelected += (_, _) =>
        {
            if (!MyInput.Static.IsAnyShiftKeyPressed()) anchor = SelectedRowIndex;
            NotifySelectionChanged();
        };
    }

    public override MyGuiControlBase HandleInput()
    {
        MyGuiControlBase result;
        if (Enabled && MyInput.Static.IsNewPrimaryButtonPressed() &&
            m_rowsArea.Contains(MyGuiManager.MouseCursorPosition - GetPositionAbsoluteTopLeft()))
        {
            // Native table draws multiselection, but its range handler can index
            // empty rows and fires selection events before updating the range.
            HandleBaseInput();
            HandleMouseOver();
            var row = ComputeRowIndexFromPosition(MyGuiManager.MouseCursorPosition);
            if (IsValidRowIndex(row))
            {
                ListSelection.Click(SelectedRowsIndexes, ref anchor, row,
                    MyInput.Static.IsAnyCtrlKeyPressed(), MyInput.Static.IsAnyShiftKeyPressed());
                SelectedRowIndex = SelectedRowsIndexes.Contains(row) ? row : SelectedRowsIndexes.Cast<int?>().LastOrDefault();
                MyGuiSoundManager.PlaySound(GuiSounds.MouseClick);
            }
            result = this;
        }
        else result = base.HandleInput();
        NotifySelectionChanged();
        return result;
    }

    public new bool SetSelectedRow(int index)
    {
        RestoreSelection(new[] { index });
        return IsValidRowIndex(index);
    }

    public void RestoreSelection(IEnumerable<int> indices)
    {
        SelectedRowsIndexes = indices.Where(index => IsValidRowIndex(index)).Distinct().ToList();
        SelectedRowIndex = anchor = SelectedRowsIndexes.Cast<int?>().FirstOrDefault();
        NotifySelectionChanged();
    }

    public override void Clear()
    {
        base.Clear();
        SelectedRowsIndexes.Clear();
        SelectedRowIndex = anchor = null;
    }

    private void NotifySelectionChanged()
    {
        var current = SelectedRowsIndexes.OrderBy(index => index).ToArray();
        if (reportedSelection.SequenceEqual(current)) return;
        reportedSelection = current;
        SelectionChanged?.Invoke();
    }
}
