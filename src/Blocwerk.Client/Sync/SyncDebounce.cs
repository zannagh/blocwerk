// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Sync;

/// <summary>
/// One refetch per burst, <see cref="Delay"/> after the burst's first ask (plan §1): the docs index 10 s after
/// structural kinds, the marks 2 s after mine kinds, as Web's asset-data.js and asset-mine.js do for the Server pages.
/// The work of the burst's first ask runs; later asks join it.
/// </summary>
internal sealed class SyncDebounce
{
    private readonly ISyncClock clock;
    private CancellationTokenSource? pending;

    public SyncDebounce(ISyncClock clock, TimeSpan delay)
    {
        this.clock = clock;
        Delay = delay;
    }

    /// <summary>Gets the docs index's debounce: 10 s.</summary>
    public static TimeSpan IndexDelay { get; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets the marks' debounce: 2 s.</summary>
    public static TimeSpan MineDelay { get; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets this debounce's delay.</summary>
    public TimeSpan Delay { get; }

    /// <summary>Gets a value indicating whether a refetch is waiting.</summary>
    public bool Pending => pending is not null;

    /// <summary>Asks for <paramref name="fire"/>: the first ask of a burst starts the timer, the others join it.</summary>
    /// <param name="fire">The work, captured when the burst began.</param>
    public void Schedule(Func<Task> fire)
    {
        if (pending is not null)
        {
            return;
        }

        var cancel = new CancellationTokenSource();
        pending = cancel;
        _ = RunAsync(cancel, fire);
    }

    /// <summary>Drops a pending refetch.</summary>
    public void Cancel()
    {
        pending?.Cancel();
        pending = null;
    }

    private async Task RunAsync(CancellationTokenSource cancel, Func<Task> fire)
    {
        try
        {
            await clock.Delay(Delay, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(pending, cancel))
            {
                pending = null;
            }

            cancel.Dispose();
        }

        await fire();
    }
}
