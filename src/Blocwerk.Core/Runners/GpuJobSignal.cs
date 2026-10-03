// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Runners;

/// <summary>Wakes long-polling <c>claim</c> calls when a job becomes claimable (single-instance app).</summary>
public sealed class GpuJobSignal
{
    private TaskCompletionSource pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Wakes every waiter.</summary>
    public void Pulse() => Interlocked.Exchange(ref pulse, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    /// <summary>Completes on the next <see cref="Pulse"/> or after <paramref name="timeout"/>.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        var current = Volatile.Read(ref pulse).Task;
        try
        {
            await current.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            // Nothing new: the caller re-checks anyway.
        }
    }
}
