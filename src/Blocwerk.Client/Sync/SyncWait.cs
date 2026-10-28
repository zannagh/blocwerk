// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Sync;

/// <summary>
/// One cancellable wait of the sync loop. A network backoff that the browser's <c>online</c> cuts short is followed by
/// a random 0–10 s instead of the rest of the backoff (plan §1), so a gym's phones do not retry together.
/// </summary>
internal sealed class SyncWait
{
    private readonly ISyncClock clock;
    private CancellationTokenSource? current;
    private bool network;
    private bool online;

    public SyncWait(ISyncClock clock)
    {
        this.clock = clock;
    }

    /// <summary>Waits <paramref name="delay"/>; a network wait ends early on <see cref="Online"/>, then waits 0–10 s.</summary>
    /// <param name="delay">The wait.</param>
    /// <param name="isNetwork">True for a network backoff (the only kind <c>online</c> cuts short).</param>
    /// <returns>A task that completes after the wait, or at once after <see cref="Cancel"/>.</returns>
    public async Task WaitAsync(TimeSpan delay, bool isNetwork)
    {
        if (await RunAsync(delay, isNetwork) && online)
        {
            online = false;
            await RunAsync(SyncDelays.Online(clock.NextJitter()), false);
        }
    }

    /// <summary>The browser came back online: a running network backoff ends, for a jittered retry.</summary>
    public void Online()
    {
        if (current is { } wait && network)
        {
            online = true;
            wait.Cancel();
        }
    }

    /// <summary>Ends any wait at once (the tab hid, the wall closed).</summary>
    public void Cancel()
    {
        online = false;
        current?.Cancel();
    }

    // True when the wait was cut short by Online.
    private async Task<bool> RunAsync(TimeSpan delay, bool isNetwork)
    {
        using var cancel = new CancellationTokenSource();
        current = cancel;
        network = isNetwork;
        try
        {
            await clock.Delay(delay, cancel.Token);
            return false;
        }
        catch (OperationCanceledException)
        {
            return online;
        }
        finally
        {
            if (ReferenceEquals(current, cancel))
            {
                current = null;
                network = false;
            }
        }
    }
}
