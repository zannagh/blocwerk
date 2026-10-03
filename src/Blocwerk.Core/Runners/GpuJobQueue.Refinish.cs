// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Runners;

/// <summary>
/// Re-finishing a capture's trained view without training (<see cref="IWallCaptureService.RefinishPhotoRealAsync"/>): the
/// capture's newest job, when its leftover (<see cref="Leftover"/>) is still stored, is put back as "delivered, not
/// installed", and the capture pipeline finishes it again on the splat worker (crop, clean-up, export, level-of-detail
/// ladder), installs it and runs the post-capture chain on it. Minutes instead of an hour; no GPU job is queued. A failed
/// re-finish restores the job and the capture (<see cref="GpuJobRefinishState"/>): the view before is still installed.
/// </summary>
public sealed partial class GpuJobQueue
{
    /// <summary>The ids of the captures (of <paramref name="captureIds"/>) whose newest job has a stored leftover to finish again.</summary>
    public static async Task<HashSet<Guid>> RefinishableAsync(BlocwerkDbContext db, ICaptureFileStore files, IReadOnlyCollection<Guid> captureIds)
    {
        if (captureIds.Count == 0)
        {
            return [];
        }

        var jobs = await db.GpuJobs.AsNoTracking().Where(j => captureIds.Contains(j.CaptureId)).ToListAsync();
        return jobs.GroupBy(j => j.CaptureId)
            .Where(g => IsRefinishable(g.OrderByDescending(j => j.CreatedAt).First(), files))
            .Select(g => g.Key)
            .ToHashSet();
    }

    /// <summary>The capture's newest job when it can be finished again (its leftover files all still stored), or null.</summary>
    public static async Task<GpuJob?> RefinishSourceAsync(BlocwerkDbContext db, ICaptureFileStore files, Guid captureId, CancellationToken ct)
    {
        var job = await db.GpuJobs.Where(j => j.CaptureId == captureId).OrderByDescending(j => j.CreatedAt).FirstOrDefaultAsync(ct);
        return job is not null && IsRefinishable(job, files) ? job : null;
    }

    /// <summary>
    /// Puts the job back as delivered but not installed (not saved), remembering it and <paramref name="capture"/> as they
    /// were: its leftover is the result the pipeline finishes. An installed preview (the final result never came) becomes
    /// that result.
    /// </summary>
    public static void ReopenForRefinish(GpuJob job, WallCapture capture, DateTimeOffset now)
    {
        var usePreview = job.ResultPath is null;
        job.RefinishStateJson = new GpuJobRefinishState(
            job.Status, job.InstalledAt, job.CompletedAt, job.Error, job.Stage, usePreview,
            capture.Status, capture.Error, capture.Stage, capture.CompletedAt).ToJson();
        if (usePreview)
        {
            job.ResultPath = job.InstalledPreviewPath;
            job.ResultFormat = job.PreviewFormat;
            job.ResultBytes = null;
            job.ResultStatsJson = JsonSerializer.Serialize(new { previewStep = job.PreviewInstalledStep, totalSteps = job.TotalSteps });
            job.InstalledPreviewPath = null;
        }

        job.Status = GpuJobStatus.Succeeded;
        job.InstalledAt = null;
        job.FinishJobId = null;
        job.Error = null;
        job.CompletedAt = now;
        job.LeaseExpiresAt = now + FinishRetryInterval;
        job.Stage = "finishing the trained view again on the server";
    }

    /// <summary>
    /// A re-finish of the capture's newest job failed: the job is put back as it was, and the capture's state before is
    /// returned for the caller to restore. Null when that job was not being re-finished.
    /// </summary>
    public async Task<GpuJobRefinishState?> RestoreRefinishAsync(Guid captureId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var job = await db.GpuJobs.Where(j => j.CaptureId == captureId).OrderByDescending(j => j.CreatedAt).FirstOrDefaultAsync(ct);
        if (job is null || GpuJobRefinishState.Parse(job.RefinishStateJson) is not { } before)
        {
            return null;
        }

        before.RestoreJob(job);
        await db.SaveChangesAsync(ct);
        return before;
    }

    private static bool IsRefinishable(GpuJob job, ICaptureFileStore files) =>
        (job.Status is GpuJobStatus.Failed or GpuJobStatus.Cancelled || job is { Status: GpuJobStatus.Succeeded, InstalledAt: not null })
        && Leftover(job) is { Count: > 0 } kept
        && kept.All(p => files.ResolvePhysicalPath(p) is { } path && File.Exists(path));
}
