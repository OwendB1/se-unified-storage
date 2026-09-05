using System.Text;
using System.Collections.Generic;
using ClientPlugin.Profiles;

namespace ClientPlugin.UI;

internal static class UnifiedStorageHelp
{
    public static string ManagementState(InventoryManagementFlags flags)
    {
        var names = new List<string>();
        if ((flags & InventoryManagementFlags.ManualBlock) != 0) names.Add("Unmanaged");
        if ((flags & InventoryManagementFlags.ReservedInventory) != 0) names.Add("Reserved");
        if ((flags & InventoryManagementFlags.NoUnifiedCargoDestination) != 0) names.Add("Source Only");
        return names.Count == 0 ? "Managed" : string.Join(", ", names);
    }

    public static string Screen(string caption) => Wrap(caption switch
    {
        "Unified Storage members" => "Exclude inventories from bulk actions or stock counts. A dash indicates mixed values; an asterisk marks unsaved edits.",
        "Component targets" => "Set ship-wide component stock goals. Ctrl/Shift select multiple components; Save target sets their common quantity while preserving individual recipes. Recipe editing requires one row. Craft deficits uses all saved targets.",
        "Loadouts" => "Define desired contents for inventory groups. Rules choose their own supply, return group and distribution policy. Editing rules saves settings; Apply loadouts performs transfers.",
        "Edit loadout" => "Set stock targets and where to draw supplies or return excess. Maintained rules run automatically while connected.",
        "Refinery ore priority" => "Choose which ores refineries process first, automatically or with a manual order. Applies to this construct's refineries, including other conveyor networks. Sorting changes input order, not ore quantities.",
        "Inventory groups" => "Groups select live inventories and control their display order. They do not move items by themselves. Group edits, reordering and deletion save immediately.",
        "Edit inventory group" => "Any matching rule includes an inventory. Each rule's block, role and item filters stay together; overlaps count once.",
        "Edit group rule" => "All conditions in a rule must match. Terminal-group names automatically include new group members.",
        "Group actions" => "Settings for this block type.",
        "Shared ship profile" => "Optionally exchange settings with the server companion. Fetch reads without changing local settings. Publish and Adopt are explicit operations; neither happens automatically.",
        "Shared profile tools" => "Owner-only recovery and maintenance of server profiles. Binding, deleting and patching alter server settings after confirmation. Fetch again after changing them.",
        "Server automation ownership" => "Choose which services the server owns using published settings. Apply ownership submits changes after confirmation. Checked services remain server-owned while an operator pauses execution.",
        "Server utility job" => "Monitor server-confirmed progress and elapsed time. Cancel job or Close requests cancellation and waits for acknowledgement. Accepted transfers cannot be undone; loss of contact leaves the outcome unknown.",
        "Rebalance progress" => "Monitor confirmed local rebalance transfers and elapsed time. Progress counts processed item plans, not equal-duration steps. Cancel or Close stops further requests; a transfer already sent can still complete.",
        _ => null
    });

    public static string Button(string caption, string text) => Wrap((caption, text) switch
    {
        ("Unified Storage members", "Apply") => "Exclusions stay local until you publish a shared profile.",
        ("Unified Storage members", "Close") => null,
        ("Component targets", "Close") => "Close without saving the selected component's unsaved quantity or recipe. Maintenance and start-threshold settings are saved on close.",
        ("Server utility job", "Close") => "Stop remaining work and close after the server responds. If cancellation cannot be confirmed, a warning explains that the job may still run. Accepted transfers are not undone.",
        ("Rebalance progress", "Close") => "Cancel this rebalance batch and close. Other operations are unaffected; a request already sent may still complete.",
        ("Rebalance progress", "Cancel job") => "Stop this rebalance batch and keep its results visible. Completed transfers are not rolled back; any request already sent is still acknowledged.",
        ("Server automation ownership", "Close" or "Cancel") => "Discard unapplied ownership checkboxes and close. Already submitted actions are not cancelled.",
        ("Edit inventory group", "Apply") => "Maintained loadouts use the new rules on their next pass. An empty rule list matches nothing.",
        ("Edit inventory group", "Add rule") => "Rules are alternatives: matching any one is sufficient. Maximum 128 rules per group.",
        ("Edit inventory group", "Edit rule") => null,
        ("Edit inventory group", "Duplicate") => "Copies are independent. Overlapping rules never count an inventory or stack twice.",
        ("Edit inventory group", "Remove") => "An empty rule list matches nothing.",
        ("Edit group rule", "Save rule") => null,
        ("Edit loadout", "Apply") => "Save this rule and close. If Maintain locally is enabled, client maintenance may act on it; otherwise run Apply loadouts explicitly.",
        ("Edit inventory group" or "Edit group rule" or "Edit loadout", "Close" or "Cancel") => null,
        ("Shared profile tools", "Delete selected") => "After confirmation, delete the selected owned server-profile binding. The server creates a recovery archive; inventory contents are not deleted.",
        ("Inventory groups", "Move up") => "Move selected groups earlier, keeping their relative order, and save immediately. Physical items are unchanged.",
        ("Inventory groups", "Move down") => "Move selected groups later, keeping their relative order, and save immediately. Physical items are unchanged.",
        ("Refinery ore priority", "Move up") => "Raise selected ores in the manual or pinned list, keeping their relative order, and save. In automatic mode unpinned selected ores are first pinned.",
        ("Refinery ore priority", "Move down") => "Lower selected ores in the manual or pinned list, keeping their relative order, and save. In automatic mode unpinned selected ores are first pinned.",
        ("Server automation ownership", "Sort now") => "Request a one-time server input sort using the published refinery priorities. Does not apply the ownership checkboxes.",
        (_, "Close") => null,
        (_, "Save target") => "Save the entered quantity on every selected component, plus maintenance settings. Recipe changes apply only with one row selected. Zero disables selected goals. Save before changing selection.",
        (_, "Craft deficits") => "Queue missing output for all saved component targets, accounting for current stock and existing assembler queues. Save an edited component target first; this action only saves the global maintenance settings.",
        (_, "New rule") => null,
        (_, "Edit selected") => null,
        (_, "Delete selected") => "Delete all selected loadout rules and save immediately. Does not remove inventory items or undo transfers already submitted.",
        (_, "Apply loadouts") => "Immediately plan transfers for all loadout rules in this scope, not just selected rows, using each rule's supply, return group and policy. Respects exclusions, capacity and conveyor access.",
        (_, "New") => null,
        (_, "Edit") => null,
        (_, "Duplicate") => "Create and immediately save a separate copy of every selected group. Does not copy or move inventory items.",
        (_, "Delete") => "Delete all selected groups and save immediately. Referencing loadout rules remain visible but paused; they are not redirected to other inventories.",
        (_, "Restore defaults") => "After confirmation, reset built-in groups and restore missing presets. Custom groups and loadout rules are kept.",
        (_, "Shared profile") => "Open optional server-profile exchange. Fetch and inspect before explicitly publishing or adopting settings.",
        (_, "Pin / unpin") => "Pin all selected ores if any are unpinned; otherwise unpin all selected ores. Saves immediately. Pinned ores take priority over automatically ordered ores.",
        (_, "Sort now") => "Immediately sort refinery input stacks using saved ore priorities. Keeps ore quantities unchanged; does not fill or drain refineries.",
        (_, "Group loadouts") => "Configure item targets, supply and excess returns for this inventory group.",
        (_, "Ship ore priority") => "Choose which ores this construct's refineries process first. Sort now applies the order to their inputs.",
        (_, "Ship component targets") => "Open ship-wide component goals and supported assembler recipes. Crafting uses saved targets.",
        (_, "Fetch current") => "Read this ship's current server profile and revision. Local settings remain unchanged. Requires an available companion.",
        (_, "Inspect fetched") => "Show the fetched profile's settings, revision and ownership without adopting or publishing anything. Fetch first.",
        (_, "Publish local") => "After confirmation, publish local groups, priorities, targets, exclusions and loadouts to this ship's server profile. Requires ownership and a current fetched revision.",
        (_, "Adopt fetched") => "After confirmation, copy fetched server settings into the local profile. Existing local settings are backed up and private groups are preserved.",
        (_, "Server automation") => "Configure ownership of unattended server services or run published settings once. Requires a fetched profile and companion support.",
        (_, "Profile tools") => "Owner-only binding recovery, deletion and section patching for server profiles.",
        (_, "List / refresh") => "Lists only server profiles owned by you.",
        (_, "Previous page") => null,
        (_, "Next page") => null,
        (_, "Bind to this ship") => "After confirmation, move the selected owned profile's binding to this mechanical ship. Rebinding disables server automation; it does not move inventories.",
        (_, "Patch section") => "After confirmation, replace the selected section of the fetched server profile with local settings. Other sections remain unchanged. Groups replaces the whole group list, including removal of server-only groups.",
        (_, "Craft now") => "Request a one-time server crafting pass using published component targets. Does not publish local edits or apply ownership checkboxes.",
        (_, "Loadouts now") => "Request a one-time server transfer pass using published loadout rules. Does not publish local edits or apply ownership checkboxes.",
        (_, "Status") => "Read server automation ownership and execution status without changing settings or starting work.",
        (_, "Apply ownership") => "After confirmation, save all ownership checkboxes to the server profile. Uses published settings and a 60-second handover delay; may enable unattended work.",
        (_, "Cancel job") => "Ask the server to stop future job steps. Completed transfers are not rolled back; wait for acknowledgement before submitting replacement work.",
        _ => null
    });

    public static string Field(string label) => Wrap(label switch
    {
        "Unmanaged" => "Excludes the whole block from plugin bulk actions. Native conveyor pulls and vanilla transfers are unaffected.",
        "Reserved" => "Skipped by bulk actions and excluded from available stock counts. Contents remain visible.",
        "Source Only" => "Cargo can supply items but will not receive deposits. Other exclusions still apply.",
        "Display name" => "Name of this local inventory view. Renaming a view does not rename blocks or terminal block groups.",
        "Select blocks by" => "Choose a live selector: family, type, definition, terminal-group name, exact block or supported recipe output. Names match new group members; exact-block selectors do not.",
        "Selection (resolved on this ship)" => "Choose the selector value to save. A missing value stays unresolved rather than silently selecting other blocks.",
        "Inventory role" => "Restrict the view or rule to compatible inventory roles, such as production input or output. Block constraints and conveyor routes are still enforced.",
        "Material / item category" => "Restrict this view to an item category such as ore, ingot or component. Combined with the block, role and exact-item filters.",
        "Exact material / item (optional)" => "Optionally show only one item definition. All items removes this filter; it does not override block constraints.",
        "Target group" => "Inventories that should hold this loadout. The group resolves live; missing groups pause the rule rather than broadening its targets.",
        "Supply group" => "Inventories allowed to supply missing items. None disables filling. Target inventories belonging to other loadout rules are not used as supply.",
        "Excess return group" => "Inventories allowed to receive stock above the target. None disables excess returns; it does not discard items.",
        "Item / material" => "Choose an item accepted by the target group and inventory role. Modded definitions are discovered from the game.",
        "Target quantity" => "Desired stock, not an amount to add. Per inventory applies this quantity to each member; otherwise it is the total for the group.",
        "Distribution policy" => "Choose how the rule divides stock: existing stacks first, filling containers first, or balancing each item. Capacity and constraints still limit placement.",
        "Per inventory" => "Checked: each eligible inventory gets the target quantity. Unchecked: the target is shared across the whole group.",
        "Maintain locally" => "After Apply, periodically maintain this rule while the client is connected. Server-owned loadout maintenance suppresses duplicate local work.",
        "Include non-working" => "Allow eligible non-working target blocks to receive their loadout. This does not bypass ownership, exclusions, constraints or conveyor checks.",
        "Automatic priority" => "Save immediately: order unpinned ores using definition-derived resource scarcity, after pinned ores. Uncheck to use the manual order.",
        "Auto-sort inputs" => "Save immediately: allow maintained refinery-input sorting while connected, unless the server owns this service. Does not pull more ore or drain outputs.",
        "Target" => "Desired total stock of the selected component. Zero disables its goal. Save target before selecting another component; typing alone does not save.",
        "Blueprint" => "Choose a recipe supported by this ship's assemblers for the selected component. Save target stores the recipe; choosing it does not queue production.",
        "Maintain targets locally" => "Periodically queue deficits while connected, unless the server owns component maintenance. Saved by Save target, Craft deficits or closing this window.",
        "Start threshold" => "Start crafting below this fraction of the target (0.01–1). Current stock plus queued output count toward the goal. Saved with global maintenance settings.",
        _ => null
    });

    // Keen's native tooltips measure text but do not word-wrap it.
    public static string Wrap(string text)
    {
        if (text == null) return null;
        var result = new StringBuilder();
        foreach (var paragraph in text.Split('\n'))
        {
            if (result.Length > 0) result.Append('\n');
            var column = 0;
            foreach (var word in paragraph.Split(' '))
            {
                if (column > 0)
                {
                    if (column + word.Length + 1 > 88) { result.Append('\n'); column = 0; }
                    else { result.Append(' '); column++; }
                }
                result.Append(word);
                column += word.Length;
            }
        }
        return result.ToString();
    }
}
