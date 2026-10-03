// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A job's bundle is a copy of capture photos, so it never outlives its reason: a queued job expires, photo retention
/// takes the job along, <c>RUNNERS__MODE=off</c> cancels what waits, and a finished job's leftover files are orphans.
/// </summary>
public class GpuRunnerRetentionTests
{
    [Fact]
    public async Task QueuedJob_ExpiresAfterItsLifetime_AndItsFilesGo()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, new GpuRunnerOptions { ClaimWait = TimeSpan.Zero, QueuedLifetime = TimeSpan.FromDays(14) });
        var job = await f.AddJobAsync(h.WallId);

        f.Clock.Advance(TimeSpan.FromDays(13));
        await f.Queue.SweepAsync(CancellationToken.None);
        Assert.Contains(f.Files.ListFiles(), x => x.Name == job.BundlePath);

        f.Clock.Advance(TimeSpan.FromDays(2));
        await f.Queue.SweepAsync(CancellationToken.None);

        await using var db = h.CreateContext();
        var row = await db.GpuJobs.SingleAsync();
        Assert.Equal(GpuJobStatus.Cancelled, row.Status);
        Assert.DoesNotContain(f.Files.ListFiles(), x => x.Name == job.BundlePath || x.Name == job.PreparedPath);
        Assert.Equal(WallCaptureStatus.SucceededWithoutSplat, (await db.WallCaptures.SingleAsync()).Status);
    }

    [Fact]
    public async Task PhotoRetention_CancelsTheCapturesJob_AndDeletesItsBundle()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var job = await f.AddJobAsync(h.WallId);
        await using (var db = h.CreateContext())
        {
            await db.WallCaptures.ExecuteUpdateAsync(s => s.SetProperty(c => c.CompletedAt, DateTimeOffset.UtcNow.AddDays(-40)));
        }

        await Sweeper(h, f).SweepAsync(CancellationToken.None);

        await using var check = h.CreateContext();
        Assert.Equal(GpuJobStatus.Cancelled, (await check.GpuJobs.SingleAsync()).Status);
        Assert.DoesNotContain(f.Files.ListFiles(), x => x.Name == job.BundlePath);
    }

    [Fact]
    public async Task FilesOfFinishedJobs_AreOrphans_ThoseOfWaitingJobsAreNot()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var waiting = await f.AddJobAsync(h.WallId);
        var failed = await f.AddJobAsync(h.WallId);
        await using (var db = h.CreateContext())
        {
            // A failed job whose file delete failed: the row stays, the files too.
            await db.GpuJobs.Where(j => j.Id == failed.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, GpuJobStatus.Failed));
        }

        foreach (var file in f.Files.ListFiles())
        {
            File.SetLastWriteTimeUtc(f.Files.ResolvePhysicalPath(file.Name)!, DateTime.UtcNow.AddHours(-3));
        }

        await Sweeper(h, f).SweepAsync(CancellationToken.None);

        var left = f.Files.ListFiles().Select(x => x.Name).ToHashSet();
        Assert.Contains(waiting.BundlePath, left);
        Assert.DoesNotContain(failed.BundlePath, left);
        Assert.DoesNotContain(failed.PreparedPath, left);
    }

    [Fact]
    public async Task ModeOff_CancelsEveryWaitingAndRunningJob_AtStartup()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, new GpuRunnerOptions { Mode = GpuRunnerMode.Off, ClaimWait = TimeSpan.Zero });
        var (runner, _) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var running = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);
        var waiting = await f.AddJobAsync(h.WallId);

        using var worker = new GpuJobSweepWorker(f.Queue, NullLogger<GpuJobSweepWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(async () =>
        {
            await using var db = h.CreateContext();
            return await db.GpuJobs.AllAsync(j => j.Status == GpuJobStatus.Cancelled);
        });
        await worker.StopAsync(CancellationToken.None);

        Assert.DoesNotContain(f.Files.ListFiles(), x => x.Name == running.BundlePath || x.Name == waiting.BundlePath);
    }

    private static WallCaptureSweeper Sweeper(WallTestHarness h, RunnerFixture f) =>
        new(h.RootContextFactory, f.Files, new WallCapturePipelineOptions(), NullLogger<WallCaptureSweeper>.Instance);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(50);
        }
    }
}
