// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>
/// Textures jobs (<see cref="GpuJobKind.Textures"/>): a capture's wall textures rendered on a 3D runner with more memory than
/// the server's geometry worker has. They share the splat jobs' queue, lease, claim token, retry budgets and uploads; what
/// differs lives here: only runners that advertise <c>textures</c> (with enough memory) are offered one, the result is a
/// zip the capture pipeline installs through its normal texture path (<see cref="RunnerTexturesClient"/>), and the pipeline
/// (not the splat finish) is woken when the job is delivered or ends (<see cref="NotifyTextures"/>).
/// </summary>
public sealed partial class GpuJobQueue
{
    /// <summary>A runner that could render a wall's textures: what the routing offers the admin.</summary>
    public sealed record TexturesRunnerOffer(Guid RunnerId, string Name, int MemoryMb, bool Paused, bool Busy);

    /// <summary>How the textures queue looks to a wall: the jobs waiting or running, across the queue.</summary>
    public sealed record TexturesQueueState(int Waiting, int Running);

    /// <summary>A textures job by id, or null.</summary>
    public async Task<GpuJob?> TexturesJobAsync(Guid jobId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        return await db.GpuJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId && j.Kind == GpuJobKind.Textures, ct);
    }

    /// <summary>
    /// The online runners (paused ones too, flagged) that may serve the wall, advertise <c>textures</c> and have at least
    /// <paramref name="requiredMb"/> to render with, the ones that can take it now first.
    /// </summary>
    public async Task<IReadOnlyList<TexturesRunnerOffer>> TexturesRunnersAsync(Guid wallId, int requiredMb, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var online = Now - options.OnlineWindow;
        var own = await Assignments(db).Where(rw => rw.WallId == wallId).Select(rw => rw.Runner).ToListAsync(ct);
        var shared = await Approvals(db).Where(a => a.WallId == wallId).Select(a => a.Runner).ToListAsync(ct);
        var able = own.Concat(shared).DistinctBy(r => r.Id)
            .Where(r => r.LastSeenAt >= online && RunnerCapabilities.Has(r.Capabilities, RunnerCapabilities.Textures)
                        && (r.TexturesMemoryMb ?? 0) >= requiredMb)
            .ToList();
        var ids = able.Select(r => r.Id).ToList();
        var busy = (await db.GpuJobs.AsNoTracking()
                .Where(j => (j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running) && ids.Contains(j.ClaimedByRunnerId!.Value))
                .Select(j => j.ClaimedByRunnerId!.Value).ToListAsync(ct))
            .ToHashSet();
        return able.Select(r => new TexturesRunnerOffer(r.Id, r.Name, r.TexturesMemoryMb ?? 0, r.Paused == true, busy.Contains(r.Id)))
            .OrderBy(o => o.Paused).ThenBy(o => o.Busy).ThenByDescending(o => o.MemoryMb).ToList();
    }

    /// <summary>The textures jobs waiting for a runner and running on one, across the queue.</summary>
    public async Task<TexturesQueueState> TexturesQueueAsync(CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var states = await db.GpuJobs.AsNoTracking()
            .Where(j => j.Kind == GpuJobKind.Textures
                        && (j.Status == GpuJobStatus.Queued || j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running))
            .Select(j => j.Status).ToListAsync(ct);
        return new TexturesQueueState(states.Count(s => s == GpuJobStatus.Queued), states.Count(s => s != GpuJobStatus.Queued));
    }

    /// <summary>
    /// A textures job is over once the pipeline has installed its textures (<paramref name="error"/> null) or given up on it:
    /// its files (bundle, result) go, nothing is kept as a leftover.
    /// </summary>
    public async Task CloseTexturesAsync(Guid jobId, string? error, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var job = await db.GpuJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.Kind == GpuJobKind.Textures, ct);
        if (job is null)
        {
            return;
        }

        var spent = FilesOf(job).ToList();
        job.ResultPath = null;
        job.PreviewPath = null;
        job.InstalledPreviewPath = null;
        if (error is null)
        {
            job.InstalledAt = Now;
            job.Error = null;
        }
        else
        {
            job.Status = job.Status == GpuJobStatus.Cancelled ? GpuJobStatus.Cancelled : GpuJobStatus.Failed;
            job.Error = Clip(error, 2048);
            job.CompletedAt ??= Now;
        }

        await db.SaveChangesAsync(ct);
        DeleteAll(spent, job.Id);
    }

    /// <summary>Wakes the capture's texture re-render (the delivered result is to be installed, or the job ended).</summary>
    private void NotifyTextures(Guid captureId) => textureQueue?.Enqueue(captureId);

    /// <summary>The delivered result waits for the pipeline: hand it over again every retry interval, give up after a day.</summary>
    private async Task RetryTexturesDeliveryAsync(BlocwerkDbContext db, GpuJob job, DateTimeOffset now, CancellationToken ct)
    {
        if (job.CompletedAt < now - FinishGiveUp)
        {
            logger.LogWarning("GPU job {JobId}: the delivered textures could not be installed for a day; giving up", job.Id);
            await CloseTexturesAsync(job.Id, "the rendered textures could not be installed on the server", ct);
        }
        else
        {
            await db.GpuJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseExpiresAt, now + FinishRetryInterval), ct);
        }

        NotifyTextures(job.CaptureId);
    }
}
