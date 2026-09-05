using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Sandbox.Graphics.GUI;
using Shared.Companion;
using VRageMath;

namespace ClientPlugin.UI;

internal sealed class CompanionJobScreen : UnifiedStorageScreen
{
    private static readonly List<CompanionJobScreen> pending = new();
    public static bool HasPending => pending.Any(job => !job.finished && !job.closed);
    private readonly long anchor, terminal;
    private readonly Guid id;
    private readonly Func<bool> canContinue;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private long next;
    private bool finished, closed, cancelRequested, closingRequested, outcomeUnknown;
    private MyGuiControlLabel status, details, duration;
    private MyGuiControlButton cancel;
    private string statusText = "Waiting for job status...", detailText = "";
    private bool succeeded;
    private CompanionJobScreen(long anchor, long terminal, Guid id, Func<bool> canContinue)
        : base("Server utility job", new Vector2(0.76f, 0.36f))
    { this.anchor = anchor; this.terminal = terminal; this.id = id; this.canContinue = canContinue; }

    public static void Start(long anchor, long terminal, Guid id, Func<bool> canContinue) =>
        pending.Add(new CompanionJobScreen(anchor, terminal, id, canContinue));

    public static void UpdatePending()
    {
        foreach (var job in pending.ToArray())
        {
            job.Poll();
            if (job.closed || job.finished && job.succeeded)
            {
                pending.Remove(job);
                if (!job.closed) Sandbox.ModAPI.MyAPIGateway.Utilities?.ShowNotification("Unified Storage: " + job.statusText, 3500);
            }
            else if (job.finished || job.outcomeUnknown || job.elapsed.Elapsed.TotalSeconds >= 2)
            {
                pending.Remove(job);
                MyGuiSandbox.AddScreen(job);
            }
        }
    }

    public static void ClearPending()
    {
        foreach (var job in pending.ToArray()) job.CloseScreen(true);
        pending.Clear();
    }
    protected override void CreateControls()
    {
        var note = Label("Closing stops remaining work; completed moves are kept.", new Vector2(-0.33f, -0.09f));
        note.TextScale = 0.55f; Controls.Add(note);
        status = Label(statusText, new Vector2(-0.33f, -0.04f)); Controls.Add(status);
        details = Label(detailText, new Vector2(-0.33f, 0.005f)); details.TextScale = 0.55f; Controls.Add(details);
        details.IsAutoEllipsisEnabled = true; details.Size = new Vector2(0.66f, 0.03f);
        status.TextScale = 0.6f;
        status.IsAutoEllipsisEnabled = true; status.Size = new Vector2(0.66f, 0.03f);
        duration = Label("", new Vector2(-0.33f, 0.05f)); duration.TextScale = 0.55f; Controls.Add(duration);
        cancel = Button("Cancel job", new Vector2(-0.16f, 0.12f), () =>
        { outcomeUnknown = false; cancelRequested = true; }, 0.20f);
        Controls.Add(cancel);
        Controls.Add(Button("Close", new Vector2(0.16f, 0.12f), () => CloseScreen()));
    }
    public override bool Update(bool hasFocus)
    {
        var result = base.Update(hasFocus);
        if (status == null || closed) return result;
        Poll();
        status.Text = statusText;
        status.SetToolTip(UnifiedStorageHelp.Wrap(statusText));
        details.Text = detailText;
        details.SetToolTip(UnifiedStorageHelp.Wrap(detailText));
        duration.Text = $"Elapsed: {elapsed.Elapsed:mm\\:ss}";
        cancel.Enabled = !finished && !cancelRequested;
        if (cancelRequested && !finished) status.Text = "Stopping: waiting for server acknowledgement...";
        if (finished && succeeded) CloseScreen();
        return result;
    }
    private void Poll()
    {
        if (closed || finished) return;
        if (!outcomeUnknown && canContinue?.Invoke() == false) cancelRequested = true;
        if (!outcomeUnknown && (cancelRequested || Stopwatch.GetTimestamp() >= next) && Plugin.Instance?.Companion?.Busy == false)
            Query(cancelRequested);
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
                    statusText = $"{receipt.State}: {receipt.CompletedItems} completed; {receipt.Mutations} changes; {receipt.Failure}";
                    detailText = receipt.Detail ?? "";
                    finished = receipt.State != UtilityJobState.Running;
                    succeeded = receipt.State == UtilityJobState.Complete;
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
        statusText = reason + ". Job may still be running.";
        detailText = "Cancellation was not confirmed. Check the inventories before retrying.";
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
