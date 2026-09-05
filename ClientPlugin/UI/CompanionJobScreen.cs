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
    private long next;
    private bool finished, closed;
    private MyGuiControlLabel status, details;
    public CompanionJobScreen(long anchor, long terminal, Guid id) : base("Server utility job")
    { this.anchor = anchor; this.terminal = terminal; this.id = id; }
    protected override void CreateControls()
    {
        Controls.Add(Label("This job is bounded. Closing this screen does not cancel it.", new Vector2(-0.36f, -0.25f)));
        Controls.Add(Label("Cancel stops future steps; it does not roll back accepted transfers.", new Vector2(-0.36f, -0.18f)));
        status = Label("Waiting for job status...", new Vector2(-0.36f, -0.04f)); Controls.Add(status);
        details = Label("", new Vector2(-0.36f, 0.05f)); details.TextScale = 0.55f; Controls.Add(details);
        Controls.Add(Button("Cancel job", new Vector2(-0.16f, 0.30f), () => Query(true), 0.20f));
        Controls.Add(Button("Close", new Vector2(0.16f, 0.30f), () => CloseScreen()));
    }
    public override bool Update(bool hasFocus)
    {
        var result = base.Update(hasFocus);
        if (!closed && !finished && status != null && Stopwatch.GetTimestamp() >= next && !Plugin.Instance.Companion.Busy) Query(false);
        return result;
    }
    private void Query(bool cancel)
    {
        next = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3;
        if (!Plugin.Instance.Companion.Request(cancel ? MessageKind.CancelJob : MessageKind.JobStatus, anchor, terminal, null,
            ProfileCodec.Encode(new UtilityJobReceipt { Id = id }), response =>
            {
                if (closed) return;
                if (response.Code != ResultCode.Ok)
                { status.Text = "Job status: " + response.Code + ". Do not restart blindly."; finished = true; return; }
                try
                {
                    var receipt = ProfileCodec.Decode<UtilityJobReceipt>(response.Body);
                    status.Text = $"{receipt.State}: {receipt.CompletedItems} completed; {receipt.Mutations} changes; {receipt.Failure}";
                    details.Text = receipt.Detail ?? "";
                    finished = receipt.State != UtilityJobState.Running;
                }
                catch (Exception) { status.Text = "Invalid job result. Outcome unknown."; finished = true; }
            })) status.Text = "Companion unavailable or busy.";
    }
    public override bool CloseScreen(bool isUnloading = false)
    { closed = true; return base.CloseScreen(isUnloading); }
}
