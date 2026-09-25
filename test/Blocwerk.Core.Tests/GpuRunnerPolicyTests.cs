// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The owner-decision defaults of the runner queue: claims respect the runner's quality (draft &lt; high &lt; max &lt;
/// ultra), a shutdown costs nothing, training failures and vanished runners have separate budgets, shared runners need
/// the receiving wall's opt-in, and a runner never sees a wall its owner no longer administers.
/// </summary>
public class GpuRunnerPolicyTests
{
    [Fact]
    public async Task Claim_TakesOnlyJobsUpToTheRunnersQuality_AndTheClaimsCap()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("8 GB", maxQuality: "high", walls: h.WallId);
        var max = await f.AddJobAsync(h.WallId, SplatQuality.Max);
        var high = await f.AddJobAsync(h.WallId, SplatQuality.High);

        Assert.Null(await f.Queue.TryClaimAsync(runner, "draft", CancellationToken.None));
        Assert.Equal(high.Id, (await f.Queue.TryClaimAsync(runner, null, CancellationToken.None))?.Id);
        Assert.Null(await f.Queue.TryClaimAsync(runner, null, CancellationToken.None));

        var (big, _) = await f.AddRunnerAsync("24 GB", maxQuality: "ultra", walls: h.WallId);
        Assert.Equal(max.Id, (await f.Queue.TryClaimAsync(big, null, CancellationToken.None))?.Id);
    }

    [Theory]
    [InlineData(null, null, SplatQuality.High)]
    [InlineData("max", null, SplatQuality.Max)]
    [InlineData("max", "high", SplatQuality.High)]
    [InlineData("high", "ultra", SplatQuality.High)]
    [InlineData("nonsense", null, SplatQuality.High)]
    public void QualityCap_UnknownIsHigh_AndTheClaimCanOnlyLowerIt(string? reported, string? requested, SplatQuality expected)
    {
        Assert.Equal(expected, GpuJobQueue.QualityCap(reported, requested));
    }

    [Theory]
    [InlineData("ultra", "gsplat", 12282, "ultra")]
    [InlineData("ultra", "gsplat", 8192, "max")]
    [InlineData("ultra", "brush", 24576, "max")]
    [InlineData("ultra", null, 24576, "max")]
    [InlineData("high", "brush", 8192, "high")]
    [InlineData("huge", "gsplat", 24576, null)]
    public void Hello_AcceptsUltra_OnlyFromGsplatWithTwelveGigabytes(string reported, string? trainer, int vram, string? expected)
    {
        Assert.Equal(expected, GpuJobQueue.AcceptedMaxQuality(reported, trainer, vram));
    }

    [Fact]
    public async Task Shutdown_RequeuesAtOnce_WithoutUsingAnAttempt()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("laptop", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);

        for (var i = 0; i < f.Options.MaxAttempts + 2; i++)
        {
            Assert.Equal(job.Id, (await f.Queue.TryClaimAsync(runner, null, CancellationToken.None))?.Id);
            var outcome = await f.Queue.FailAsync(runner, job.Id, new RunnerFailure("the runner was shut down", true, Shutdown: true), CancellationToken.None);
            Assert.Equal(RunnerJobOutcome.Ok, outcome);
        }

        await using var db = h.CreateContext();
        var row = await db.GpuJobs.SingleAsync();
        Assert.Equal(GpuJobStatus.Queued, row.Status);
        Assert.Equal(0, row.FailureCount);
        Assert.Equal(0, row.LostLeaseCount);
        Assert.Null(row.ClaimedByRunnerId);
    }

    [Fact]
    public async Task RetryableFailures_UseTheAttemptBudget_ThenTheCaptureEndsWithoutAView()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("oom", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);

        for (var i = 1; i <= f.Options.MaxAttempts; i++)
        {
            Assert.Equal(job.Id, (await f.Queue.TryClaimAsync(runner, null, CancellationToken.None))?.Id);
            await f.Queue.FailAsync(runner, job.Id, new RunnerFailure("out of memory", true), CancellationToken.None);
        }

        Assert.Null(await f.Queue.TryClaimAsync(runner, null, CancellationToken.None));
        await using var db = h.CreateContext();
        var row = await db.GpuJobs.SingleAsync();
        Assert.Equal(GpuJobStatus.Failed, row.Status);
        Assert.Equal(f.Options.MaxAttempts, row.FailureCount);
        Assert.Equal(WallCaptureStatus.SucceededWithoutSplat, (await db.WallCaptures.SingleAsync()).Status);
    }

    [Fact]
    public async Task NonRetryableFailure_FailsAtOnce()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("strict", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);

        await f.Queue.FailAsync(runner, job.Id, new RunnerFailure("bad bundle", false), CancellationToken.None);

        await using var db = h.CreateContext();
        Assert.Equal(GpuJobStatus.Failed, (await db.GpuJobs.SingleAsync()).Status);
    }

    [Fact]
    public async Task SharedRunner_NeedsTheReceivingWallsOptIn_UnlessTheServerTurnsThatOff()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var other = await f.AddWallAsync("Other wall");
        var (shared, _) = await f.AddRunnerAsync("shared", shared: true, walls: other);
        var job = await f.AddJobAsync(h.WallId);

        Assert.Null(await f.Queue.TryClaimAsync(shared, null, CancellationToken.None));
        Assert.False(await f.Queue.HasEligibleRunnerOnlineAsync(h.WallId, SplatQuality.Draft, CancellationToken.None));

        await f.OptInAsync(h.WallId);
        Assert.True(await f.Queue.HasEligibleRunnerOnlineAsync(h.WallId, SplatQuality.Draft, CancellationToken.None));
        Assert.Equal(job.Id, (await f.Queue.TryClaimAsync(shared, null, CancellationToken.None))?.Id);

        using var h2 = new WallTestHarness();
        using var open = await RunnerFixture.CreateAsync(h2, new GpuRunnerOptions { ClaimWait = TimeSpan.Zero, SharedNeedsOptIn = false });
        var other2 = await open.AddWallAsync("Other wall");
        var (shared2, _) = await open.AddRunnerAsync("shared", shared: true, walls: other2);
        var job2 = await open.AddJobAsync(h2.WallId);
        Assert.Equal(job2.Id, (await open.Queue.TryClaimAsync(shared2, null, CancellationToken.None))?.Id);
    }

    [Fact]
    public async Task Runner_NeverSeesAWallItsOwnerDoesNotAdminister()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var foreign = await f.AddWallAsync("Someone else's wall", foreign: true);
        var (runner, _) = await f.AddRunnerAsync("mine", walls: foreign);
        await f.AddJobAsync(foreign);

        Assert.Null(await f.Queue.TryClaimAsync(runner, null, CancellationToken.None));
        Assert.False(await f.Queue.HasEligibleRunnerOnlineAsync(foreign, SplatQuality.Draft, CancellationToken.None));
    }

    [Fact]
    public async Task EligibleRunnerOnline_ConsidersQualityAndOnlineState()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        await f.AddRunnerAsync("high only", maxQuality: "high", walls: h.WallId);

        Assert.True(await f.Queue.HasEligibleRunnerOnlineAsync(h.WallId, SplatQuality.High, CancellationToken.None));
        Assert.False(await f.Queue.HasEligibleRunnerOnlineAsync(h.WallId, SplatQuality.Max, CancellationToken.None));
        Assert.False(await f.Queue.UltraAvailableForWallAsync(h.WallId, CancellationToken.None));

        f.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(await f.Queue.HasEligibleRunnerOnlineAsync(h.WallId, SplatQuality.Draft, CancellationToken.None));

        await f.AddRunnerAsync("ultra box", online: false, maxQuality: "ultra", walls: h.WallId);
        Assert.True(await f.Queue.UltraAvailableForWallAsync(h.WallId, CancellationToken.None));
    }
}
