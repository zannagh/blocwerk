// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture.Retention;

/// <summary>
/// Walls with 3D work in flight, which the model retention leaves alone until the next sweep: a capture being processed,
/// a re-solve or re-render mark, a 3D runner job not yet over or installed (a re-finish puts it back there), an open "Update panels + 3D" run.
/// </summary>
internal static class RetentionBusyWalls
{
    public static async Task<HashSet<Guid>> ListAsync(BlocwerkDbContext db, CancellationToken ct)
    {
        var captures = await db.WallCaptures
            .Where(c => c.Status == WallCaptureStatus.Queued || c.Status == WallCaptureStatus.Detecting
                        || c.Status == WallCaptureStatus.Solving || c.Status == WallCaptureStatus.Texturing
                        || c.Status == WallCaptureStatus.Splatting)
            .Select(c => c.WallId)
            .ToListAsync(ct);
        var redo = await CaptureRedoMarks.Redoing(db.WallCaptures).Select(c => c.WallId).ToListAsync(ct);
        var jobs = await db.GpuJobs
            .Where(j => j.Status == GpuJobStatus.Queued || j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running
                        || (j.Status == GpuJobStatus.Succeeded && j.InstalledAt == null))
            .Select(j => j.WallId)
            .ToListAsync(ct);
        var refreshes = await db.WallRefreshes
            .Where(r => r.Status != WallRefreshStatus.Done && r.Status != WallRefreshStatus.Failed && r.Status != WallRefreshStatus.Discarded)
            .Select(r => r.WallId)
            .ToListAsync(ct);
        return captures.Concat(redo).Concat(jobs).Concat(refreshes).ToHashSet();
    }
}
