using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ClientPlugin.Inventory;
using ClientPlugin.Profiles;
using Sandbox.Definitions;
using Sandbox.Graphics.GUI;
using VRage;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin.UI;

internal sealed class LoadoutBomScreen : InventoryRuleEditor
{
    private readonly MechanicalInventorySession session;
    private readonly ScopeProfile profile;
    private readonly string initialGroup;
    private readonly Action saved;
    private readonly List<LoadoutRecord> draft = new();
    private List<string> groupIds;
    private MyGuiControlCombobox target, supply;
    private BomEditor text;
    private MyGuiControlTable preview;
    private MyGuiControlButton import;
    private MyGuiControlLabel status;

    public LoadoutBomScreen(MechanicalInventorySession session, ScopeProfile profile, string initialGroup, Action saved)
        : base("Import loadout BOM")
    {
        this.session = session; this.profile = profile; this.initialGroup = initialGroup; this.saved = saved;
    }

    protected override void CreateControls()
    {
        groupIds = new[] { string.Empty }.Concat(profile.Groups.Select(g => g.Id)).ToList();
        var names = groupIds.Select(id => profile.Groups.FirstOrDefault(g => g.Id == id)?.Name ?? "Choose a group").ToArray();
        target = Combo("BomTarget", "Target group", -0.36f, -0.26f, 0.35f, names,
            Math.Max(0, groupIds.IndexOf(initialGroup ?? InventoryGroupRecord.DefaultId(InventorySectionKind.ShipTools))));
        supply = Combo("BomSupply", "Supply group", 0.01f, -0.26f, 0.35f, names,
            Math.Max(0, groupIds.IndexOf(InventoryGroupRecord.DefaultId(InventorySectionKind.UnifiedCargo))));
        text = new BomEditor();
        text.SetToolTip(UnifiedStorageHelp.Wrap("Paste MGP Copy BoM or finite Isy Special-container targets here. Example: Component/SteelPlate=120. Quantities are totals across the target group, not per container."));
        Controls.Add(text);
        preview = new MyGuiControlTable
        {
            Name = "BomPreview", Position = new Vector2(-0.36f, -0.045f), Size = new Vector2(0.72f, 0.27f),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP, ColumnsCount = 3, VisibleRowsCount = 7
        };
        preview.SetCustomColumnWidths(new[] { 0.40f, 0.16f, 0.44f });
        preview.SetColumnName(0, new StringBuilder("Item"));
        preview.SetColumnName(1, new StringBuilder("Total"));
        preview.SetColumnName(2, new StringBuilder("Import result"));
        Controls.Add(preview);
        status = Label("Paste a BOM to preview its targets.", new Vector2(-0.36f, 0.24f));
        status.TextScale = 0.62f;
        Controls.Add(status);
        var paste = Button("Paste", new Vector2(-0.25f, 0.33f), text.Paste);
        paste.SetToolTip("Read the clipboard into the BOM editor.");
        Controls.Add(paste);
        import = Button("Import targets", new Vector2(0, 0.33f), Import, 0.18f);
        import.SetToolTip(UnifiedStorageHelp.Wrap("Replace matching group-total targets and keep unrelated rules. Imported rules use the scope policy, with excess returns and local maintenance off. No items move until Apply loadouts."));
        Controls.Add(import);
        Controls.Add(Button("Cancel", new Vector2(0.25f, 0.33f), () => CloseScreen()));
        target.ItemSelected += RefreshPreview;
        supply.ItemSelected += RefreshPreview;
        text.TextChanged += _ => RefreshPreview();
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        draft.Clear();
        preview.Clear();
        if (string.IsNullOrWhiteSpace(text.Text.ToString()))
        {
            status.Text = "Paste a BOM to preview its targets.";
            import.Enabled = false;
            return;
        }
        var groupId = groupIds[(int)target.GetSelectedKey()];
        var supplyId = groupIds[(int)supply.GetSelectedKey()];
        var projection = InventoryGroups.Build(session.Refresh(), profile);
        var targetRoles = projection.Roles.Where(r => r.Section.GroupId == groupId).ToArray();
        var errors = 0;
        foreach (var entry in LoadoutBom.Parse(text.Text.ToString()))
        {
            var error = entry.Error;
            MyDefinitionId id = default;
            var definition = MyDefinitionId.TryParse(entry.DefinitionId, out id)
                ? MyDefinitionManager.Static.GetPhysicalItemDefinition(id) : null;
            if (error == null && definition == null) error = "Item definition is not loaded.";
            if (error == null && definition.HasIntegralAmounts && entry.Amount != decimal.Truncate(entry.Amount))
                error = "This item requires whole quantities.";
            var roles = targetRoles.Where(r => definition != null && r.Accepts(id)).Select(r => r.Role).Distinct().ToArray();
            if (error == null && roles.Length != 1) error = roles.Length == 0
                ? "No compatible target inventory." : "Group has multiple roles; narrow its rules.";
            var existing = profile.Loadouts.Where(r => r.GroupId == groupId && r.ItemDefinitionId == entry.DefinitionId).ToArray();
            if (error == null && existing.Any(r => r.TargetKind != LoadoutTargetKind.Section || r.PerMember || r.Role != roles[0]))
                error = "Conflicts with an existing per-inventory or restricted rule.";
            if (error == null)
                draft.Add(new LoadoutRecord
                {
                    GroupId = groupId, SupplyGroupId = supplyId, ReturnGroupId = string.Empty,
                    TargetKind = LoadoutTargetKind.Section, Role = roles[0], ItemDefinitionId = id.ToString(),
                    Amount = entry.Amount, PerMember = false, Policy = profile.Policy
                });
            else errors++;
            var row = new MyGuiControlTable.Row();
            row.AddCell(new MyGuiControlTable.Cell(definition == null ? entry.DefinitionId ?? $"Line {entry.Line}"
                    : DefinitionLabels.Item(definition.DisplayNameText, id.TypeId.ToString(), id.SubtypeName),
                toolTip: entry.DefinitionId));
            row.AddCell(new MyGuiControlTable.Cell(entry.Amount.ToString(CultureInfo.InvariantCulture)));
            row.AddCell(new MyGuiControlTable.Cell(error == null ? (existing.Length > 0 ? "Replace target" : "New target") : $"Line {entry.Line}: {error}",
                toolTip: UnifiedStorageHelp.Wrap(error)));
            preview.Add(row);
        }
        var validGroups = groupId.Length > 0 && supplyId.Length > 0 && groupId != supplyId;
        var resultingCount = profile.Loadouts.Count(r => !draft.Any(d => d.GroupId == r.GroupId &&
            d.Role == r.Role && d.ItemDefinitionId == r.ItemDefinitionId)) + draft.Count;
        status.Text = !validGroups ? "Choose different target and supply groups."
            : resultingCount > 256 ? "A profile supports at most 256 loadout rules."
            : errors > 0 ? $"{errors} invalid entries. Nothing will be imported until these are resolved."
            : $"{draft.Count} group-total targets. Maintenance and excess returns off.";
        import.Enabled = validGroups && resultingCount <= 256 && errors == 0 && draft.Count > 0;
    }

    private void Import()
    {
        RefreshPreview();
        if (!import.Enabled) return;
        foreach (var rule in draft)
        {
            profile.Loadouts.RemoveAll(r => r.GroupId == rule.GroupId && r.Role == rule.Role && r.ItemDefinitionId == rule.ItemDefinitionId);
            profile.Loadouts.Add(rule);
        }
        saved();
        CloseScreen();
    }

    private sealed class BomEditor : MyGuiControlMultilineEditableText
    {
        public BomEditor() : base(new Vector2(0, -0.14f), new Vector2(0.72f, 0.14f),
            textScale: 0.72f, contents: new StringBuilder(), drawScrollbarH: false,
            textBoxAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP)
        { Name = "BomText"; TextWrap = true; }

        public void Paste()
        {
            // Use the native paste path, including LinuxCompat's async clipboard handling.
            m_selection.SelectAll(this);
            m_selection.PasteText(this);
        }
    }
}
