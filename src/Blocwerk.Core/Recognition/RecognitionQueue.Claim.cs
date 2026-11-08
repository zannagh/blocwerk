// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Recognition;

/// <summary>
/// Claiming: a runner is offered the queued jobs of the kinds it advertises, whose memory fits its hello's
/// <c>memoryBudgetMb</c>, of walls it may serve (<see cref="GpuJobQueue.Assignments"/>, <see cref="GpuJobQueue.Approvals"/>);
/// interactive before background, oldest first. A job this runner already failed (<see cref="GpuJobFailedRunners"/>) is
/// left to another online runner that could take it; with none, it tries again (the attempt budget still ends it). A
/// runner holds at most one recognition claim at a time.
/// </summary>
public sealed partial class RecognitionQueue
{
    private const int CandidateWindow = 50;
    private readonly SemaphoreSlim claimLock = new(1, 1);

    /// <summary>One claim attempt: the job this runner now holds, or null.</summary>
    public async Task<RecognitionJob?> TryClaimAsync(GpuRunner runner, CancellationToken ct)
    {
        var kinds = RecognitionKinds.All.Where(k => RunnerCapabilities.Has(runner.Capabilities, k)).ToList();
        if (kinds.Count == 0)
        {
            return null;
        }

        await claimLock.WaitAsync(ct);
        try
        {
            await using var db = dbContextFactory.CreateDbContext();
            if (await db.RecognitionJobs.AnyAsync(j => j.ClaimedByRunnerId == runner.Id && (j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running), ct))
            {
                return null;
            }

            var pick = await PickAsync(db, runner, kinds, ct);
            return pick is null ? null : await ClaimPickAsync(db, runner, pick, ct);
        }
        finally
        {
            claimLock.Release();
        }
    }

    /// <summary>Whether a runner with these reported facts could take <paramref name="job"/> (kind and memory).</summary>
    internal static bool Able(RecognitionJob job, string? capabilities, int? memoryBudgetMb) =>
        RunnerCapabilities.Has(capabilities, job.Kind) && (job.RequiredMemoryMb ?? 0) <= (memoryBudgetMb ?? 0);

    private async Task<RecognitionJob?> PickAsync(BlocwerkDbContext db, GpuRunner runner, List<string> kinds, CancellationToken ct)
    {
        var own = gpuQueue.Assignments(db).Where(rw => rw.RunnerId == runner.Id);
        var approved = gpuQueue.Approvals(db).Where(a => a.RunnerId == runner.Id);
        var budget = runner.MemoryBudgetMb ?? 0;
        var candidates = await db.RecognitionJobs.AsNoTracking()
            .Where(j => j.Status == GpuJobStatus.Queued && kinds.Contains(j.Kind) && (j.RequiredMemoryMb ?? 0) <= budget
                        && (own.Any(rw => rw.WallId == j.WallId) || approved.Any(a => a.WallId == j.WallId)))
            .OrderBy(j => j.Lane).ThenBy(j => j.CreatedAt).Take(CandidateWindow).ToListAsync(ct);
        foreach (var job in candidates)
        {
            var failed = GpuJobFailedRunners.Parse(job.FailedRunnerIdsJson);
            if (!failed.Contains(runner.Id) || !await AnotherRunnerOnlineAsync(db, job, failed, ct))
            {
                return job;
            }
        }

        return null;
    }

    /// <summary>Whether a runner that did not fail <paramref name="job"/>, may serve its wall and could take it is online and not paused.</summary>
    private async Task<bool> AnotherRunnerOnlineAsync(BlocwerkDbContext db, RecognitionJob job, IReadOnlyList<Guid> failed, CancellationToken ct)
    {
        var online = Now - gpuQueue.Options.OnlineWindow;
        var own = await gpuQueue.Assignments(db)
            .Where(rw => rw.WallId == job.WallId && rw.Runner.LastSeenAt >= online && rw.Runner.Paused != true && !failed.Contains(rw.RunnerId))
            .Select(rw => new { rw.Runner.Capabilities, rw.Runner.MemoryBudgetMb }).ToListAsync(ct);
        var shared = await gpuQueue.Approvals(db)
            .Where(a => a.WallId == job.WallId && a.Runner.LastSeenAt >= online && a.Runner.Paused != true && !failed.Contains(a.RunnerId))
            .Select(a => new { a.Runner.Capabilities, a.Runner.MemoryBudgetMb }).ToListAsync(ct);
        return own.Concat(shared).Any(r => Able(job, r.Capabilities, r.MemoryBudgetMb));
    }

    private async Task<RecognitionJob?> ClaimPickAsync(BlocwerkDbContext db, GpuRunner runner, RecognitionJob pick, CancellationToken ct)
    {
        var lease = Now + TimeSpan.FromSeconds(pick.LeaseSeconds);
        var claimed = await db.RecognitionJobs
            .Where(j => j.Id == pick.Id && j.Status == GpuJobStatus.Queued)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, GpuJobStatus.Claimed)
                    .SetProperty(j => j.ClaimedByRunnerId, runner.Id)
                    .SetProperty(j => j.ClaimToken, GpuJobQueue.Clip(runner.ClaimToken, 64))
                    .SetProperty(j => j.LeaseExpiresAt, lease)
                    .SetProperty(j => j.Progress, 0)
                    .SetProperty(j => j.Stage, "is downloading the photos")
                    .SetProperty(j => j.Attempts, j => j.Attempts + 1),
                ct);
        if (claimed == 0)
        {
            return null;
        }

        logger.LogInformation(
            "Runner {RunnerId} ({Name}) claimed recognition job {JobId} ({Kind}, {Lane}, claim {Claim})",
            runner.Id, runner.Name, pick.Id, pick.Kind, pick.Lane, pick.Attempts + 1);
        return await db.RecognitionJobs.AsNoTracking().FirstAsync(j => j.Id == pick.Id, ct);
    }
}
