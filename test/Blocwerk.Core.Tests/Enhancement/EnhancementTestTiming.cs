// <copyright file="EnhancementTestTiming.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Tests.Enhancement;

/// <summary>
/// A clock whose timers fire only when the test says so: the step watchdog then fires after the step did what the test
/// needs it to have done, however slow the machine is (CI under load), instead of racing a real 150 ms timer.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ManualTimer> timers = [];

    /// <summary>The timers created and not yet fired or disposed.</summary>
    public int Pending
    {
        get
        {
            lock (gate)
            {
                return timers.Count(t => !t.Done);
            }
        }
    }

    /// <inheritdoc/>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state);
        lock (gate)
        {
            timers.Add(timer);
        }

        return timer;
    }

    /// <summary>Fires every live timer once (the watchdog's "time is up").</summary>
    public void FireAll()
    {
        ManualTimer[] due;
        lock (gate)
        {
            due = [.. timers.Where(t => !t.Done)];
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }
}

/// <summary>A timer of <see cref="ManualTimeProvider"/>.</summary>
public sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
{
    /// <summary>Fired or disposed.</summary>
    public bool Done { get; private set; }

    public bool Change(TimeSpan dueTime, TimeSpan period) => !Done;

    public void Fire()
    {
        if (Done)
        {
            return;
        }

        Done = true;
        callback(state);
    }

    public void Dispose() => Done = true;

    public ValueTask DisposeAsync()
    {
        Done = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Waiting for a state the test cannot get a signal for, with a generous bound that fails loudly.</summary>
internal static class Eventually
{
    /// <summary>The bound: far above anything a loaded CI machine needs, so only a real hang fails.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>Polls <paramref name="condition"/> until it holds; throws when it never does within <see cref="Bound"/>.</summary>
    /// <param name="condition">The state to wait for.</param>
    /// <param name="what">What is waited for, for the failure message.</param>
    /// <returns>When it holds.</returns>
    public static async Task UntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Waited {Bound.TotalSeconds:0} s for {what}.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Awaits a signal with the same bound, failing loudly instead of hanging the run.</summary>
    /// <param name="signal">The signal.</param>
    /// <param name="what">What it stands for.</param>
    /// <returns>When it is set.</returns>
    public static async Task SignalAsync(Task signal, string what)
    {
        try
        {
            await signal.WaitAsync(Bound);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Waited {Bound.TotalSeconds:0} s for {what}.");
        }
    }
}
