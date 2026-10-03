// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>
/// The per-runner failure record (<see cref="GpuRunnerFailure"/>): written when a release costs a training failure (or ends
/// the job) while a runner held it, so the runner overview shows each runner's own reason. Best effort: the release
/// already happened, a record that cannot be written is logged.
/// </summary>
public sealed partial class GpuJobQueue
{
    /// <summary>How long a runner's failures are kept.</summary>
    public static readonly TimeSpan FailureRetention = TimeSpan.FromDays(30);

    /// <summary>Records the failure (never cancelled: the release it belongs to already happened).</summary>
    private async Task RecordFailureAsync(GpuJob job, Guid runnerId, string? reason, CancellationToken ct)
    {
        try
        {
            await using var db = dbContextFactory.CreateDbContext();
            var now = Now;
            db.GpuRunnerFailures.Add(new GpuRunnerFailure { RunnerId = runnerId, JobId = job.Id, WallId = job.WallId, At = now, Reason = reason });
            await db.SaveChangesAsync(ct);
            var expired = now - FailureRetention;
            await db.GpuRunnerFailures.Where(f => f.RunnerId == runnerId && f.At < expired).ExecuteDeleteAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            logger.LogWarning(ex, "GPU job {JobId}: the failure of runner {RunnerId} could not be recorded", job.Id, runnerId);
        }
    }
}
