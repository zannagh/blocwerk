// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// The "running step" mark the progress API reads (<see cref="CaptureFollowUpRecord.Running"/>): written conditionally like
/// every record change, dropped by the step's entry, by a graceful shutdown in the step, when the chain finds the capture's
/// model no longer active, when a mark's recoveries run out, and on startup for marks a dead process left behind.
/// </summary>
public sealed partial class CaptureFollowUpChain
{
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
                .Where(c => c.FollowUpJson != null && c.FollowUpJson.Contains(CaptureFollowUpRecord.RunningMarker))
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

    /// <summary>Drops the running mark of <paramref name="key"/> (any step when null), whatever model the capture points at.</summary>
    private Task<CaptureFollowUpRecord?> ClearRunningAsync(Guid captureId, string? key, CancellationToken ct) =>
        CaptureFollowUpRecordStore.UpdateAsync(
            dbContextFactory.CreateDbContext,
            captureId,
            r => r.Running is { } running && (key is null || running.Key == key) ? r with { Running = null } : r,
            ct);
}
