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
/// runner holds at most one recognition claim at a time; after a restart it gets that one back (<see cref="ReattachAsync"/>).
/// </summary>
public sealed partial class RecognitionQueue
{
    private const int CandidateWindow = 50;
    private readonly SemaphoreSlim claimLock = new(1, 1);

    /// <summary>One claim attempt: the job this runner now holds, or null.</summary>
    public async Task<RecognitionJob?> TryClaimAsync(GpuRunner runner, CancellationToken ct) => (await ClaimOrReattachAsync(runner, ct)).Job;

    /// <summary>
    /// One claim attempt: a queued job, or the job this runner already holds when it has been silent long enough to have
    /// restarted (<see cref="ReattachAsync"/>). The runner is read again first: a long-poll outlives a revoke or a new hello.
    /// </summary>
    public async Task<(RecognitionJob? Job, bool Reattached)> ClaimOrReattachAsync(GpuRunner runner, CancellationToken ct)
    {
        if (!RunnerCapabilities.HasRecognition(runner.Capabilities))
        {
            return (null, false);
        }

        await claimLock.WaitAsync(ct);
        try
        {
            await using var db = dbContextFactory.CreateDbContext();
            var current = await gpuQueue.ActiveRunners(db).AsNoTracking().FirstOrDefaultAsync(r => r.Id == runner.Id, ct);
            if (current is null)
            {
                return (null, false);
            }

            current.ClaimToken = runner.ClaimToken;
            if (await HoldsClaimAsync(db, runner.Id, ct))
            {
                var held = await ReattachAsync(db, current, ct);
                return (held, held is not null);
            }

            var kinds = RecognitionKinds.All.Where(k => RunnerCapabilities.Has(current.Capabilities, k)).ToList();
            var pick = kinds.Count == 0 ? null : await PickAsync(db, current, kinds, ct);
            return (pick is null ? null : await ClaimPickAsync(db, current, pick, ct), false);
        }
        finally
        {
            claimLock.Release();
        }
    }

    /// <summary>Whether the runner holds a recognition job (claimed or running).</summary>
    public async Task<bool> HoldsClaimAsync(Guid runnerId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        return await HoldsClaimAsync(db, runnerId, ct);
    }

    /// <summary>Whether a runner with these reported facts could take <paramref name="job"/> (kind and memory).</summary>
    internal static bool Able(RecognitionJob job, string? capabilities, int? memoryBudgetMb) =>
        RunnerCapabilities.Has(capabilities, job.Kind) && (job.RequiredMemoryMb ?? 0) <= (memoryBudgetMb ?? 0);

    private static Task<bool> HoldsClaimAsync(BlocwerkDbContext db, Guid runnerId, CancellationToken ct) =>
        db.RecognitionJobs.AnyAsync(j => j.ClaimedByRunnerId == runnerId && (j.Status == GpuJobStatus.Claimed || j.Status == GpuJobStatus.Running), ct);

    /// <summary>Picks for <paramref name="runner"/>: the instance runner serves every wall, any other runner its own and approved ones.</summary>
    private async Task<RecognitionJob?> PickAsync(BlocwerkDbContext db, GpuRunner runner, List<string> kinds, CancellationToken ct)
    {
        var anyWall = runner.Scope == RunnerScope.Instance;
        var own = gpuQueue.Assignments(db).Where(rw => rw.RunnerId == runner.Id);
        var approved = gpuQueue.Approvals(db).Where(a => a.RunnerId == runner.Id);
        var budget = runner.MemoryBudgetMb ?? 0;
        var candidates = await db.RecognitionJobs.AsNoTracking()
            .Where(j => j.Status == GpuJobStatus.Queued && kinds.Contains(j.Kind) && (j.RequiredMemoryMb ?? 0) <= budget
                        && (anyWall || own.Any(rw => rw.WallId == j.WallId) || approved.Any(a => a.WallId == j.WallId)))
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
        var instance = await gpuQueue.ActiveRunners(db)
            .Where(r => r.Scope == RunnerScope.Instance && r.LastSeenAt >= online && r.Paused != true && !failed.Contains(r.Id))
            .Select(r => new { r.Capabilities, r.MemoryBudgetMb }).ToListAsync(ct);
        return own.Concat(shared).Concat(instance).Any(r => Able(job, r.Capabilities, r.MemoryBudgetMb));
    }

    private async Task<RecognitionJob?> ClaimPickAsync(BlocwerkDbContext db, GpuRunner runner, RecognitionJob pick, CancellationToken ct)
    {
        var now = Now;
        var lease = now + TimeSpan.FromSeconds(pick.LeaseSeconds);
        var run = now + TimeSpan.FromSeconds(pick.RunSeconds);
        var claimed = await db.RecognitionJobs
            .Where(j => j.Id == pick.Id && j.Status == GpuJobStatus.Queued)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, GpuJobStatus.Claimed)
                    .SetProperty(j => j.ClaimedByRunnerId, runner.Id)
                    .SetProperty(j => j.ClaimToken, GpuJobQueue.Clip(runner.ClaimToken, 64))
                    .SetProperty(j => j.LeaseExpiresAt, lease)
                    .SetProperty(j => j.RunDeadlineAt, run)
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
