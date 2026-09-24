// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>
/// The server side of the 3D runners: key authentication, the job queue (claim, lease, progress,
/// result, failure), and the hand-back of a trained splat to the capture pipeline.
/// </summary>
/// <remarks>
/// Single-instance app (like <see cref="WallCaptureQueue"/>): claims are serialised in process and
/// guarded by a conditional update, the database row is the durable truth, and the lease sweep
/// (<see cref="GpuJobSweepWorker"/>) requeues whatever a vanished runner held. A runner only ever
/// reaches ITS claimed job: every job-scoped call re-checks <c>ClaimedByRunnerId</c>.
/// </remarks>
public sealed partial class GpuJobQueue(
    RootDbContextFactory dbContextFactory,
    ICaptureFileStore files,
    WallCaptureQueue captureQueue,
    GpuRunnerOptions options,
    GpuJobSignal signal,
    ILogger<GpuJobQueue> logger,
    IDeployBusyGate? busyGate = null,
    TimeProvider? clock = null)
{
    private static readonly TimeSpan LastSeenWriteInterval = TimeSpan.FromSeconds(5);
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    public GpuRunnerOptions Options => options;

    private DateTimeOffset Now => time.GetUtcNow();

    /// <summary>The runner a bearer key names, or null (unknown, malformed or revoked). Stamps last-seen.</summary>
    public async Task<GpuRunner?> AuthenticateAsync(string? token, CancellationToken ct)
    {
        if (!GpuRunnerTokens.LooksLikeRunnerKey(token))
        {
            return null;
        }

        var hash = GpuRunnerTokens.Hash(token!);
        await using var db = dbContextFactory.CreateDbContext();
        var runner = await db.GpuRunners.FirstOrDefaultAsync(r => r.KeyHash == hash, ct);
        if (runner is null || !GpuRunnerTokens.HashEquals(runner.KeyHash, hash) || runner.RevokedAt is not null)
        {
            return null;
        }

        if (runner.LastSeenAt is null || Now - runner.LastSeenAt.Value > LastSeenWriteInterval)
        {
            runner.LastSeenAt = Now;
            await db.SaveChangesAsync(ct);
        }

        db.Entry(runner).State = EntityState.Detached;
        return runner;
    }

    /// <summary>Stores what the runner reports about itself.</summary>
    public async Task HelloAsync(GpuRunner runner, RunnerHello hello, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var row = await db.GpuRunners.FirstAsync(r => r.Id == runner.Id, ct);
        row.GpuName = Clip(hello.GpuName, 200);
        row.VramMb = hello.VramMb is > 0 and < 10_000_000 ? hello.VramMb : null;
        row.MaxQuality = hello.MaxQuality is "draft" or "high" or "max" ? hello.MaxQuality : null;
        row.MemoryBudgetMb = hello.MemoryBudgetMb is > 0 and < 10_000_000 ? hello.MemoryBudgetMb : null;
        row.RunnerVersion = Clip(hello.RunnerVersion, 64);
        row.Platform = Clip(string.Join(" · ", new[] { hello.Platform, hello.BrushVersion is null ? null : $"Brush {hello.BrushVersion}" }
            .Where(s => !string.IsNullOrWhiteSpace(s))), 200);
        row.LastSeenAt = Now;
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Runner {RunnerId} ({Name}) said hello: {Gpu}, {Vram} MB, max quality {Quality}, version {Version}",
            runner.Id, runner.Name, row.GpuName, row.VramMb, row.MaxQuality, row.RunnerVersion);
    }

    /// <summary>
    /// Whether the <see cref="GpuRunnerMode.Auto"/> mode sends this wall's photo-real view to runners:
    /// the wall has a runner of its own, or some admin shares one. Revoked runners do not count.
    /// </summary>
    public async Task<bool> WallHasRunnersAsync(Guid wallId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        return await db.GpuRunners.AnyAsync(
            r => r.RevokedAt == null
                 && (r.SharedWithOtherWalls || db.GpuRunnerWalls.Any(rw => rw.RunnerId == r.Id && rw.WallId == wallId)),
            ct);
    }

    /// <summary>Queues a prepared job and wakes waiting runners.</summary>
    public async Task<GpuJob> EnqueueAsync(GpuJob job, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        job.Status = GpuJobStatus.Queued;
        job.CreatedAt = Now;
        db.GpuJobs.Add(job);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "GPU job {JobId} queued for capture {CaptureId} of wall {WallId} ({Quality}, bundle {Bytes} bytes)",
            job.Id, job.CaptureId, job.WallId, job.Quality, job.BundleBytes);
        signal.Pulse();
        return job;
    }

    /// <summary>The capture's newest GPU job, or null.</summary>
    public async Task<GpuJob?> LatestForCaptureAsync(Guid captureId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        return await db.GpuJobs.AsNoTracking().Where(j => j.CaptureId == captureId)
            .OrderByDescending(j => j.CreatedAt).FirstOrDefaultAsync(ct);
    }

    /// <summary>Remembers the splat worker's finish job, so a restart resumes it.</summary>
    public async Task SetFinishJobAsync(Guid jobId, string finishJobId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        await db.GpuJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.FinishJobId, finishJobId), ct);
    }

    /// <summary>Marks a finished job installed (or failed at finish) and deletes its files.</summary>
    public async Task CloseAsync(Guid jobId, string? error, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var job = await db.GpuJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
        {
            return;
        }

        if (error is null)
        {
            job.InstalledAt = Now;
        }
        else
        {
            job.Status = GpuJobStatus.Failed;
            job.Error = Clip(error, 2048);
        }

        await db.SaveChangesAsync(ct);
        DeleteFiles(job);
    }

    internal static string? Clip(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];

    private void DeleteFiles(GpuJob job)
    {
        foreach (var path in new[] { job.BundlePath, job.PreparedPath, job.ResultPath })
        {
            try
            {
                files.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not delete {File} of GPU job {JobId}", path, job.Id);
            }
        }
    }
}
