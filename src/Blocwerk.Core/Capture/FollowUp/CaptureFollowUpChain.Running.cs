// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// The "running step" mark the progress API reads (<see cref="CaptureFollowUpRecord.Running"/>): written conditionally like
/// every record change, dropped by the step's entry, by a graceful shutdown in the step, when the chain finds the capture's
/// model no longer active, when a mark's recoveries run out, and on startup for marks a dead process left behind.
/// </summary>
public sealed partial class CaptureFollowUpChain
{
    /// <summary>How long a shutdown waits for a step's running mark to be dropped.</summary>
    private static readonly TimeSpan ShutdownCleanupTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Drops the running marks written before <paramref name="startedBefore"/> (this process's start: no step of an earlier
    /// process can still run). Returns how many it dropped.
    /// </summary>
    /// <param name="dbContextFactory">The root context factory.</param>
    /// <param name="startedBefore">Marks older than this are stale.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The number of marks dropped.</returns>
    public static async Task<int> ClearStaleRunningAsync(RootDbContextFactory dbContextFactory, DateTimeOffset startedBefore, CancellationToken ct)
    {
        List<Guid> marked;
        await using (var db = dbContextFactory.CreateDbContext())
        {
            marked = await db.WallCaptures.AsNoTracking()
                .Where(c => c.FollowUpRunningSince != null && c.FollowUpRunningSince < startedBefore)
                .Select(c => c.Id)
                .ToListAsync(ct);
        }

        var dropped = 0;
        foreach (var id in marked)
        {
            var stale = false;
            await CaptureFollowUpRecordStore.UpdateAsync(
                dbContextFactory.CreateDbContext,
                id,
                r =>
                {
                    stale = r.Running is { } running && running.StartedAt < startedBefore;
                    return stale ? r with { Running = null } : r;
                },
                ct);
            dropped += stale ? 1 : 0;
        }

        return dropped;
    }

    /// <summary>Marks <paramref name="running"/> as the capture's running step (only while it points at the run's model).</summary>
    private Task<CaptureFollowUpRecord?> MarkRunningAsync(CaptureFollowUpContext context, CaptureFollowUpRunning running, CancellationToken ct) =>
        UpdateRecordAsync(context.CaptureId, context.ModelId, r => r.Starting(running), ct);

    /// <summary>
    /// Drops the running mark of <paramref name="key"/> (any step when null; only the one started at
    /// <paramref name="startedAt"/> when given), whatever model the capture points at. Touches nothing but the mark.
    /// </summary>
    private Task<CaptureFollowUpRecord?> ClearRunningAsync(Guid captureId, string? key, CancellationToken ct, DateTimeOffset? startedAt = null) =>
        CaptureFollowUpRecordStore.UpdateAsync(
            dbContextFactory.CreateDbContext,
            captureId,
            r => r.Running is { } running && (key is null || running.Key == key) && (startedAt is null || running.StartedAt == startedAt)
                ? r with { Running = null }
                : r,
            ct);

    /// <summary>
    /// The cleanup of a step stopped by a shutdown: bounded and never throwing, so it cannot hide the cancellation (the
    /// startup sweep drops a mark it could not).
    /// </summary>
    private async Task ClearRunningOnShutdownAsync(Guid captureId, CaptureFollowUpRunning running)
    {
        using var timeout = new CancellationTokenSource(ShutdownCleanupTimeout);
        try
        {
            await ClearRunningAsync(captureId, running.Key, timeout.Token, running.StartedAt);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Capture {CaptureId}: the running mark of follow-up step {Step} was not dropped on shutdown", captureId, running.Key);
        }
    }

    /// <summary>
    /// An entry the store refused (the capture was re-pointed meanwhile) leaves its step's mark behind: it is dropped by
    /// capture id, whatever model the capture points at now, and only if it is still that step's mark.
    /// </summary>
    private async Task DropOrphanedMarkAsync(Guid captureId, CaptureFollowUpEntry entry, CancellationToken ct)
    {
        if (entry.StartedAt is { } started)
        {
            await ClearRunningAsync(captureId, entry.Key, ct, started);
        }
    }
}
