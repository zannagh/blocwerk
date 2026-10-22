// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests.Pulse;

/// <summary>One timer of <see cref="PulseTestClock"/>: fires once, unless disposed first.</summary>
public sealed class PulseTestTimer(TimerCallback callback, object? state, DateTimeOffset dueAt) : ITimer
{
    public DateTimeOffset DueAt { get; } = dueAt;

    public bool Done { get; private set; }

    public bool Change(TimeSpan dueTime, TimeSpan period) => !Done;

    public void Fire()
    {
        if (!Done)
        {
            Done = true;
            callback(state);
        }
    }

    public void Dispose() => Done = true;

    public ValueTask DisposeAsync()
    {
        Done = true;
        return ValueTask.CompletedTask;
    }
}
