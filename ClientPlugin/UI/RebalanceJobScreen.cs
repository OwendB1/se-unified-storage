using System;
using System.Diagnostics;
using System.Linq;
using ClientPlugin.Transfers;
using Sandbox.Definitions;
using Sandbox.Graphics.GUI;
using VRageMath;

namespace ClientPlugin.UI;

internal sealed class RebalanceJobScreen : UnifiedStorageScreen
{
    private readonly TransferOperationResult[] operations;
    private readonly Stopwatch elapsed;
    private MyGuiControlLabel progress, detail, outcome;
    private MyGuiControlButton cancel;
    private bool cancelled;

    public RebalanceJobScreen(TransferOperationResult[] operations, Stopwatch elapsed)
        : base("Rebalance progress", new Vector2(0.76f, 0.36f))
    { this.operations = operations; this.elapsed = elapsed; }

    protected override void CreateControls()
    {
        var note = Label("Closing stops remaining work; completed moves are kept.", new Vector2(-0.33f, -0.09f));
        note.TextScale = 0.55f;
        Controls.Add(note);
        progress = Label("", new Vector2(-0.33f, -0.04f)); Controls.Add(progress);
        progress.TextScale = 0.6f;
        progress.IsAutoEllipsisEnabled = true; progress.Size = new Vector2(0.66f, 0.03f);
        detail = Label("", new Vector2(-0.33f, 0.005f));
        detail.TextScale = 0.55f;
        detail.IsAutoEllipsisEnabled = true; detail.Size = new Vector2(0.66f, 0.03f);
        Controls.Add(detail);
        outcome = Label("", new Vector2(-0.33f, 0.05f)); outcome.TextScale = 0.55f; Controls.Add(outcome);
        cancel = Button("Cancel job", new Vector2(-0.16f, 0.12f), Cancel, 0.20f);
        Controls.Add(cancel);
        Controls.Add(Button("Close", new Vector2(0.16f, 0.12f), () => CloseScreen()));
    }

    public override bool Update(bool hasFocus)
    {
        var result = base.Update(hasFocus);
        if (progress == null) return result;
        var pending = operations.Count(item => item.Status is TransferOperationStatus.Queued or TransferOperationStatus.Running);
        var done = operations.Length - pending;
        progress.Text = $"{(pending == 0 ? cancelled ? "Stopped" : "Finished" : cancelled ? "Stopping" : "Balancing")}: " +
                        $"{done} / {operations.Length} item plans ({done * 100 / Math.Max(1, operations.Length)}%) · {elapsed.Elapsed:mm\\:ss}";
        var current = operations.FirstOrDefault(item => item.Status == TransferOperationStatus.Running);
        detail.Text = current == null ? "" :
            $"{MyDefinitionManager.Static.GetPhysicalItemDefinition(current.Plan.ItemId)?.DisplayNameText ?? current.Plan.ItemId.SubtypeName}: " +
            $"{current.MovedAmount} / {current.Plan.RequestedAmount} confirmed";
        detail.SetToolTip(current?.Message ?? "Confirmed quantities update after the server acknowledges a transfer. No time estimate is assumed.");
        var complete = operations.Count(item => item.Status == TransferOperationStatus.Complete);
        var stopped = operations.Count(item => item.Status == TransferOperationStatus.Cancelled);
        outcome.Text = $"{complete} complete · {done - complete - stopped} partial/failed · {stopped} cancelled";
        outcome.SetToolTip(UnifiedStorageHelp.Wrap(string.Join("\n", operations
            .Where(item => item.Status is TransferOperationStatus.Partial or TransferOperationStatus.Failed or TransferOperationStatus.TimedOut)
            .Take(5).Select(item => item.Message))));
        cancel.Enabled = pending > 0 && !cancelled;
        if (pending == 0) elapsed.Stop();
        if (pending == 0 && complete == operations.Length) CloseScreen();
        return result;
    }

    private void Cancel()
    {
        cancelled = true;
        Plugin.Instance?.Transfers?.Cancel(operations);
    }

    public override bool CloseScreen(bool isUnloading = false)
    {
        Cancel();
        return base.CloseScreen(isUnloading);
    }
}
