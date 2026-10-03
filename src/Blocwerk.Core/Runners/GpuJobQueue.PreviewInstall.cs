// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Runners;

/// <summary>
/// Installing a pending preview (<c>WallCaptureProcessor.InstallPreviewAsync</c>): claimed under the processor's view
/// lock, only while <see cref="GpuJobPreviews.MayInstall"/> holds, only over the view it was uploaded to replace, and only
/// for the very file that is still pending. Installed, the file moves to <see cref="GpuJob.InstalledPreviewPath"/>.
/// </summary>
public sealed partial class GpuJobQueue
{
    /// <summary>The job as it is now, or null.</summary>
    public async Task<GpuJob?> FindAsync(Guid jobId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        return await db.GpuJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
    }

    /// <summary>Jobs whose delivered preview still waits to be installed (for the worker's start).</summary>
    public async Task<List<Guid>> PendingPreviewsAsync(CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var candidates = await db.GpuJobs.AsNoTracking().Where(j => j.PreviewPath != null && j.InstalledAt == null).ToListAsync(ct);
        return candidates.Where(GpuJobPreviews.IsPending).Select(j => j.Id).ToList();
    }

    /// <summary>
    /// Claims the install of the pending preview <paramref name="path"/> at <paramref name="step"/> (the caller holds the
    /// view lock and swaps the view right after). False when <see cref="GpuJobPreviews.MayInstall"/> no longer holds, a
    /// newer preview replaced it, or the model's view changed since it arrived (a final result, a re-finish, a server
    /// training). The older installed preview's file goes.
    /// </summary>
    public async Task<bool> TryMarkPreviewInstalledAsync(Guid jobId, int step, string path, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var job = await db.GpuJobs.AsNoTracking().Where(j => j.Id == jobId)
            .Select(j => new { j.GeometryModelId, j.PreviewBaseSplatId, j.InstalledPreviewPath }).FirstOrDefaultAsync(ct);
        if (job is null || await CurrentViewAsync(db, job.GeometryModelId, ct) != job.PreviewBaseSplatId)
        {
            return false;
        }

        var marked = await db.GpuJobs
            .Where(j => j.Id == jobId && j.PreviewPath == path && j.PreviewStep == step && j.InstalledAt == null
                        && j.Status != GpuJobStatus.Succeeded && j.Status != GpuJobStatus.Failed && j.Status != GpuJobStatus.Cancelled
                        && (j.PreviewInstalledStep == null || j.PreviewInstalledStep < step))
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.PreviewInstalledStep, step)
                    .SetProperty(j => j.InstalledPreviewPath, path)
                    .SetProperty(j => j.PreviewPath, (string?)null)
                    .SetProperty(j => j.PreviewFinishJobId, (string?)null),
                ct) > 0;
        if (marked && job.InstalledPreviewPath != path)
        {
            DeleteQuietly(job.InstalledPreviewPath, jobId);
        }

        return marked;
    }

    /// <summary>A pending preview that could not be finished is dropped, if it is still the pending one (its step stays taken).</summary>
    public async Task DropPreviewAsync(Guid jobId, int step, string path, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var dropped = await db.GpuJobs.Where(j => j.Id == jobId && j.PreviewStep == step && j.PreviewPath == path)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.PreviewPath, (string?)null).SetProperty(j => j.PreviewFinishJobId, (string?)null), ct);
        if (dropped > 0)
        {
            DeleteQuietly(path, jobId);
        }
    }

    /// <summary>
    /// The preview (step, total steps) that is the capture's view because its newest job's final result was never
    /// installed, or null.
    /// </summary>
    public async Task<(int Step, int? Total)?> InstalledPreviewAsync(Guid captureId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var job = await db.GpuJobs.AsNoTracking().Where(j => j.CaptureId == captureId)
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => new { j.PreviewInstalledStep, j.TotalSteps, j.InstalledAt, j.Status })
            .FirstOrDefaultAsync(ct);
        return job is { PreviewInstalledStep: { } step, InstalledAt: null } && job.Status != GpuJobStatus.Cancelled
            ? (step, job.TotalSteps)
            : null;
    }
}
