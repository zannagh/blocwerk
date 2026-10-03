// <copyright file="CoverageBackgroundCompute.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>
/// Computes a capture's missing coverage report off the request: once per capture at a time (every page load asks
/// again while it runs) and one capture at a time overall, so opening several pages never piles up CPU work. A
/// computation that failed is not started again for <see cref="RetryAfterFailure"/>. The key names what it is computed from
/// (the volumes' fingerprint), so a run started before the volumes changed is not taken for the current one.
/// </summary>
internal static class CoverageBackgroundCompute
{
    /// <summary>How long a failed capture is left alone.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<(Guid CaptureId, string Key), Task> InFlight = new();
    private static readonly ConcurrentDictionary<(Guid CaptureId, string Key), DateTimeOffset> Failed = new();
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>The running computation of a capture, or null when none runs.</summary>
    /// <param name="captureId">The capture.</param>
    /// <returns>The task.</returns>
    public static Task? Pending(Guid captureId) => InFlight.FirstOrDefault(e => e.Key.CaptureId == captureId).Value;

    /// <summary>Starts the computation unless one runs or failed recently.</summary>
    /// <param name="captureId">The capture.</param>
    /// <param name="compute">The computation (it must not depend on the caller's request).</param>
    /// <param name="logger">Where a failure is logged.</param>
    /// <param name="key">What it is computed from.</param>
    /// <returns>True when the report is being computed (now or already).</returns>
    public static bool Ensure(Guid captureId, Func<Task> compute, ILogger logger, string key = "")
    {
        var id = (captureId, key);
        if (Failed.TryGetValue(id, out var failedAt) && DateTimeOffset.UtcNow - failedAt < RetryAfterFailure)
        {
            return false;
        }

        var done = new TaskCompletionSource();
        if (InFlight.TryAdd(id, done.Task))
        {
            _ = Task.Run(() => RunAsync(id, compute, logger, done));
        }

        return true;
    }

    private static async Task RunAsync((Guid CaptureId, string Key) id, Func<Task> compute, ILogger logger, TaskCompletionSource done)
    {
        try
        {
            await OneAtATime.WaitAsync();
            try
            {
                await compute();
                Failed.TryRemove(id, out _);
            }
            finally
            {
                OneAtATime.Release();
            }
        }
        catch (Exception ex)
        {
            Failed[id] = DateTimeOffset.UtcNow;
            logger.LogWarning(ex, "Capture {CaptureId}: the coverage report could not be computed", id.CaptureId);
        }
        finally
        {
            InFlight.TryRemove(id, out _);
            done.SetResult();
        }
    }
}
