// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Sync;

/// <summary>The browser's clock: <see cref="Task.Delay(TimeSpan, CancellationToken)"/> and <see cref="Random.Shared"/>.</summary>
public sealed class SyncClock : ISyncClock
{
    /// <inheritdoc />
    public Task Delay(TimeSpan delay, CancellationToken cancel) => Task.Delay(delay, cancel);

    /// <inheritdoc />
    public double NextJitter() => Random.Shared.NextDouble();
}
