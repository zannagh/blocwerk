// <copyright file="WallEnhancementStepTracker.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Enhancement;

/// <summary>
/// Follows one long step's progress (photos done of all) and turns it into an expected end: the pace since the step
/// (re)started, so photos a resumed search reads back from its checkpoint do not make the estimate optimistic. The end
/// goes to the deploy gate's ETA (<see cref="IDeployBusyEta"/>) and the progress to the card.
/// </summary>
public sealed class WallEnhancementStepTracker : IProgress<WallEnhancementProgress>
{
    private readonly Guid runId;
    private readonly IDeployBusyEta? eta;
    private readonly Func<DateTimeOffset> clock;
    private readonly object gate = new();
    private DateTimeOffset? firstAt;
    private int firstDone;

    /// <summary>Initializes a new instance of the <see cref="WallEnhancementStepTracker"/> class.</summary>
    /// <param name="runId">The run (the ETA's key).</param>
    /// <param name="eta">Where the expected end goes, if anywhere.</param>
    /// <param name="clock">The clock (tests).</param>
    public WallEnhancementStepTracker(Guid runId, IDeployBusyEta? eta, Func<DateTimeOffset>? clock = null)
    {
        this.runId = runId;
        this.eta = eta;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The last progress reported, if any.</summary>
    public WallEnhancementProgress? Last { get; private set; }

    /// <summary>When the step is expected to be done, once the pace is known.</summary>
    public DateTimeOffset? ExpectedEnd { get; private set; }

    /// <inheritdoc/>
    public void Report(WallEnhancementProgress value)
    {
        lock (gate)
        {
            var now = clock();
            Last = value;
            if (firstAt is null)
            {
                (firstAt, firstDone) = (now, value.Done);
                return;
            }

            var done = value.Done - firstDone;
            if (done <= 0 || value.Total <= value.Done)
            {
                return;
            }

            var perUnit = (now - firstAt.Value) / done;
            ExpectedEnd = now + (perUnit * (value.Total - value.Done));
            eta?.Set(runId, ExpectedEnd);
        }
    }

    /// <summary>The step ended: the deploy gate no longer waits for it.</summary>
    public void Clear() => eta?.Set(runId, null);
}
