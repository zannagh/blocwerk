// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests.Pulse;

/// <summary>A clock whose time and one-shot timers move only when the test advances it, so the throttle is stepped exactly.</summary>
public sealed class PulseTestClock : TimeProvider
{
    private readonly Lock gate = new();
    private readonly List<PulseTestTimer> timers = [];
    private DateTimeOffset now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new PulseTestTimer(callback, state, GetUtcNow() + dueTime);
        lock (gate)
        {
            timers.Add(timer);
        }

        return timer;
    }

    /// <summary>Moves the time on and fires every timer that fell due, on this thread.</summary>
    public void Advance(TimeSpan by)
    {
        List<PulseTestTimer> due;
        lock (gate)
        {
            now += by;
            due = timers.Where(t => !t.Done && t.DueAt <= now).ToList();
            timers.RemoveAll(t => t.Done || t.DueAt <= now);
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }
}
