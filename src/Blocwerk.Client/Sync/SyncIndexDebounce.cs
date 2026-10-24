// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Sync;

/// <summary>
/// Structural kinds (the comment kinds included) come in bursts while someone sets: the docs index is refetched once,
/// <see cref="Delay"/> after the first event of a burst (plan §1), as Web's asset-data.js does for the Server pages.
/// </summary>
internal sealed class SyncIndexDebounce
{
    private readonly ISyncClock clock;
    private readonly Func<Task> fire;
    private CancellationTokenSource? pending;

    public SyncIndexDebounce(ISyncClock clock, Func<Task> fire)
    {
        this.clock = clock;
        this.fire = fire;
    }

    /// <summary>Gets the debounce: 10 s.</summary>
    public static TimeSpan Delay { get; } = TimeSpan.FromSeconds(10);

    /// <summary>Asks for a refetch: the first ask of a burst starts the timer, the others join it.</summary>
    public void Schedule()
    {
        if (pending is not null)
        {
            return;
        }

        var cancel = new CancellationTokenSource();
        pending = cancel;
        _ = RunAsync(cancel);
    }

    /// <summary>Drops a pending refetch.</summary>
    public void Cancel()
    {
        pending?.Cancel();
        pending = null;
    }

    private async Task RunAsync(CancellationTokenSource cancel)
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
