// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The re-solve and re-render marks (<see cref="CaptureRedoMarks"/>) keep the wall busy, so they must not outlive their
/// run: which captures a run of this process is working on (one run per capture and kind, so a re-queued mark never
/// starts a second one beside it), and clearing a mark retried a few times before it is left to the workers' idle rescan.
/// </summary>
public sealed partial class WallCaptureProcessor
{
    private static readonly TimeSpan[] ClearRetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];

    private readonly ConcurrentDictionary<Guid, byte> resolving = new();
    private readonly ConcurrentDictionary<Guid, byte> rerendering = new();

    /// <summary>Whether a run of this process is working on the capture's re-solve or texture re-render.</summary>
    /// <param name="captureId">The capture.</param>
    /// <returns>True while one runs.</returns>
    internal bool IsRedoing(Guid captureId) => resolving.ContainsKey(captureId) || rerendering.ContainsKey(captureId);

    /// <summary>Runs <paramref name="run"/> as the capture's only run of this kind; a second one returns at once.</summary>
    private static async Task RunOnceAsync(ConcurrentDictionary<Guid, byte> live, Guid captureId, Func<Task> run)
    {
        if (!live.TryAdd(captureId, 0))
        {
            return;
        }

        try
        {
            await run();
        }
        finally
        {
            live.TryRemove(captureId, out _);
        }
    }

    /// <summary>Writes a change that clears a mark, retried a few times: a failure here would leave the wall busy.</summary>
    private async Task ClearMarkAsync(Guid captureId, Action<WallCapture> change, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var db = dbContextFactory.CreateDbContext();
                var capture = await db.WallCaptures.FirstOrDefaultAsync(c => c.Id == captureId, ct);
                if (capture is null)
                {
                    return;
                }

                change(capture);
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (Exception ex) when (attempt < ClearRetryDelays.Length && ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Capture {CaptureId}: its re-solve/re-render mark could not be cleared; retrying", captureId);
                await Task.Delay(ClearRetryDelays[attempt], ct);
            }
        }
    }
}
