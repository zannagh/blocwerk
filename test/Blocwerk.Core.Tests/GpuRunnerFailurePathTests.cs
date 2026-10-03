// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Blocwerk.Web.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The failure paths around a runner's job: a 410 says whether the job is over or requeued, a bundle missing on the
/// server fails the job at once, a pending preview is retried after a worker outage, a cancel or expiry keeps the
/// installed preview, and another wall's job on a shared runner is shown only as "busy".
/// </summary>
public class GpuRunnerFailurePathTests
{
    private const string Api = RunnerApiEndpoints.Prefix;

    [Fact]
    public async Task A410_SaysWhetherTheJobIsOver_OrWentBackToTheQueue()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        await using var host = await RunnerApiTestHost.StartAsync(f);
        var other = await f.AddWallAsync("Other wall");
        var (shared, key) = await f.AddRunnerAsync("shared", shared: true, walls: other);
        await f.ApproveAsync(h.WallId, shared);
        var first = await f.AddJobAsync(h.WallId);
        Assert.Equal(HttpStatusCode.OK, (await host.PostJsonAsync($"{Api}/claim", key, "{}")).StatusCode);
        await using (var db = h.CreateContext())
        {
            await db.GpuRunnerApprovals.ExecuteDeleteAsync();
        }

        var requeued = await host.PostJsonAsync($"{Api}/jobs/{first.Id}/progress", key, "{\"fraction\":0.5}");
        Assert.Equal(HttpStatusCode.Gone, requeued.StatusCode);
        Assert.Contains("\"reason\":\"requeued\"", await requeued.Content.ReadAsStringAsync());

        await f.ApproveAsync(h.WallId, shared);
        Assert.Equal(HttpStatusCode.OK, (await host.PostJsonAsync($"{Api}/claim", key, "{}")).StatusCode);
        await f.Queue.CancelForCaptureAsync(first.CaptureId, "superseded", CancellationToken.None);
        var over = await host.PostJsonAsync($"{Api}/jobs/{first.Id}/progress", key, "{\"fraction\":0.6}");
        Assert.Equal(HttpStatusCode.Gone, over.StatusCode);
        Assert.Contains("\"reason\":\"over\"", await over.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABundleMissingOrDamagedOnTheServer_FailsTheJobAtOnce_WithThatReason(bool missing)
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        await using var host = await RunnerApiTestHost.StartAsync(f);
        var (_, key) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        Assert.Equal(HttpStatusCode.OK, (await host.PostJsonAsync($"{Api}/claim", key, "{}")).StatusCode);
        var bundle = f.Files.ResolvePhysicalPath(job.BundlePath)!;
        if (missing)
        {
            File.Delete(bundle);
        }
        else
        {
            await File.WriteAllBytesAsync(bundle, [1, 2, 3]);
        }

        var response = await host.SendAsync(HttpMethod.Get, $"{Api}/jobs/{job.Id}/bundle", key);

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Contains("\"reason\":\"over\"", await response.Content.ReadAsStringAsync());
        await using var db = h.CreateContext();
        var row = await db.GpuJobs.SingleAsync();
        Assert.Equal(GpuJobStatus.Failed, row.Status);
        Assert.Contains("bundle is missing or damaged", row.Error);
        Assert.Equal(WallCaptureStatus.SucceededWithoutSplat, (await db.WallCaptures.SingleAsync()).Status);
    }

    [Fact]
    public async Task APendingPreview_IsHandedToThePreviewWorkerAgain_AfterTheRetryInterval()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var previews = new GpuPreviewQueue();
        var queue = new GpuJobQueue(
            h.RootContextFactory, f.Files, f.CaptureQueue, f.Options, new GpuJobSignal(), NullLogger<GpuJobQueue>.Instance,
            clock: f.Clock, diskSpace: f.Disk, previews: previews);
        var (runner, _) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await queue.TryClaimAsync(runner, null, CancellationToken.None);
        await using (var db = h.CreateContext())
        {
            await db.GpuJobs.ExecuteUpdateAsync(s => s.SetProperty(j => j.PreviewPath, "preview.upl").SetProperty(j => j.PreviewStep, 7000));
        }

        var next = previews.DequeueAsync(CancellationToken.None).AsTask();
        await queue.SweepAsync(CancellationToken.None); // the preview worker recovers them itself on start
        f.Clock.Advance(GpuJobQueue.PreviewRetryInterval - TimeSpan.FromSeconds(1));
        await queue.SweepAsync(CancellationToken.None);
        Assert.False(next.IsCompleted);

        f.Clock.Advance(TimeSpan.FromSeconds(1));
        await queue.SweepAsync(CancellationToken.None);
        Assert.Equal(job.Id, await next.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACancelOrExpiry_KeepsTheInstalledPreview_ToFinishAgain(bool cancel)
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var job = await f.AddJobAsync(h.WallId);
        var preview = await f.Files.SaveAsync(RunnerFixture.SlimPly(), ".upl", CancellationToken.None);
        await using (var db = h.CreateContext())
        {
            await db.GpuJobs.ExecuteUpdateAsync(s => s.SetProperty(j => j.InstalledPreviewPath, preview)
                .SetProperty(j => j.PreviewFormat, "ply").SetProperty(j => j.PreviewInstalledStep, 7000));
        }

        if (cancel)
        {
            await f.Queue.CancelForCaptureAsync(job.CaptureId, "cancelled by an admin", CancellationToken.None);
        }
        else
        {
            f.Clock.Advance(f.Options.QueuedLifetime + TimeSpan.FromMinutes(1));
            await f.Queue.SweepAsync(CancellationToken.None);
        }

        Assert.False(File.Exists(f.Files.ResolvePhysicalPath(job.BundlePath)));
        Assert.True(File.Exists(f.Files.ResolvePhysicalPath(preview)));
        Assert.True(File.Exists(f.Files.ResolvePhysicalPath(job.PreparedPath)));
        await using var check = h.CreateContext();
        Assert.Equal(GpuJobStatus.Cancelled, (await check.GpuJobs.SingleAsync()).Status);
        Assert.NotNull(await GpuJobQueue.RefinishSourceAsync(check, f.Files, job.CaptureId, CancellationToken.None));
        Assert.Contains(preview, await GpuJobQueue.ReferencedFilesAsync(check, CancellationToken.None));
    }

    [Fact]
    public async Task ASharedRunnersJobOfAnotherWall_IsShownOnlyAsBusy()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var other = await f.AddWallAsync("Their wall", foreign: true);
        Guid theirAdmin;
        await using (var db = h.CreateContext())
        {
            theirAdmin = await db.Walls.IgnoreQueryFilters().Where(w => w.Id == other).Select(w => w.OwnerId).SingleAsync();
        }

        var (shared, _) = await f.AddRunnerAsync("their shared GPU", shared: true, ownerId: theirAdmin, walls: other);
        var (mine, _) = await f.AddRunnerAsync("my GPU", walls: h.WallId);
        await f.AddJobAsync(other);
        var own = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(shared, null, CancellationToken.None);
        await f.Queue.TryClaimAsync(mine, null, CancellationToken.None);

        var service = new GpuRunnerService(h.DbContextFactory, h.CurrentUser, f.Queue, NullLogger<GpuRunnerService>.Instance, null);
        var runners = await service.ListForWallAsync(h.WallId);

        Assert.Equal(GpuRunnerCurrentJob.Busy, runners.Single(r => r.Id == shared.Id).CurrentJob);
        Assert.Equal(own.Id, runners.Single(r => r.Id == mine.Id).CurrentJob?.JobId);
    }
}
