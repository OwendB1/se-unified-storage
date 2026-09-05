using System;

namespace ClientPlugin.Transfers;

internal sealed class OperationRateBudget
{
    private double credits = 1;
    private double? previous;

    public bool Available(double seconds, int perSecond)
    {
        var rate = Math.Max(1, perSecond);
        // Keep at most one nominal frame of credit, never an idle-time backlog.
        credits = Math.Min(Math.Max(1, Math.Ceiling(rate / 60d)),
            credits + Math.Max(0, seconds - (previous ?? seconds)) * rate);
        previous = seconds;
        return credits >= 1;
    }

    public void Consume() => credits -= 1;
}
