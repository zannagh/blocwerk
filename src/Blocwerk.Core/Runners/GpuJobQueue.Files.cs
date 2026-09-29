// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>
/// A GPU job's files in the capture store: the bundle (a copy of the capture's photos), the prepared state, the uploaded
/// result and the newest preview. Once training is over the bundle goes, but the job keeps a leftover: its trained result
/// (installed or not), else its installed preview, with the prepared state, so the view can be finished and installed
/// again without training (<see cref="ReopenForRefinish"/>). A leftover goes when a newer job of the same capture is
/// installed, and with the capture's photos (retention).
/// </summary>
public sealed partial class GpuJobQueue
{
    /// <summary>Every file of the job.</summary>
    public static IEnumerable<string> FilesOf(GpuJob job) =>
        new[] { job.BundlePath, job.PreparedPath, job.ResultPath, job.PreviewPath, job.InstalledPreviewPath }.OfType<string>().Distinct();

    /// <summary>What the job keeps once its training is over: its result, else an installed preview; with it the prepared state.</summary>
    public static IReadOnlyList<string> Leftover(GpuJob job) =>
        job.ResultPath is not null ? [job.ResultPath, job.PreparedPath]
        : job.InstalledPreviewPath is not null ? [job.InstalledPreviewPath, job.PreparedPath]
        : [];

    /// <summary>
    /// Marks the leftovers of every job of <paramref name="captureIds"/> (but <paramref name="exceptJobId"/>) whose training
    /// is over as gone (not saved) and returns their files, for deleting after the save.
    /// </summary>
    public static async Task<List<string>> DropLeftoversAsync(
        BlocwerkDbContext db, IReadOnlyCollection<Guid> captureIds, Guid? exceptJobId, CancellationToken ct)
    {
        var jobs = await db.GpuJobs
            .Where(j => captureIds.Contains(j.CaptureId) && j.Id != exceptJobId
                        && (j.ResultPath != null || j.PreviewPath != null || j.InstalledPreviewPath != null)
                        && ((j.Status == GpuJobStatus.Succeeded && j.InstalledAt != null)
                            || j.Status == GpuJobStatus.Failed || j.Status == GpuJobStatus.Cancelled))
            .ToListAsync(ct);
        var paths = new List<string>();
        foreach (var job in jobs)
        {
            paths.AddRange(new[] { job.ResultPath, job.PreviewPath, job.InstalledPreviewPath, job.PreparedPath }.OfType<string>());
            job.ResultPath = null;
            job.PreviewPath = null;
            job.InstalledPreviewPath = null;
        }

        return paths;
    }

    /// <summary>Stored names the capture sweep must keep: every file of a job still in play, and every job's leftover.</summary>
    public static async Task<List<string>> ReferencedFilesAsync(BlocwerkDbContext db, CancellationToken ct)
    {
        var jobs = await db.GpuJobs.AsNoTracking()
            .Where(j => j.Status == GpuJobStatus.Queued || j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running
                        || (j.Status == GpuJobStatus.Succeeded && j.InstalledAt == null)
                        || (j.Status != GpuJobStatus.Cancelled && (j.ResultPath != null || j.PreviewPath != null || j.InstalledPreviewPath != null)))
            .ToListAsync(ct);
        return jobs.SelectMany(FilesOf).ToList();
    }

    /// <summary>
    /// Deletes whatever of the job (and of <paramref name="alsoSpent"/>, names read before it changed) is not its
    /// <see cref="Leftover"/> (the training is over).
    /// </summary>
    private void DeleteSpent(GpuJob job, params string?[] alsoSpent)
    {
        var keep = Leftover(job);
        foreach (var path in FilesOf(job).Concat(alsoSpent.OfType<string>()).Distinct().Where(p => !keep.Contains(p)))
        {
            DeleteQuietly(path, job.Id);
        }
    }

    private void DeleteFiles(GpuJob job)
    {
        foreach (var path in FilesOf(job))
        {
            DeleteQuietly(path, job.Id);
        }
    }

    private void DeleteQuietly(string? path, Guid jobId)
    {
        try
        {
            files.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete {File} of GPU job {JobId}; the capture sweep removes it as an orphan", path, jobId);
        }
    }
}
