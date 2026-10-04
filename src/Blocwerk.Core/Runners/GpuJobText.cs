// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Runners;

/// <summary>
/// The quiet line a finished capture shows while its photo-real view is with a 3D runner: "Photo-real view pending:
/// waiting for a 3D runner (none online)" or "Photo-real view pending: 3D runner “Cellar PC” is training (step …, 42 %)".
/// </summary>
public static class GpuJobText
{
    /// <summary>The pending line of every listed capture that has a waiting or running GPU job, by capture id.</summary>
    public static async Task<Dictionary<Guid, string>> PendingAsync(BlocwerkDbContext db, IReadOnlyCollection<Guid> captureIds)
    {
        if (captureIds.Count == 0)
        {
            return [];
        }

        var jobs = await db.GpuJobs.AsNoTracking()
            .Where(j => captureIds.Contains(j.CaptureId) && j.Kind == GpuJobKind.Splat
                        && (j.Status == GpuJobStatus.Queued || j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running
                            || (j.Status == GpuJobStatus.Succeeded && j.InstalledAt == null)))
            .Select(j => new
            {
                j.CaptureId, j.Status, j.Stage, j.Progress, j.CreatedAt, j.PreviewInstalledStep, j.TotalSteps,
                Runner = j.ClaimedByRunner == null ? null : j.ClaimedByRunner.Name,
            })
            .ToListAsync();
        return jobs.GroupBy(j => j.CaptureId)
            .ToDictionary(g => g.Key, g =>
            {
                var j = g.OrderByDescending(x => x.CreatedAt).First();
                return Pending(j.Status, j.Stage, j.Progress, j.Runner, j.PreviewInstalledStep, j.TotalSteps);
            });
    }

    /// <summary>
    /// The line for one job; with a preview installed it leads with that ("Photo-real preview (step 7000 of 50000) —
    /// refining: 3D runner “Cellar PC” is training …").
    /// </summary>
    public static string Pending(
        GpuJobStatus status, string? stage, double progress, string? runnerName, int? previewStep = null, int? totalSteps = null)
    {
        var what = status switch
        {
            GpuJobStatus.Queued => stage ?? "waiting for a 3D runner",
            GpuJobStatus.Succeeded => "trained, finishing on the server",
            _ => string.Create(CultureInfo.InvariantCulture, $"3D runner “{runnerName ?? "?"}” {stage ?? "is training"} ({progress:P0})"),
        };
        return previewStep is { } step
            ? $"{GpuJobPreviews.Label(step, totalSteps)} — refining: {what}"
            : $"Photo-real view pending: {what}";
    }
}
