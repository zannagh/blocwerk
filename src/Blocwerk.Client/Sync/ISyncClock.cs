// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Sync;

/// <summary>The sync loop's time and chance: <see cref="SyncClock"/> in the browser, a hand-driven fake in the tests.</summary>
public interface ISyncClock
{
    /// <summary>Waits <paramref name="delay"/>; a cancelled wait throws <see cref="OperationCanceledException"/>.</summary>
    /// <param name="delay">The wait.</param>
    /// <param name="cancel">Cancels the wait.</param>
    /// <returns>A task that completes after the wait.</returns>
    Task Delay(TimeSpan delay, CancellationToken cancel);

    /// <summary>A jitter value in [0, 1).</summary>
    /// <returns>The value.</returns>
    double NextJitter();
}
