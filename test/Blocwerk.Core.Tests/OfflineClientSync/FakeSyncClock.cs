// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Sync;

namespace Blocwerk.Core.Tests.OfflineClientSync;

/// <summary>A hand-driven clock: every wait is recorded and ends only when the test fires it (or it is cancelled).</summary>
internal sealed class FakeSyncClock : ISyncClock
{
    private readonly List<(TimeSpan Delay, TaskCompletionSource Done)> waits = [];

    public double Jitter { get; set; } = 0.5;

    /// <summary>Gets every wait asked for, in order.</summary>
    public List<TimeSpan> Asked { get; } = [];

    /// <summary>Gets the waits still running.</summary>
    public IReadOnlyList<TimeSpan> Running => waits.Where(w => !w.Done.Task.IsCompleted).Select(w => w.Delay).ToList();

    public Task Delay(TimeSpan delay, CancellationToken cancel)
    {
        Asked.Add(delay);
        var waiting = (Delay: delay, Done: new TaskCompletionSource());
        cancel.Register(() => waiting.Done.TrySetCanceled(cancel));
        waits.Add(waiting);
        return waiting.Done.Task;
    }

    public double NextJitter() => Jitter;

    /// <summary>Ends the oldest running wait of <paramref name="delay"/> (any, when null).</summary>
    public void Fire(TimeSpan? delay = null)
    {
        var waiting = waits.First(w => !w.Done.Task.IsCompleted && (delay is null || w.Delay == delay));
        waiting.Done.TrySetResult();
    }
}
