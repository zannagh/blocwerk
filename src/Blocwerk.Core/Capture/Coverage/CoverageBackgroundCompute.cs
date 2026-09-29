// <copyright file="CoverageBackgroundCompute.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>
/// Computes a capture's missing coverage report off the request: once per capture at a time (every page load asks
/// again while it runs) and one capture at a time overall, so opening several pages never piles up CPU work. A
/// computation that failed is not started again for <see cref="RetryAfterFailure"/>.
/// </summary>
internal static class CoverageBackgroundCompute
{
    /// <summary>How long a failed capture is left alone.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<Guid, Task> InFlight = new();
    private static readonly ConcurrentDictionary<Guid, DateTimeOffset> Failed = new();
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>The running computation of a capture, or null when none runs.</summary>
    /// <param name="captureId">The capture.</param>
    /// <returns>The task.</returns>
    public static Task? Pending(Guid captureId) => InFlight.GetValueOrDefault(captureId);

    /// <summary>Starts the computation unless one runs or failed recently.</summary>
    /// <param name="captureId">The capture.</param>
    /// <param name="compute">The computation (it must not depend on the caller's request).</param>
    /// <param name="logger">Where a failure is logged.</param>
    /// <returns>True when the report is being computed (now or already).</returns>
    public static bool Ensure(Guid captureId, Func<Task> compute, ILogger logger)
    {
        if (Failed.TryGetValue(captureId, out var failedAt) && DateTimeOffset.UtcNow - failedAt < RetryAfterFailure)
        {
            return false;
        }

        var done = new TaskCompletionSource();
        if (InFlight.TryAdd(captureId, done.Task))
        {
            _ = Task.Run(() => RunAsync(captureId, compute, logger, done));
        }

        return true;
    }

    private static async Task RunAsync(Guid captureId, Func<Task> compute, ILogger logger, TaskCompletionSource done)
    {
        try
        {
            await OneAtATime.WaitAsync();
            try
            {
                await compute();
                Failed.TryRemove(captureId, out _);
            }
            finally
            {
                OneAtATime.Release();
            }
        }
        catch (Exception ex)
        {
            Failed[captureId] = DateTimeOffset.UtcNow;
            logger.LogWarning(ex, "Capture {CaptureId}: the coverage report could not be computed", captureId);
        }
        finally
        {
            InFlight.TryRemove(captureId, out _);
            done.SetResult();
        }
    }
}
