// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.Retention;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>
/// A job's trained result (up to <see cref="GpuRunnerOptions.MaxResultBytes"/>) and prepared state stay after the view is
/// installed, so the view can be finished again without training (<see cref="ReopenForRefinish"/>) or the capture
/// exported for a replay. For the capture behind the wall's active model nothing else ever drops them (photo retention
/// spares that capture), so they go <see cref="WallCapturePipelineOptions.RunnerLeftoverRetention"/> after the install.
/// Only installed jobs: a failed or cancelled job's result was never installed, and finishing it again is the only way
/// to get that view, so it stays until a newer job of the capture is installed or the capture's photos expire. The
/// installed view (the splat row's own files) is never touched.
/// </summary>
public sealed partial class GpuJobQueue
{
    /// <summary>Drops (or, in a dry run, measures) the installed results older than the retention, with their prepared state.</summary>
    public static async Task<RetentionOutcome> DropAgedResultsAsync(
        RootDbContextFactory dbContextFactory, ICaptureFileStore files, WallCapturePipelineOptions options, DateTimeOffset now,
        ILogger logger, CancellationToken ct)
    {
        if (options.RunnerLeftoverRetention is not { } retention)
        {
            return RetentionOutcome.None;
        }

        await using var db = dbContextFactory.CreateDbContext();
        var candidates = await db.GpuJobs.AsNoTracking()
            .Where(j => j.ResultPath != null && j.RefinishStateJson == null && j.LeftoverDroppedAt == null
                        && j.Status == GpuJobStatus.Succeeded && j.InstalledAt != null)
            .ToListAsync(ct);

        // Filtered in memory: SQLite cannot compare DateTimeOffset in SQL.
        var aged = candidates.Where(j => now - j.InstalledAt!.Value >= retention).ToList();
        if (options.RetentionDryRun)
        {
            return new(aged.Count, RetentionFiles.SizeOf(files, aged.SelectMany(Spent)));
        }

        var count = 0;
        long freed = 0;
        foreach (var job in aged)
        {
            // Conditional: a re-finish that reopened the job since (or a newer result) keeps it.
            var dropped = await db.GpuJobs
                .Where(j => j.Id == job.Id && j.ResultPath == job.ResultPath && j.RefinishStateJson == null
                            && j.LeftoverDroppedAt == null && j.Status == GpuJobStatus.Succeeded && j.InstalledAt != null)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(j => j.ResultPath, (string?)null)
                        .SetProperty(j => j.PreviewPath, (string?)null)
                        .SetProperty(j => j.InstalledPreviewPath, (string?)null)
                        .SetProperty(j => j.LeftoverDroppedAt, now),
                    ct);
            if (dropped > 0)
            {
                count++;
                freed += RetentionFiles.Delete(files, Spent(job), logger);
            }
        }

        return new(count, freed);
    }

    /// <summary>
    /// What an installed job still holds: its result and prepared state, and any preview file (the final view superseded
    /// the previews; the close usually deleted them already).
    /// </summary>
    private static IEnumerable<string> Spent(GpuJob job) =>
        new[] { job.ResultPath, job.PreparedPath, job.PreviewPath, job.InstalledPreviewPath }.OfType<string>();
}
