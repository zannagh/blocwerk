// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>
/// A splat prepare / finish job no 3D runner could take: after <see cref="RunnerWaitNotice"/> its waiting line says
/// "(none online for a day)" and the capture's creator gets one push (the line in the row is what remembers it, so a restart
/// sends none again; a runner that comes and goes again without taking it starts a new day). A markerless reconstruction is
/// offered to the on-demand launcher first (<see cref="IOnDemandComputeLauncher"/>).
/// </summary>
public sealed partial class GpuJobQueue
{
    /// <summary>How long a prepare / finish job waits without a runner before the capture's creator is told.</summary>
    internal static readonly TimeSpan RunnerWaitNotice = TimeSpan.FromHours(24);

    internal const string NoneOnline = "(none online)";

    internal const string NoneOnlineForADay = "(none online for a day)";

    /// <summary>The waiting line of <paramref name="job"/> when <paramref name="noneAble"/> runners could take it.</summary>
    private async Task<string> NoRunnerStageAsync(GpuJob job, string stage, bool noneAble, bool markerless, CancellationToken ct)
    {
        if (!noneAble || job.Kind is not (GpuJobKind.SplatPrepare or GpuJobKind.SplatFinish))
        {
            return stage;
        }

        if (markerless && job.Kind == GpuJobKind.SplatPrepare && onDemand is not null)
        {
            await onDemand.TryLaunchAsync(job, ct);
        }

        return Now - job.CreatedAt >= RunnerWaitNotice ? stage.Replace(NoneOnline, NoneOnlineForADay, StringComparison.Ordinal) : stage;
    }

    /// <summary>Stores a changed waiting line (unless a runner claimed the job meanwhile) and sends the day-long-wait push.</summary>
    private async Task SetWaitingStageAsync(BlocwerkDbContext db, GpuJob job, string stage, CancellationToken ct)
    {
        if (stage == job.Stage
            || await db.GpuJobs.Where(j => j.Id == job.Id && j.Status == GpuJobStatus.Queued).ExecuteUpdateAsync(s => s.SetProperty(j => j.Stage, stage), ct) == 0)
        {
            return;
        }

        PulseJob(job.WallId, job.Id, null);
        if (notifications is not null && stage.EndsWith(NoneOnlineForADay, StringComparison.Ordinal)
            && job.Stage?.EndsWith(NoneOnlineForADay, StringComparison.Ordinal) != true)
        {
            var creator = await db.WallCaptures.AsNoTracking().Where(c => c.Id == job.CaptureId)
                .Select(c => (Guid?)c.CreatedByUserId).FirstOrDefaultAsync(ct);
            if (creator is { } userId)
            {
                logger.LogInformation("GPU job {JobId}: no 3D runner for a day; telling user {UserId}", job.Id, userId);
                await notifications.NotifyNoRunnerAsync(job.WallId, userId);
            }
        }
    }
}
