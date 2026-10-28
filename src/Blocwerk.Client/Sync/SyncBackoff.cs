// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Sync;

/// <summary>
/// A wall's network failures in a row: each backs off (5/30/120 s ±20 %, cut short by <c>online</c>), the first shows
/// the <c>offline</c> bubble, and the first answer after them shows <c>online</c>.
/// </summary>
internal sealed class SyncBackoff
{
    private readonly ISyncClock clock;
    private readonly ISyncTransport transport;
    private readonly SyncWait wait;
    private int failures;

    public SyncBackoff(ISyncClock clock, ISyncTransport transport, SyncWait wait)
    {
        this.clock = clock;
        this.transport = transport;
        this.wait = wait;
    }

    /// <summary>A fresh start: no failures counted.</summary>
    public void Reset() => failures = 0;

    /// <summary>The server could not be reached: show offline once, then wait.</summary>
    /// <returns>A task that completes after the wait.</returns>
    public Task FailedAsync()
    {
        failures++;
        if (failures == 1)
        {
            transport.ShowStatus("offline");
        }

        return wait.WaitAsync(SyncDelays.Network(failures, clock.NextJitter()), isNetwork: true);
    }

    /// <summary>The server answered: show online when it had not.</summary>
    public void Reached()
    {
        if (failures > 0)
        {
            transport.ShowStatus("online");
        }

        failures = 0;
    }
}
