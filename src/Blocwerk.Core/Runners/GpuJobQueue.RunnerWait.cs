// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>
/// A splat prepare / finish job no 3D runner could take: once no runner able to take it has been seen for
/// <see cref="RunnerWaitNotice"/> (counted from the job's creation or the last time such a runner called, whichever is later) its
/// waiting line says "(none online for a day)" and the capture's creator gets one push. <see cref="GpuJob.NoRunnerNotifiedAt"/>
/// remembers the push, so runners coming and going never send it again. A markerless reconstruction is offered to the on-demand
/// launcher first (<see cref="IOnDemandComputeLauncher"/>).
/// </summary>
public sealed partial class GpuJobQueue
{
    internal const string NoneOnline = "(none online)";

    internal const string NoneOnlineForADay = "(none online for a day)";

    /// <summary>How long a prepare / finish job waits without a runner before the capture's creator is told.</summary>
    internal static readonly TimeSpan RunnerWaitNotice = TimeSpan.FromHours(24);

    /// <summary>The waiting line of <paramref name="job"/> when <paramref name="noneAble"/> runners could take it now.</summary>
    private async Task<string> NoRunnerStageAsync(
        BlocwerkDbContext db, GpuJob job, string stage, bool noneAble, bool markerless, CancellationToken ct)
    {
        if (!noneAble || job.Kind is not (GpuJobKind.SplatPrepare or GpuJobKind.SplatFinish))
        {
            return stage;
        }

        if (markerless && job.Kind == GpuJobKind.SplatPrepare && onDemand is not null)
        {
            await onDemand.TryLaunchAsync(job, ct);
        }

        if (Now - await WithoutRunnerSinceAsync(db, job, ct) < RunnerWaitNotice)
        {
            return stage;
        }

        await NotifyNoRunnerOnceAsync(db, job, ct);
        return stage.Replace(NoneOnline, NoneOnlineForADay, StringComparison.Ordinal);
    }

    /// <summary>Since when no runner that could take the job called: its creation, or the last call of such a runner.</summary>
    private async Task<DateTimeOffset> WithoutRunnerSinceAsync(BlocwerkDbContext db, GpuJob job, CancellationToken ct)
    {
        var own = await Assignments(db).Where(rw => rw.WallId == job.WallId)
            .Select(rw => new { rw.Runner.LastSeenAt, rw.Runner.MaxQuality, rw.Runner.Capabilities, rw.Runner.TexturesMemoryMb, rw.Runner.MemoryBudgetMb })
            .ToListAsync(ct);
        var shared = await Approvals(db).Where(a => a.WallId == job.WallId)
            .Select(a => new { a.Runner.LastSeenAt, a.Runner.MaxQuality, a.Runner.Capabilities, a.Runner.TexturesMemoryMb, a.Runner.MemoryBudgetMb })
            .ToListAsync(ct);
        var seen = own.Concat(shared)
            .Where(r => r.LastSeenAt is not null && Able(job, r.MaxQuality, r.Capabilities, r.TexturesMemoryMb, r.MemoryBudgetMb))
            .Select(r => r.LastSeenAt!.Value).DefaultIfEmpty(job.CreatedAt).Max();
        return seen > job.CreatedAt ? seen : job.CreatedAt;
    }

    /// <summary>Pushes the capture's creator, once per job: only the update that stamps <see cref="GpuJob.NoRunnerNotifiedAt"/> sends it.</summary>
    private async Task NotifyNoRunnerOnceAsync(BlocwerkDbContext db, GpuJob job, CancellationToken ct)
    {
        if (notifications is null || job.NoRunnerNotifiedAt is not null)
        {
            return;
        }

        var now = Now;
        var stamped = await db.GpuJobs.Where(j => j.Id == job.Id && j.Status == GpuJobStatus.Queued && j.NoRunnerNotifiedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.NoRunnerNotifiedAt, now), ct);
        if (stamped != 1)
        {
            return;
        }

        var creator = await db.WallCaptures.AsNoTracking().Where(c => c.Id == job.CaptureId)
            .Select(c => (Guid?)c.CreatedByUserId).FirstOrDefaultAsync(ct);
        if (creator is { } userId)
        {
            logger.LogInformation("GPU job {JobId}: no 3D runner for a day; telling user {UserId}", job.Id, userId);
            await notifications.NotifyNoRunnerAsync(job.WallId, userId);
        }
    }

    /// <summary>Stores a changed waiting line, unless a runner claimed the job meanwhile (its stage then says so).</summary>
    private async Task SetWaitingStageAsync(BlocwerkDbContext db, GpuJob job, string stage, CancellationToken ct)
    {
        if (stage != job.Stage
            && await db.GpuJobs.Where(j => j.Id == job.Id && j.Status == GpuJobStatus.Queued).ExecuteUpdateAsync(s => s.SetProperty(j => j.Stage, stage), ct) > 0)
        {
            PulseJob(job.WallId, job.Id, null);
        }
    }
}
