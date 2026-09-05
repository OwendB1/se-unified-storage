using System;
using System.Linq;
using System.Text;
using ClientPlugin.Inventory;
using ClientPlugin.Profiles;
using Sandbox.Game.Entities.Cube;
using Sandbox.Graphics.GUI;
using Shared.Companion;
using VRageMath;

namespace ClientPlugin.UI;

internal sealed class ProfileToolsScreen : UnifiedStorageScreen
{
    private readonly MechanicalInventorySession session;
    private readonly ScopeProfile local;
    private readonly SharedScopeProfile snapshot;
    private OwnedProfileList catalog = new();
    private int page;
    private bool closed;
    private MyGuiControlCombobox profiles, fields;
    private MyGuiControlLabel status;
    private readonly Action changed;
    public ProfileToolsScreen(MechanicalInventorySession session, ScopeProfile local, SharedScopeProfile snapshot, Action changed) : base("Shared profile tools")
    { this.session = session; this.local = local; this.snapshot = snapshot; this.changed = changed; }
    protected override void CreateControls()
    {
        Controls.Add(Label("Explicit owner-only operations. Fetch again after a successful change.", new Vector2(-0.36f, -0.29f)));
        status = Label("List your profiles to recover, move or delete a binding.", new Vector2(-0.36f, -0.23f)); Controls.Add(status);
        profiles = new MyGuiControlCombobox(new Vector2(-0.10f, -0.15f), new Vector2(0.50f, 0.035f)); Controls.Add(profiles);
        profiles.SetToolTip("Select an owned server profile from the current catalog page. Selection alone does not adopt, bind or delete it; use the explicit action buttons.");
        for (var index = 0; index < catalog.Profiles.Count; index++)
        {
            var value = catalog.Profiles[index];
            profiles.AddItem(index, $"Anchor {value.Anchor} / rev {value.Revision}" + (value.AnchorMissing ? " (missing)" : ""));
        }
        if (catalog.Profiles.Count > 0) profiles.SelectItemByIndex(0);
        Controls.Add(Button("List / refresh", new Vector2(0.25f, -0.15f), List, 0.16f));
        Controls.Add(Button("Previous page", new Vector2(-0.21f, -0.08f), () => { page = Math.Max(0, page - 1); List(); }, 0.20f));
        Controls.Add(Button("Next page", new Vector2(0.06f, -0.08f), () => { page = Math.Min(15, page + 1); List(); }, 0.20f));
        Controls.Add(Button("Bind to this ship", new Vector2(-0.19f, 0.00f), () => Lifecycle(false), 0.23f));
        Controls.Add(Button("Delete selected", new Vector2(0.19f, 0.00f), () => Lifecycle(true), 0.22f));
        Controls.Add(Label("Patch one settings section of the fetched ship profile from local settings:", new Vector2(-0.36f, 0.09f)));
        fields = new MyGuiControlCombobox(new Vector2(-0.13f, 0.16f), new Vector2(0.43f, 0.035f)); Controls.Add(fields);
        fields.SetToolTip("Choose which server settings section Patch section will replace from local settings. Groups replaces the entire group list, not just matching entries.");
        foreach (ProfileFields field in Enum.GetValues(typeof(ProfileFields)))
            if (field is not (ProfileFields.None or ProfileFields.All)) fields.AddItem((long)field, field.ToString());
        fields.SelectItemByIndex(0);
        Controls.Add(Button("Patch section", new Vector2(0.23f, 0.16f), Patch, 0.19f));
        Controls.Add(Label("Groups replaces that section exactly, including removal of server-only groups.", new Vector2(-0.36f, 0.23f)));
        Controls.Add(Label("Rebinding disables automation. Delete writes a separate recovery archive.", new Vector2(-0.36f, 0.28f)));
        Controls.Add(Button("Close", new Vector2(0, 0.35f), () => CloseScreen()));
    }
    private OwnedProfileInfo Selected => profiles.GetSelectedKey() >= 0 && profiles.GetSelectedKey() < catalog.Profiles.Count ? catalog.Profiles[(int)profiles.GetSelectedKey()] : null;
    private void List() => Send(new ProfileOperation { Operation = ProfileOperationKind.ListOwned, Page = page }, null, response =>
    {
        if (response.Code != ResultCode.Ok) return;
        catalog = ProfileCodec.Decode<OwnedProfileList>(response.Body); RecreateControls(false);
        status.Text = $"Owned profiles, page {page + 1}.";
    });
    private void Lifecycle(bool delete)
    {
        var selected = Selected;
        if (selected == null) { status.Text = "Select a listed profile first."; return; }
        Confirm(delete ? "Delete this shared profile? A recovery archive is written first." :
            "Move this profile binding to the currently accessed ship? Server automation is disabled by this operation.", () =>
            Send(new ProfileOperation { Operation = delete ? ProfileOperationKind.Delete : ProfileOperationKind.Rebind },
                new SharedScopeProfile { Id = selected.Id, Revision = selected.Revision }, _ => changed()));
    }
    private void Patch()
    {
        if (snapshot == null) { status.Text = "Fetch the current ship profile before patching."; return; }
        var field = (ProfileFields)fields.GetSelectedKey();
        Confirm($"Replace the server's {field} section with the current local section? Other sections stay unchanged.", () =>
        {
            // Send only the selected region; do not transmit an unrelated large profile for a small patch.
            var source = ProfileCodec.Clone(local);
            var partial = new ScopeProfile { GroupSchemaVersion = InventoryGroupRecord.SchemaVersion };
            switch (field)
            {
                case ProfileFields.Policy: partial.Policy = source.Policy; break;
                case ProfileFields.Groups: partial.Groups = source.Groups; break;
                case ProfileFields.Loadouts: partial.Loadouts = source.Loadouts; break;
                case ProfileFields.Components:
                    partial.ComponentTargets = source.ComponentTargets; partial.ComponentStartThreshold = source.ComponentStartThreshold;
                    partial.MaintainComponentTargets = source.MaintainComponentTargets; break;
                case ProfileFields.Refineries: partial.RefineryPriority = source.RefineryPriority; break;
                case ProfileFields.Exclusions: partial.InventoryManagement = source.InventoryManagement; break;
            }
            Send(new ProfileOperation { Operation = ProfileOperationKind.Patch, Fields = field, Settings = partial }, snapshot, _ => changed());
        });
    }
    private void Send(ProfileOperation operation, SharedScopeProfile binding, Action<CompanionMessage> done)
    {
        try
        {
            var scope = session.Refresh().Scope;
            var terminal = scope.InteractedEntity as MyTerminalBlock ?? scope.Inventories.Select(member => member.Owner).OfType<MyTerminalBlock>().FirstOrDefault();
            if (terminal == null || !Plugin.Instance.Companion.Supports(CompanionCapabilities.ProfileOperations))
            { status.Text = "No accessible endpoint or server profile-tools capability."; return; }
            if (!Plugin.Instance.Companion.Request(MessageKind.ProfileOperation, scope.AnchorGrid.EntityId, terminal.EntityId, binding, ProfileCodec.Encode(operation), response =>
            {
                if (closed) return;
                status.Text = "Server returned " + response.Code + ". Fetch before further changes.";
                if (response.Code == ResultCode.Ok) done(response);
            })) status.Text = "Companion unavailable or busy.";
        }
        catch (Exception exception) { Plugin.Instance.Log.Error(exception, "Profile tool failed"); status.Text = "Operation failed; see log. Nothing is retried automatically."; }
    }
    private void Confirm(string message, Action action) => MyGuiSandbox.AddScreen(MyGuiSandbox.CreateMessageBox(
        buttonType: MyMessageBoxButtonsType.YES_NO, messageText: new StringBuilder(message), callback: result =>
        { if (result == MyGuiScreenMessageBox.ResultEnum.YES && !closed) action(); }));
    public override bool CloseScreen(bool isUnloading = false) { closed = true; return base.CloseScreen(isUnloading); }
}
