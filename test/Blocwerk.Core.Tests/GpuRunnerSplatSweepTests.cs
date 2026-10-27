// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.Extensions.Configuration;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The sweep of splat prepare / finish jobs: the waiting texts, the queued lifetimes (shorter for a markerless capture), the
/// longer prepare claim, and the clean-up of queued jobs while the flag is off.
/// </summary>
public class GpuRunnerSplatSweepTests
{
    private static readonly GpuRunnerOptions On = new() { ClaimWait = TimeSpan.Zero, SplatOnRunners = true };

    [Theory]
    [InlineData(GpuJobKind.SplatPrepare, false, "waiting for a 3D runner to prepare the photos (none online)")]
    [InlineData(GpuJobKind.SplatPrepare, true, "waiting for a 3D runner to reconstruct the wall (none online)")]
    [InlineData(GpuJobKind.SplatFinish, false, "waiting for a 3D runner to finish it (none online)")]
    public async Task TheWaitingText_SaysWhatIsWaitingFor_WhenNoCapableRunnerIsOnline(GpuJobKind kind, bool markerless, string expected)
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, On);
        var (old, _) = await f.AddRunnerAsync("old", walls: h.WallId);
        await SplatJobSupport.HelloAsync(f, old, 64_000, RunnerCapabilities.Splat);
        var job = await SplatJobSupport.AddJobAsync(f, h.WallId, kind, markerless);

        await f.Queue.SweepAsync(CancellationToken.None);

        Assert.Equal(expected, (await SplatJobSupport.JobAsync(h, job.Id)).Stage);
    }

    [Fact]
    public async Task TheWaitingText_CountsCapableRunners_AndIgnoresOnesWithTooLittleMemory()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, On);
        var (small, _) = await f.AddRunnerAsync("small", walls: h.WallId);
        await SplatJobSupport.HelloAsync(f, small, 2048, SplatJobSupport.PrepareAndFinish);
        var job = await SplatJobSupport.AddJobAsync(f, h.WallId, GpuJobKind.SplatPrepare);
        await f.Queue.SweepAsync(CancellationToken.None);
        Assert.Contains("none online", (await SplatJobSupport.JobAsync(h, job.Id)).Stage);

        var (big, _) = await f.AddRunnerAsync("big", walls: h.WallId);
        await SplatJobSupport.HelloAsync(f, big, 16_000, SplatJobSupport.PrepareAndFinish);
        await f.Queue.SweepAsync(CancellationToken.None);

        Assert.Equal("waiting for a 3D runner (1 online)", (await SplatJobSupport.JobAsync(h, job.Id)).Stage);
    }

    [Theory]
    [InlineData(GpuJobKind.SplatPrepare, false, 13, 15, "no 3D runner prepared the photos within 14 days")]
    [InlineData(GpuJobKind.SplatPrepare, true, 2, 4, "no 3D runner reconstructed the wall within 3 days")]
    [InlineData(GpuJobKind.SplatFinish, false, 13, 15, "no 3D runner finished the 3D view within 14 days")]
    public async Task AQueuedJob_ExpiresAfterItsKindsLifetime(GpuJobKind kind, bool markerless, int stillQueuedAfterDays, int expiredAfterDays, string reason)
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, On);
        var job = await SplatJobSupport.AddJobAsync(f, h.WallId, kind, markerless);

        f.Clock.Advance(TimeSpan.FromDays(stillQueuedAfterDays));
        await f.Queue.SweepAsync(CancellationToken.None);
        Assert.Equal(GpuJobStatus.Queued, (await SplatJobSupport.JobAsync(h, job.Id)).Status);

        f.Clock.Advance(TimeSpan.FromDays(expiredAfterDays - stillQueuedAfterDays));
        await f.Queue.SweepAsync(CancellationToken.None);

        var expired = await SplatJobSupport.JobAsync(h, job.Id);
        Assert.Equal((GpuJobStatus.Cancelled, reason), (expired.Status, expired.Error));
        Assert.Empty(f.Files.ListFiles());
    }

    [Fact]
    public async Task APrepareClaim_MayRunTenHours_AFinishOrTrainingOnlySix()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, On);
        var (runner, _) = await f.AddRunnerAsync("pc", walls: h.WallId);
        var stored = await SplatJobSupport.HelloAsync(f, runner, 16_000, SplatJobSupport.PrepareAndFinish);
        var prepare = await SplatJobSupport.AddJobAsync(f, h.WallId, GpuJobKind.SplatPrepare);
        await f.Queue.TryClaimAsync(stored, null, CancellationToken.None);
        var report = new RunnerProgress(0.5, null, null, "run", null);

        f.Clock.Advance(TimeSpan.FromHours(7));
        Assert.Equal(RunnerJobOutcome.Ok, await f.Queue.ProgressAsync(stored, prepare.Id, report, CancellationToken.None));
        Assert.Equal("is preparing the photos", (await SplatJobSupport.JobAsync(h, prepare.Id)).Stage);

        f.Clock.Advance(TimeSpan.FromHours(3.1));
        var late = await f.Queue.ProgressAsync(stored, prepare.Id, report, CancellationToken.None);

        Assert.NotEqual(RunnerJobOutcome.Ok, late);
        Assert.Equal("the preparation took longer than 10 h", (await SplatJobSupport.JobAsync(h, prepare.Id)).Error);
    }

    [Fact]
    public async Task AFinishClaim_StopsAtTheSixHourCap()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, On);
        var (runner, _) = await f.AddRunnerAsync("pc", walls: h.WallId);
        var stored = await SplatJobSupport.HelloAsync(f, runner, 16_000, SplatJobSupport.PrepareAndFinish);
        var finish = await SplatJobSupport.AddJobAsync(f, h.WallId, GpuJobKind.SplatFinish);
        await f.Queue.TryClaimAsync(stored, null, CancellationToken.None);

        f.Clock.Advance(TimeSpan.FromHours(6.1));

        Assert.NotEqual(RunnerJobOutcome.Ok, await f.Queue.ProgressAsync(stored, finish.Id, new RunnerProgress(0.5, null, null, "run", null), CancellationToken.None));
    }

    [Fact]
    public async Task WithTheFlagOff_TheSweepCancelsQueuedPrepareAndFinishJobs_AndDeletesTheirInput_NotOthers()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, new GpuRunnerOptions { ClaimWait = TimeSpan.Zero });
        var prepare = await SplatJobSupport.AddJobAsync(f, h.WallId, GpuJobKind.SplatPrepare);
        var finish = await SplatJobSupport.AddJobAsync(f, h.WallId, GpuJobKind.SplatFinish);
        var splat = await f.AddJobAsync(h.WallId);

        await f.Queue.SweepAsync(CancellationToken.None);

        foreach (var id in new[] { prepare.Id, finish.Id })
        {
            var job = await SplatJobSupport.JobAsync(h, id);
            Assert.Equal(GpuJobStatus.Cancelled, job.Status);
            Assert.Contains("no longer prepare or finish", job.Error);
            var bundle = f.Files.ResolvePhysicalPath(job.BundlePath);
            Assert.False(bundle is not null && File.Exists(bundle));
        }

        Assert.Equal(GpuJobStatus.Queued, (await SplatJobSupport.JobAsync(h, splat.Id)).Status);
    }

    [Fact]
    public async Task WithTheFlagOn_TheSweepLeavesThemQueued()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, On);
        var prepare = await SplatJobSupport.AddJobAsync(f, h.WallId, GpuJobKind.SplatPrepare);

        await f.Queue.SweepAsync(CancellationToken.None);

        Assert.Equal(GpuJobStatus.Queued, (await SplatJobSupport.JobAsync(h, prepare.Id)).Status);
    }

    [Fact]
    public void TheFlagAndTheLimits_BindFromConfiguration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Blocwerk:SplatService:OnRunners"] = "true", ["Blocwerk:Runners:PrepareMemoryMb"] = "8000", ["Blocwerk:Runners:FinishMemoryMb"] = "4000",
                ["Blocwerk:Runners:PrepareMaxHours"] = "12", ["Blocwerk:Runners:MarkerlessQueuedDays"] = "2",
            }).Build();

        var bound = GpuRunnerOptions.Bind(config);

        Assert.Equal((true, 8000, 4000), (bound.SplatOnRunners, bound.PrepareMemoryMb, bound.FinishMemoryMb));
        Assert.Equal((TimeSpan.FromHours(12), TimeSpan.FromDays(2)), (bound.PrepareMaxJobDuration, bound.MarkerlessQueuedLifetime));
        var defaults = GpuRunnerOptions.Bind(null);
        Assert.Equal((false, 6144, 3072), (defaults.SplatOnRunners, defaults.PrepareMemoryMb, defaults.FinishMemoryMb));
    }
}
