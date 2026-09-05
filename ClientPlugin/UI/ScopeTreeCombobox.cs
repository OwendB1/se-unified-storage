using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin.UI;

// Keep native text, selection, scrolling and tooltips; only draw the missing branch glyphs.
internal sealed class ScopeTreeCombobox : MyGuiControlCombobox
{
    private const string Indent = "      ";
    private readonly Dictionary<long, bool> branches = new();
    private static readonly AccessTools.FieldRef<MyGuiControlCombobox, RectangleF> OpenArea =
        AccessTools.FieldRefAccess<MyGuiControlCombobox, RectangleF>("m_openedItemArea");
    private static readonly AccessTools.FieldRef<MyGuiControlCombobox, RectangleF> SelectedArea =
        AccessTools.FieldRefAccess<MyGuiControlCombobox, RectangleF>("m_selectedItemArea");
    private static readonly AccessTools.FieldRef<MyGuiControlCombobox, bool> Scrolling =
        AccessTools.FieldRefAccess<MyGuiControlCombobox, bool>("m_showScrollBar");
    private static readonly AccessTools.FieldRef<MyGuiControlCombobox, int> Start =
        AccessTools.FieldRefAccess<MyGuiControlCombobox, int>("m_displayItemsStartIndex");
    private static readonly AccessTools.FieldRef<MyGuiControlCombobox, int> End =
        AccessTools.FieldRefAccess<MyGuiControlCombobox, int>("m_displayItemsEndIndex");
    private static readonly AccessTools.FieldRef<MyGuiControlCombobox, Item> Hovered =
        AccessTools.FieldRefAccess<MyGuiControlCombobox, Item>("m_preselectedMouseOver");
    private static readonly AccessTools.FieldRef<MyGuiControlCombobox, int?> KeyboardIndex =
        AccessTools.FieldRefAccess<MyGuiControlCombobox, int?>("m_preselectedKeyboardIndex");
    private static readonly AccessTools.FieldRef<MyGuiControlCombobox, Vector4> SelectedColor =
        AccessTools.FieldRefAccess<MyGuiControlCombobox, Vector4>("m_textColor");
    private static readonly AccessTools.FieldRef<MyGuiControlCombobox, float> TextScale =
        AccessTools.FieldRefAccess<MyGuiControlCombobox, float>("m_textScaleWithLanguage");

    public ScopeTreeCombobox(Vector2 position, Vector2 size)
        : base(position, size, originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            openAreaItemsCount: 10, isAutoscaleEnabled: true, isAutoEllipsisEnabled: true) { }

    public new void ClearItems()
    {
        branches.Clear();
        base.ClearItems();
    }

    public void AddTreeItem(long key, string label, string toolTip)
    {
        if (label.StartsWith("  ├─ ", StringComparison.Ordinal) || label.StartsWith("  └─ ", StringComparison.Ordinal))
        {
            branches[key] = label[2] == '└';
            label = Indent + label.Substring(5);
        }
        AddItem(key, label, toolTip: toolTip);
    }

    public override void Draw(float transitionAlpha, float backgroundTransitionAlpha)
    {
        base.Draw(transitionAlpha, backgroundTransitionAlpha);
        var style = GetVisualStyle(VisualStyle);
        var selected = TryGetItemByKey(GetSelectedKey());
        var selectedArea = SelectedArea(this);
        selectedArea.Position += GetPositionAbsoluteTopLeft();
        using (MyGuiManager.UsingScissorRectangle(ref selectedArea))
            DrawBranch(selected, selectedArea, style.ItemFontNormal, TextScale(this), SelectedColor(this), transitionAlpha, false);
        if (!IsOpen) return;
        var area = OpenArea(this);
        area.Position += GetPositionAbsoluteTopLeft();
        var start = Scrolling(this) ? Start(this) : 0;
        var end = Scrolling(this) ? End(this) : GetItemsCount();
        using (MyGuiManager.UsingScissorRectangle(ref area))
        {
            for (var index = start; index < end && index < GetItemsCount(); index++)
            {
                var item = GetItemByIndex(index);
                var hovered = ReferenceEquals(item, Hovered(this));
                var focused = KeyboardIndex(this) == index;
                var color = hovered ? style.ItemTextColorHighlight ?? Vector4.One : focused
                    ? style.ItemTextColorFocus ?? Vector4.One : style.ItemTextColor ?? Vector4.One;
                var row = new RectangleF(area.Position + new Vector2(0, (index - start) * 0.03f), new Vector2(area.Size.X, 0.03f));
                DrawBranch(item, row, hovered || focused ? style.ItemFontHighlight : style.ItemFontNormal,
                    item.TextScale, color, transitionAlpha, true);
            }
        }
    }

    private void DrawBranch(Item item, RectangleF row, string font, float scale, Vector4 color, float alpha, bool connectRows)
    {
        if (item == null || !branches.TryGetValue(item.Key, out var last)) return;
        var indentWidth = MyGuiManager.MeasureString(font, new StringBuilder(Indent), scale).X;
        var top = row.Position.Y + (connectRows ? 0 : row.Size.Y * 0.15f);
        var middle = row.Position.Y + row.Size.Y * 0.5f;
        var bottom = last ? middle : row.Position.Y + row.Size.Y * (connectRows ? 1 : 0.85f);
        var origin = MyGuiManager.GetScreenCoordinateFromNormalizedCoordinate(new Vector2(row.Position.X + indentWidth * 0.3f, top));
        var end = MyGuiManager.GetScreenCoordinateFromNormalizedCoordinate(new Vector2(row.Position.X + indentWidth * 0.8f, bottom));
        var center = MyGuiManager.GetScreenCoordinateFromNormalizedCoordinate(new Vector2(row.Position.X, middle));
        var x = (int)Math.Round(origin.X);
        var y = (int)Math.Round(origin.Y);
        var joint = (int)Math.Round(center.Y);
        var thickness = Math.Max(1, (int)Math.Round(MyGuiManager.GetScreenSizeFromNormalizedSize(new Vector2(0, row.Size.Y)).Y * 0.045f));
        var tint = ApplyColorMaskModifiers(color, Enabled, alpha);
        MyGuiManager.DrawSpriteBatch(MyGuiConstants.BLANK_TEXTURE, x, y, thickness,
            Math.Max(thickness, (int)Math.Round(end.Y) - y + (last ? thickness : 0)), tint);
        MyGuiManager.DrawSpriteBatch(MyGuiConstants.BLANK_TEXTURE, x, joint,
            Math.Max(thickness, (int)Math.Round(end.X) - x), thickness, tint);
    }
}
