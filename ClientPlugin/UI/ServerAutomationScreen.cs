using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Graphics.GUI;
using Shared.Companion;
using VRageMath;

namespace ClientPlugin.UI;

internal sealed class ServerAutomationScreen : UnifiedStorageScreen
{
    private readonly SharedScopeProfile snapshot;
    private readonly Action<CompanionCapabilities> apply;
    private readonly Action<ShipAction?> run;
    private readonly Dictionary<CompanionCapabilities, MyGuiControlCheckbox> checks = new();
    public ServerAutomationScreen(SharedScopeProfile snapshot, Action<CompanionCapabilities> apply, Action<ShipAction?> run) : base("Server automation ownership")
    { this.snapshot = snapshot; this.apply = apply; this.run = run; }

    protected override void CreateControls()
    {
        checks.Clear();
        Controls.Add(Label("Checked services belong to the server, even while paused by an operator.", new Vector2(-0.36f, -0.27f)));
        Controls.Add(Label("Published settings are used. Local edits must be published separately.", new Vector2(-0.36f, -0.21f)));
        var modes = new[] { CompanionCapabilities.RefineryAutomation, CompanionCapabilities.ComponentAutomation, CompanionCapabilities.LoadoutAutomation };
        var names = new[] { "Refinery input sorting", Plugin.Instance.Companion.Supports(CompanionCapabilities.CraftingTargets)
            ? "Crafting target maintenance" : "Crafting targets (legacy: components only)", "Maintained group loadouts" };
        for (var index = 0; index < modes.Length; index++)
        {
            var y = -0.10f + index * 0.075f;
            var allowed = Plugin.Instance.Companion.Supports(modes[index]);
            Controls.Add(Label(names[index] + (allowed ? "" : " (operator paused)"), new Vector2(-0.34f, y)));
            var check = new MyGuiControlCheckbox(new Vector2(0.31f, y)) { IsChecked = (snapshot.Automation & modes[index]) != 0 };
            check.SetToolTip($"Stage server ownership of {names[index].ToLowerInvariant()}. Apply ownership submits all checked services. Uses published settings; operator pauses do not return ownership to the client.");
            checks[modes[index]] = check; Controls.Add(check);
        }
        Controls.Add(Button("Sort now", new Vector2(-0.25f, 0.15f), () => run(ShipAction.SortRefineries), 0.15f));
        Controls.Add(Button("Craft now", new Vector2(-0.08f, 0.15f), () => run(ShipAction.QueueComponents), 0.15f));
        Controls.Add(Button("Loadouts now", new Vector2(0.10f, 0.15f), () => run(ShipAction.ApplyLoadouts), 0.18f));
        Controls.Add(Button("Status", new Vector2(0.29f, 0.15f), () => run(null), 0.13f));
        Controls.Add(Label("Owner-only. New settings have a 60-second handover delay.", new Vector2(-0.36f, 0.23f)));
        Controls.Add(Label("Unknown ownership pauses client automation; it never causes takeover.", new Vector2(-0.36f, 0.27f)));
        Controls.Add(Button("Apply ownership", new Vector2(-0.15f, 0.33f), () =>
        {
            var modes = CompanionCapabilities.None;
            foreach (var pair in checks) if (pair.Value.IsChecked) modes |= pair.Key;
            MyGuiSandbox.AddScreen(MyGuiSandbox.CreateMessageBox(buttonType: MyMessageBoxButtonsType.YES_NO,
                messageText: new StringBuilder("Change server ownership for the fetched profile revision?\nThis can start unattended work using the published settings."),
                callback: result => { if (result == MyGuiScreenMessageBox.ResultEnum.YES) { apply(modes); CloseScreen(); } }));
        }, 0.22f));
        Controls.Add(Button("Cancel", new Vector2(0.17f, 0.33f), () => CloseScreen()));
    }
}
