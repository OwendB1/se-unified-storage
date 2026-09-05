using System;
using System.Diagnostics;
using Sandbox.Graphics.GUI;
using Shared.Companion;
using VRageMath;

namespace ClientPlugin.UI;

internal sealed class CompanionJobScreen : UnifiedStorageScreen
{
    private readonly long anchor, terminal;
    private readonly Guid id;
    private readonly Func<bool> canContinue;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private long next;
    private bool finished, closed, cancelRequested, closingRequested, outcomeUnknown;
    private MyGuiControlLabel status, details, duration;
    private MyGuiControlButton cancel;
    public CompanionJobScreen(long anchor, long terminal, Guid id, Func<bool> canContinue = null) : base("Server utility job")
    { this.anchor = anchor; this.terminal = terminal; this.id = id; this.canContinue = canContinue; }
    protected override void CreateControls()
    {
        Controls.Add(Label("Closing this window requests cancellation of remaining work.", new Vector2(-0.36f, -0.25f)));
        Controls.Add(Label("Cancel stops future steps; it does not roll back accepted transfers.", new Vector2(-0.36f, -0.18f)));
        status = Label("Waiting for job status...", new Vector2(-0.36f, -0.04f)); Controls.Add(status);
        details = Label("", new Vector2(-0.36f, 0.05f)); details.TextScale = 0.55f; Controls.Add(details);
        details.IsAutoEllipsisEnabled = true; details.Size = new Vector2(0.72f, 0.04f);
        status.TextScale = 0.6f;
        status.IsAutoEllipsisEnabled = true; status.Size = new Vector2(0.72f, 0.04f);
        duration = Label("", new Vector2(-0.36f, 0.13f)); Controls.Add(duration);
        cancel = Button("Cancel job", new Vector2(-0.16f, 0.30f), () =>
        { outcomeUnknown = false; cancelRequested = true; }, 0.20f);
        Controls.Add(cancel);
        Controls.Add(Button("Close", new Vector2(0.16f, 0.30f), () => CloseScreen()));
    }
    public override bool Update(bool hasFocus)
    {
        var result = base.Update(hasFocus);
        if (status == null || closed) return result;
        if (!outcomeUnknown && canContinue?.Invoke() == false) cancelRequested = true;
        duration.Text = $"Elapsed: {elapsed.Elapsed:mm\\:ss}";
        cancel.Enabled = !finished && !cancelRequested;
        if (cancelRequested && !finished) status.Text = "Stopping: waiting for server acknowledgement...";
        if (!finished && !outcomeUnknown && (cancelRequested || Stopwatch.GetTimestamp() >= next) && !Plugin.Instance.Companion.Busy)
            Query(cancelRequested);
        return result;
    }
    private void Query(bool cancel)
    {
        next = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        if (!Plugin.Instance.Companion.Request(cancel ? MessageKind.CancelJob : MessageKind.JobStatus, anchor, terminal, null,
            ProfileCodec.Encode(new UtilityJobReceipt { Id = id }), response =>
            {
                if (closed) return;
                if (response.Code != ResultCode.Ok)
                { Unknown("Server returned " + response.Code); return; }
                try
                {
                    var receipt = ProfileCodec.Decode<UtilityJobReceipt>(response.Body);
                    if (receipt.Id != id) throw new InvalidOperationException("Wrong job receipt.");
                    status.Text = $"{receipt.State}: {receipt.CompletedItems} completed; {receipt.Mutations} changes; {receipt.Failure}";
                    status.SetToolTip(status.Text);
                    details.Text = receipt.Detail ?? "";
                    details.SetToolTip(UnifiedStorageHelp.Wrap(receipt.Detail ?? "Server-confirmed progress; total work is not estimated."));
                    finished = receipt.State != UtilityJobState.Running;
                    // A status response may arrive after Close was clicked. Do not
                    // discard the queued cancellation while waiting for that response.
                    if (cancel || finished) cancelRequested = false;
                    if (finished)
                    {
                        elapsed.Stop();
                        if (closingRequested) CloseScreen();
                    }
                }
                catch (Exception) { Unknown("Invalid job result"); }
            })) Unknown("Companion unavailable or busy");
    }
    private void Unknown(string reason)
    {
        outcomeUnknown = true;
        cancelRequested = false;
        status.Text = reason + ". Job may still be running.";
        details.Text = "Cancellation was not confirmed. Do not start replacement work blindly.";
        if (closingRequested) CloseScreen();
    }
    public override bool CloseScreen(bool isUnloading = false)
    {
        if (!finished && !outcomeUnknown && !isUnloading)
        {
            closingRequested = cancelRequested = true;
            return false;
        }
        if (isUnloading && !finished && !outcomeUnknown && Plugin.Instance?.Companion?.Busy == false)
            Query(true);
        if (!finished)
            Sandbox.ModAPI.MyAPIGateway.Utilities?.ShowNotification(
                "Unified Storage: server job cancellation unconfirmed; it may still be running.", 7000, "Red");
        closed = true;
        return base.CloseScreen(isUnloading);
    }
}
