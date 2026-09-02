using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage;

namespace ClientPlugin.Automation;

public sealed class ProductionQueueExecutor
{
    private readonly Queue<ProductionRequest> requests = new();
    private InFlight active;

    public int PendingCount => requests.Count + (active == null ? 0 : 1);

    public void Enqueue(IEnumerable<ProductionRequest> additions)
    {
        foreach (var request in additions ?? Enumerable.Empty<ProductionRequest>())
            if (request.Runs > MyFixedPoint.Zero)
                requests.Enqueue(request);
    }

    public void Update()
    {
        if (active != null)
        {
            var current = Queued(active.Request);
            if (current > active.Before)
            {
                active = null;
                return;
            }
            if (DateTime.UtcNow >= active.Deadline)
            {
                Plugin.Instance.Log.Warning("Assembler queue acknowledgement timed out for {0}",
                    active.Request.Assembler.DisplayNameText);
                requests.Clear();
                active = null;
            }
            return;
        }
        if (requests.Count == 0)
            return;
        var request = requests.Dequeue();
        if (request.Assembler.Closed || request.Assembler.DisassembleEnabled || request.Assembler.IsSlave ||
            !request.Assembler.UseConveyorSystem || !request.Assembler.CanUseBlueprint(request.Blueprint) ||
            !request.Assembler.HasPlayerAccess(MySession.Static?.LocalPlayerId ?? 0L) ||
            request.Assembler.CurrentState is MyAssembler.StateEnum.InventoryFull or MyAssembler.StateEnum.MissingItems)
            return;
        if (Queued(request) != request.QueuedAtPlan)
        {
            Plugin.Instance.Log.Info("Skipped stale component target request for {0}; queue changed after planning",
                request.Assembler.DisplayNameText);
            return;
        }
        if (request.Assembler.Queue.Count() >= Sandbox.Game.World.MySession.Static.MaxProductionQueueLength)
            return;
        var before = Queued(request);
        request.Assembler.InsertQueueItemRequest(-1, request.Blueprint, request.Runs);
        active = new InFlight(
            request,
            before,
            DateTime.UtcNow.AddMilliseconds(Math.Max(500, Config.Current.AcknowledgementTimeoutMilliseconds)));
    }

    public void Clear()
    {
        requests.Clear();
        active = null;
    }

    private static MyFixedPoint Queued(ProductionRequest request) =>
        request.Assembler.Queue.Where(item => ReferenceEquals(item.Blueprint, request.Blueprint))
            .Aggregate(MyFixedPoint.Zero, (sum, item) => sum + item.Amount);

    private sealed class InFlight
    {
        public InFlight(ProductionRequest request, MyFixedPoint before, DateTime deadline)
        {
            Request = request;
            Before = before;
            Deadline = deadline;
        }

        public ProductionRequest Request { get; }
        public MyFixedPoint Before { get; }
        public DateTime Deadline { get; }
    }
}
