// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Blocwerk.Web.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The result of a splat prepare / finish job: the allow-listed zip is accepted under its kind's size cap, anything else is
/// refused and leaves the claim in place; a delivery wakes the capture (also one parked in Solving); a job is closed without
/// keeping its input.
/// </summary>
public class GpuRunnerSplatResultTests
{
    private const string Api = RunnerApiEndpoints.Prefix;

    [Fact]
    public async Task APrepareResult_IsStored_AndWakesACaptureParkedInSolving()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, claimed) = await ClaimedAsync(f, h, GpuJobKind.SplatPrepare, markerless: true, capture: WallCaptureStatus.Solving);

        var outcome = await f.Queue.AcceptResultAsync(runner, claimed.Id, new MemoryStream(SplatJobSupport.PrepareResult()), null, null, CancellationToken.None);

        Assert.Equal(RunnerJobOutcome.Ok, outcome);
        var job = await SplatJobSupport.JobAsync(h, claimed.Id);
        Assert.Equal((GpuJobStatus.Succeeded, "zip"), (job.Status, job.ResultFormat));
        Assert.Equal(claimed.CaptureId, await f.CaptureQueue.DequeueAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token));
        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync(c => c.Id == claimed.CaptureId);
        Assert.Equal(WallCaptureStatus.Solving, capture.Status);
    }

    [Fact]
    public async Task AFinishResult_DoesNotWakeACaptureParkedInSolving_OnlyAPrepareResultDoes()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, claimed) = await ClaimedAsync(f, h, GpuJobKind.SplatFinish, capture: WallCaptureStatus.Solving);

        await f.Queue.AcceptResultAsync(runner, claimed.Id, new MemoryStream(SplatJobSupport.FinishResult()), null, null, CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await f.CaptureQueue.DequeueAsync(new CancellationTokenSource(TimeSpan.FromMilliseconds(300)).Token));
    }

    [Fact]
    public async Task AFinishResult_WakesAFinishedCapture_IntoItsPhotoRealStage()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, claimed) = await ClaimedAsync(f, h, GpuJobKind.SplatFinish);

        var outcome = await f.Queue.AcceptResultAsync(runner, claimed.Id, new MemoryStream(SplatJobSupport.FinishResult()), null, null, CancellationToken.None);

        Assert.Equal(RunnerJobOutcome.Ok, outcome);
        Assert.Equal(claimed.CaptureId, await f.CaptureQueue.DequeueAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token));
        await using var db = h.CreateContext();
        Assert.Equal(WallCaptureStatus.Splatting, (await db.WallCaptures.SingleAsync(c => c.Id == claimed.CaptureId)).Status);
    }

    [Fact]
    public async Task ARefusedResult_LeavesTheClaim_AndStoresNothing()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, claimed) = await ClaimedAsync(f, h, GpuJobKind.SplatFinish);
        var before = f.Files.ListFiles().Count;
        byte[][] refused =
        [
            RunnerFixture.SlimPly(10),
            SplatJobSupport.ResultZip(["wall.splat"], [("wall.splat", [1]), ("../evil.json", [1])]),
            SplatJobSupport.ResultZip(["wall.splat"], [("wall.splat", [1]), ("extra.json", [1])]),
            SplatJobSupport.ResultZip(["wall.splat"], [("wall.splat", new byte[20_000_000])], deflated: ["wall.splat"]),
        ];

        foreach (var body in refused)
        {
            Assert.Equal(RunnerJobOutcome.Invalid, await f.Queue.AcceptResultAsync(runner, claimed.Id, new MemoryStream(body), null, null, CancellationToken.None));
        }

        Assert.Equal(GpuJobStatus.Claimed, (await SplatJobSupport.JobAsync(h, claimed.Id)).Status);
        Assert.Equal(before, f.Files.ListFiles().Count);
    }

    [Fact]
    public void TheUploadCaps_AreThePlansPerKind()
    {
        var options = new GpuRunnerOptions { MaxBundleBytes = 6144L << 20, MaxResultBytes = 2048L << 20 };

        Assert.Equal((6144L + 512 + 64) << 20, options.MaxUploadBytes(GpuJobKind.SplatPrepare));
        Assert.Equal(2048L << 20, options.MaxUploadBytes(GpuJobKind.SplatFinish));
        Assert.Equal(2048L << 20, options.MaxUploadBytes(GpuJobKind.Splat));
        Assert.Equal(2048L << 20, options.MaxUploadBytes(GpuJobKind.Textures));
    }

    [Fact]
    public async Task OverHttp_AFinishResultPastTheResultCap_Is413_ButAPrepareResultOfTheSameSizeIsTaken()
    {
        using var h = new WallTestHarness();
        var options = new GpuRunnerOptions { ClaimWait = TimeSpan.Zero, MaxResultBytes = 64 * 1024 };
        using var f = await RunnerFixture.CreateAsync(h, options);
        await using var host = await RunnerApiTestHost.StartAsync(f);
        var (runner, key) = await f.AddRunnerAsync("pc", walls: h.WallId);
        var stored = await SplatJobSupport.HelloAsync(f, runner, 16_000, SplatJobSupport.PrepareAndFinish);
        var finish = await SplatJobSupport.AddJobAsync(f, h.WallId, GpuJobKind.SplatFinish);
        await f.Queue.TryClaimAsync(stored, null, CancellationToken.None);
        var big = SplatJobSupport.PrepareResult(bundleBytes: 100 * 1024);

        var tooBig = await host.SendAsync(HttpMethod.Put, $"{Api}/jobs/{finish.Id}/result", key, new ByteArrayContent(big));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
        Assert.Equal(
            RunnerJobOutcome.Ok, await f.Queue.AcceptResultAsync(stored, finish.Id, new MemoryStream(SplatJobSupport.FinishResult()), null, null, CancellationToken.None));
        var prepare = await SplatJobSupport.AddJobAsync(f, h.WallId, GpuJobKind.SplatPrepare);
        await f.Queue.TryClaimAsync(stored, null, CancellationToken.None);

        var taken = await host.SendAsync(HttpMethod.Put, $"{Api}/jobs/{prepare.Id}/result", key, new ByteArrayContent(big));

        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
    }

    [Fact]
    public async Task Close_DeletesTheInput_KeepsTheResult_AndMarksItInstalled()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, claimed) = await ClaimedAsync(f, h, GpuJobKind.SplatPrepare);
        await f.Queue.AcceptResultAsync(runner, claimed.Id, new MemoryStream(SplatJobSupport.PrepareResult()), null, null, CancellationToken.None);
        var delivered = await SplatJobSupport.JobAsync(h, claimed.Id);

        await f.Queue.CloseComputeAsync(claimed.Id, null, CancellationToken.None);

        var job = await SplatJobSupport.JobAsync(h, claimed.Id);
        Assert.NotNull(job.InstalledAt);
        Assert.Equal(delivered.ResultPath, job.ResultPath);
        var bundle = f.Files.ResolvePhysicalPath(job.BundlePath);
        Assert.False(bundle is not null && File.Exists(bundle));
        Assert.True(File.Exists(f.Files.ResolvePhysicalPath(job.ResultPath!)));
        Assert.Equal(new[] { job.ResultPath }, GpuJobQueue.Leftover(job).ToArray());
    }

    [Fact]
    public async Task CloseWithAnError_FailsTheJob_AndDeletesEverything()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, claimed) = await ClaimedAsync(f, h, GpuJobKind.SplatFinish);
        await f.Queue.AcceptResultAsync(runner, claimed.Id, new MemoryStream(SplatJobSupport.FinishResult()), null, null, CancellationToken.None);

        await f.Queue.CloseComputeAsync(claimed.Id, "could not read it", CancellationToken.None);

        var job = await SplatJobSupport.JobAsync(h, claimed.Id);
        Assert.Equal((GpuJobStatus.Failed, "could not read it", (string?)null), (job.Status, job.Error, job.ResultPath));
        Assert.Empty(f.Files.ListFiles());
    }

    [Fact]
    public async Task ADeliveredResultNobodyReadsForADay_IsWokenAgain_ThenGivenUp()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, new GpuRunnerOptions { SplatOnRunners = true });
        var (runner, claimed) = await ClaimedAsync(f, h, GpuJobKind.SplatPrepare);
        await f.Queue.AcceptResultAsync(runner, claimed.Id, new MemoryStream(SplatJobSupport.PrepareResult()), null, null, CancellationToken.None);
        _ = await f.CaptureQueue.DequeueAsync(CancellationToken.None);

        f.Clock.Advance(TimeSpan.FromMinutes(16));
        await f.Queue.SweepAsync(CancellationToken.None);
        Assert.Equal(GpuJobStatus.Succeeded, (await SplatJobSupport.JobAsync(h, claimed.Id)).Status);
        Assert.Equal(claimed.CaptureId, await f.CaptureQueue.DequeueAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token));

        f.Clock.Advance(TimeSpan.FromHours(25));
        await f.Queue.SweepAsync(CancellationToken.None);

        var job = await SplatJobSupport.JobAsync(h, claimed.Id);
        Assert.Equal(GpuJobStatus.Failed, job.Status);
        Assert.Contains("within a day", job.Error);
        Assert.Empty(f.Files.ListFiles());
    }

    private static async Task<(GpuRunner Runner, GpuJob Job)> ClaimedAsync(
        RunnerFixture f, WallTestHarness h, GpuJobKind kind, bool markerless = false, WallCaptureStatus capture = WallCaptureStatus.Succeeded)
    {
        var (runner, _) = await f.AddRunnerAsync("pc", walls: h.WallId);
        var stored = await SplatJobSupport.HelloAsync(f, runner, 16_000, SplatJobSupport.PrepareAndFinish);
        await SplatJobSupport.AddJobAsync(f, h.WallId, kind, markerless, capture);
        var job = await f.Queue.TryClaimAsync(stored, null, CancellationToken.None);
        return (stored, job!);
    }
}
