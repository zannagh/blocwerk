// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The capture side of the runners: with a runner for the wall the photo-real stage is prepared on
/// the server and the capture WAITS (model and textures live, no worker held) until a runner
/// delivers; then the server finishes and installs the splat like the all-in-one path.
/// </summary>
public class GpuRunnerCaptureTests
{
    [Fact]
    public async Task WithARunner_TheCaptureWaits_ThenFinishesOnTheServer()
    {
        using var h = new WallTestHarness();
        using var s = Scenario(h, out var clock);
        var captureId = await s.StartCaptureAsync();
        var (runner, _) = await AddRunnerAsync(h, clock);

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using (var db = h.CreateContext())
        {
            var capture = await db.WallCaptures.SingleAsync();
            Assert.Equal(WallCaptureStatus.AwaitingRunner, capture.Status);
            Assert.NotNull(capture.GeometryModelId);
            Assert.Equal(2, await db.WallGeometryTextures.CountAsync());
            Assert.Equal(GpuJobStatus.Queued, (await db.GpuJobs.SingleAsync()).Status);
        }

        Assert.Equal(["splat-prepare"], s.SplatClient.MultipartSubmissions.Select(m => m.Kind));
        clock.Advance(TimeSpan.FromMinutes(5)); // nobody online
        await s.Runners!.SweepAsync(CancellationToken.None);
        await using (var db = h.CreateContext())
        {
            Assert.Equal("Photo-real view: waiting for a 3D runner (none online)", (await db.WallCaptures.SingleAsync()).Stage);
        }

        var job = await s.Runners.TryClaimAsync(runner, CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(RunnerJobOutcome.Ok, await s.Runners.AcceptResultAsync(runner, job.Id, new MemoryStream(RunnerFixture.SlimPly()), null, CancellationToken.None));
        Assert.Equal(captureId, await s.Queue.DequeueAsync(CancellationToken.None));
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        Assert.Equal(["splat-prepare", "splat-finish"], s.SplatClient.MultipartSubmissions.Select(m => m.Kind));
        await using (var db = h.CreateContext())
        {
            var capture = await db.WallCaptures.SingleAsync();
            Assert.Equal(WallCaptureStatus.Succeeded, capture.Status);
            Assert.Equal(capture.GeometryModelId, (await db.WallGeometrySplats.SingleAsync()).GeometryModelId);
            Assert.NotNull((await db.GpuJobs.SingleAsync()).InstalledAt);
        }
    }

    [Fact]
    public async Task WithoutAnyRunner_AutoModeKeepsTheAllInOnePath()
    {
        using var h = new WallTestHarness();
        using var s = Scenario(h, out _);
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        Assert.Equal(["splat"], s.SplatClient.MultipartSubmissions.Select(m => m.Kind));
        await using var db = h.CreateContext();
        Assert.Equal(WallCaptureStatus.Succeeded, (await db.WallCaptures.SingleAsync()).Status);
        Assert.Empty(await db.GpuJobs.ToListAsync());
    }

    [Fact]
    public async Task Cancelling_EndsTheWaitingCaptureWithoutASplat()
    {
        using var h = new WallTestHarness();
        using var s = Scenario(h, out var clock);
        var captureId = await s.StartCaptureAsync();
        await AddRunnerAsync(h, clock);
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        Assert.True(await s.Runners!.CancelForCaptureAsync(captureId, "cancelled by an admin", CancellationToken.None));

        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Equal(WallCaptureStatus.SucceededWithoutSplat, capture.Status);
        Assert.Contains("cancelled by an admin", capture.Error);
        Assert.Equal(GpuJobStatus.Cancelled, (await db.GpuJobs.SingleAsync()).Status);
    }

    private static CaptureScenario Scenario(WallTestHarness h, out MutableTestClock clock)
    {
        clock = new MutableTestClock(DateTimeOffset.UtcNow);
        var s = new CaptureScenario(h, runnerOptions: new GpuRunnerOptions { ClaimWait = TimeSpan.Zero }, clock: clock);
        s.SplatClient.IsConfigured = true;
        var ply = RunnerFixture.SlimPly();
        s.SplatClient.Download = name => name switch
        {
            "bundle.zip" => RunnerFixture.Bundle(),
            "prepared.json" => "{\"version\":1}"u8.ToArray(),
            "frame.json" => System.Text.Encoding.UTF8.GetBytes(s.SplatClient.FrameJson),
            "wall.spz" => s.SplatClient.Spz,
            _ => CaptureScenario.TinyJpeg(),
        };
        return s;
    }

    private static async Task<(GpuRunner Runner, string Token)> AddRunnerAsync(WallTestHarness h, MutableTestClock clock)
    {
        var (token, prefix) = GpuRunnerTokens.Create();
        var runner = new GpuRunner
        {
            Name = "gpu", OwnerUserId = h.Owner.Id, KeyHash = GpuRunnerTokens.Hash(token), KeyPrefix = prefix, LastSeenAt = clock.GetUtcNow(),
        };
        await using var db = h.CreateContext();
        db.GpuRunners.Add(runner);
        db.GpuRunnerWalls.Add(new GpuRunnerWall { RunnerId = runner.Id, WallId = h.WallId });
        await db.SaveChangesAsync();
        return (runner, token);
    }
}
