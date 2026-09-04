using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClientPlugin.Inventory;
using ClientPlugin.Profiles;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using Shared.Companion;
using VRageMath;

namespace ClientPlugin.UI;

internal sealed class SharedProfileScreen : UnifiedStorageScreen
{
    private readonly MechanicalInventorySession session;
    private readonly ScopeProfile local;
    private readonly Companion.CompanionClient client;
    private readonly Action adopted;
    private SharedScopeProfile snapshot;
    private bool fetched;
    private bool closed;
    private MyGuiControlLabel status;
    private MyGuiControlLabel revision;
    private MyGuiControlCheckbox faction;
    private MyGuiControlButton fetch;
    private MyGuiControlButton publish;
    private MyGuiControlButton adopt;
    private MyGuiControlButton inspect;

    public SharedProfileScreen(MechanicalInventorySession session, ScopeProfile profile, Action adopted) : base("Shared ship profile")
    {
        this.session = session; local = profile; client = Plugin.Instance.Companion;
        this.adopted = adopted;
        client.ProfileChanged += Changed;
    }

    protected override void CreateControls()
    {
        Controls.Add(Label("Optional server storage. Transfers and automation still run client-side.", new Vector2(-0.36f, -0.29f)));
        Controls.Add(Label("Fetch first. Nothing is adopted or published automatically.", new Vector2(-0.36f, -0.23f)));
        status = Label("Waiting for companion discovery...", new Vector2(-0.36f, -0.14f)); Controls.Add(status);
        revision = Label("No shared snapshot fetched.", new Vector2(-0.36f, -0.08f)); Controls.Add(revision);
        fetch = Button("Fetch current", new Vector2(-0.19f, 0.01f), Fetch, 0.2f); Controls.Add(fetch);
        inspect = Button("Inspect fetched", new Vector2(0.19f, 0.01f), Inspect, 0.22f); Controls.Add(inspect);
        Controls.Add(Label("Let my faction read the published profile", new Vector2(-0.36f, 0.1f)));
        faction = new MyGuiControlCheckbox(new Vector2(0.31f, 0.1f)); Controls.Add(faction);
        Controls.Add(Label("Publish includes targets, priorities, groups, exclusions and loadouts.", new Vector2(-0.36f, 0.17f)));
        Controls.Add(Label("Adopt keeps private groups; existing local settings are backed up.", new Vector2(-0.36f, 0.22f)));
        publish = Button("Publish local", new Vector2(-0.19f, 0.28f), Publish, 0.22f); Controls.Add(publish);
        adopt = Button("Adopt fetched", new Vector2(0.19f, 0.28f), Adopt, 0.22f); Controls.Add(adopt);
        Controls.Add(Button("Close", new Vector2(0, 0.35f), () => CloseScreen()));
    }

    public override bool Update(bool hasFocus)
    {
        var result = base.Update(hasFocus);
        if (fetch == null) return result;
        var client = Plugin.Instance?.Companion;
        var available = client?.Available == true && !client.Busy;
        fetch.Enabled = available;
        publish.Enabled = available && fetched && (snapshot == null || snapshot.OwnerIdentityId == MySession.Static?.LocalPlayerId);
        adopt.Enabled = available && fetched && snapshot != null;
        inspect.Enabled = snapshot != null;
        if (client?.Available != true) status.Text = "Companion unavailable. Standalone inventory remains active.";
        return result;
    }

    private void Fetch()
    {
        try { Send(MessageKind.GetProfile, null); }
        catch (Exception exception) { status.Text = "Cannot fetch this scope; reopen the terminal."; Plugin.Instance.Log.Error(exception, "Shared profile fetch failed"); }
    }

    private void Changed(CompanionMessage message)
    {
        if (closed || status == null || !session.Scope.Grids.Any(g => g.EntityId == message.AnchorEntityId) ||
            snapshot != null && snapshot.Id == message.ProfileId && snapshot.Revision >= message.Revision) return;
        fetched = false;
        status.Text = "Shared profile changed. Fetch the current revision before continuing.";
    }

    private void Inspect()
    {
        var value = snapshot.Settings;
        var text = new StringBuilder();
        text.AppendLine($"Policy: {value.Policy}; faction-readable: {snapshot.FactionShared}");
        text.AppendLine("Server-owned automation: disabled in this companion version.");
        text.AppendLine($"Refinery priorities: {(value.RefineryPriority.Automatic ? "Automatic" : "Manual")}");
        text.AppendLine("Pinned: " + string.Join(", ", value.RefineryPriority.PinnedDefinitionIds));
        text.AppendLine("Manual: " + string.Join(", ", value.RefineryPriority.ManualDefinitionIds));
        text.AppendLine("\nComponent targets:");
        foreach (var target in value.ComponentTargets) text.AppendLine($"{target.DefinitionId}: {target.Amount}; blueprint: {target.BlueprintDefinitionId}");
        text.AppendLine("\nInventory groups (selectors, not stored membership):");
        foreach (var group in value.Groups)
            text.AppendLine($"{group.Name} [{group.Id}]: {group.Selector} {group.Family} {group.Value}; role: {(group.AllRoles ? "all" : group.Role.ToString())}; items: {group.ItemType} {group.ItemDefinitionId}");
        text.AppendLine("\nLoadouts:");
        foreach (var rule in value.Loadouts)
            text.AppendLine($"{rule.GroupId}: {rule.Amount} {rule.ItemDefinitionId} ({(rule.PerMember ? "per inventory" : "group total")}); supply: {rule.SupplyGroupId}; return: {rule.ReturnGroupId}; {rule.Policy}");
        text.AppendLine("\nManagement exclusions:");
        foreach (var record in value.InventoryManagement) text.AppendLine($"Block {record.BlockEntityId}, inventory {record.InventoryIndex}: {record.Flags}");
        MyAPIGateway.Utilities.ShowMissionScreen("Shared ship profile", "Revision ", snapshot.Revision.ToString(), text.ToString());
    }

    private void Publish()
    {
        Confirm("Publish all local ship settings to the server? Other authorized readers can fetch them.\n" +
                "Server-only groups are retained. This does not enable server automation.", () =>
        {
            var settings = ProfileCodec.Clone(local);
            if (snapshot != null)
                settings.Groups.AddRange(snapshot.Settings.Groups.Where(g => settings.Groups.All(localGroup => localGroup.Id != g.Id)).Select(g => g.Copy()));
            ProfileCodec.Validate(settings);
            Send(MessageKind.PublishProfile, ProfileCodec.Encode(new SharedScopeProfile
            { Settings = settings, FactionShared = faction.IsChecked }));
        });
    }

    private void Adopt()
    {
        Confirm("Adopt the fetched server settings? Matching settings will replace local values.\n" +
                "Private groups and their loadouts are kept. A local backup is written first.", () =>
        {
            var value = ProfileCodec.Clone(snapshot.Settings);
            var privateIds = new HashSet<string>(local.Groups.Where(group => value.Groups.All(shared => shared.Id != group.Id)).Select(g => g.Id));
            value.Groups.AddRange(local.Groups.Where(g => privateIds.Contains(g.Id)).Select(g => g.Copy()));
            value.Loadouts.AddRange(local.Loadouts.Where(rule => privateIds.Contains(rule.GroupId)));
            Plugin.Instance.Profiles.BackupBeforeAdoption();
            local.GroupSchemaVersion = value.GroupSchemaVersion; local.Groups = value.Groups;
            local.Policy = value.Policy; local.ComponentTargets = value.ComponentTargets;
            local.ComponentStartThreshold = value.ComponentStartThreshold;
            // Adoption is configuration, not permission to start unattended local loops.
            local.MaintainComponentTargets = false;
            value.RefineryPriority.AutoSortInputs = false;
            foreach (var rule in value.Loadouts) rule.Maintain = false;
            local.RefineryPriority = value.RefineryPriority;
            local.InventoryManagement = value.InventoryManagement; local.Loadouts = value.Loadouts;
            Plugin.Instance.Profiles.Save();
            session.MarkContentsDirty();
            adopted?.Invoke();
            status.Text = "Adopted. Local maintenance switches are off; enable them explicitly.";
        });
    }

    private void Send(MessageKind kind, byte[] body)
    {
        var scope = session.Refresh().Scope;
        var terminal = scope.InteractedEntity as MyTerminalBlock;
        if (terminal == null || !scope.Grids.Any(g => g.EntityId == terminal.CubeGrid.EntityId))
            terminal = scope.Inventories.Select(i => i.Owner).OfType<MyTerminalBlock>().FirstOrDefault();
        if (terminal == null) { status.Text = "No terminal endpoint available in this ship."; return; }
        var anchor = snapshot?.AnchorEntityId ?? local.ScopeAnchorEntityId;
        status.Text = "Waiting for server...";
        if (!Plugin.Instance.Companion.Request(kind, anchor, terminal.EntityId, snapshot, body, response =>
        {
            if (closed) return;
            if (response.Code == ResultCode.Ok || response.Code == ResultCode.Conflict && response.Body.Length != 0)
            {
                snapshot = ProfileCodec.Decode<SharedScopeProfile>(response.Body);
                ProfileCodec.Validate(snapshot.Settings);
                fetched = true; faction.IsChecked = snapshot.FactionShared;
                revision.Text = $"Revision {snapshot.Revision}; owner identity {snapshot.OwnerIdentityId}";
                status.Text = response.Code == ResultCode.Conflict ? "Revision changed. Review the fetched profile before publishing again." :
                    kind == MessageKind.PublishProfile ? "Published. Server persistence is queued for the next save flush." : "Fetched. Local settings unchanged.";
            }
            else if (response.Code == ResultCode.NotFound)
            {
                fetched = true; snapshot = null; revision.Text = "No server profile for this mechanical ship.";
                status.Text = "The ship owner can publish the first profile.";
            }
            else
            {
                fetched = false;
                status.Text = response.Code == ResultCode.UnknownOutcome ? "Outcome unknown. Fetch before attempting another publish." :
                    response.Code == ResultCode.Conflict ? "Multiple anchored profiles: resolve the mechanical merge first." : $"Server returned: {response.Code}";
            }
        })) status.Text = "Companion unavailable or another request is pending.";
    }

    private void Confirm(string text, Action action) => MyGuiSandbox.AddScreen(MyGuiSandbox.CreateMessageBox(
        buttonType: MyMessageBoxButtonsType.YES_NO, messageText: new StringBuilder(text), callback: answer =>
        {
            if (answer != MyGuiScreenMessageBox.ResultEnum.YES || closed) return;
            try { action(); }
            catch (Exception exception) { status.Text = "Operation failed; see the plugin log."; Plugin.Instance.Log.Error(exception, "Shared profile action failed"); }
        }));

    public override bool CloseScreen(bool isUnloading = false)
    {
        closed = true; client.ProfileChanged -= Changed;
        return base.CloseScreen(isUnloading);
    }
}
