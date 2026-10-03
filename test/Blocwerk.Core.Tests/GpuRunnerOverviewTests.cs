// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Jobs;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The runner overview: a site admin sees every runner and every job; a wall admin the runners that serve their walls (and
/// their own), another wall's job only as "busy" and its failures without the reason; a member nothing; a kiosk is refused.
/// What a runner does comes from the progress read model; offline wins over paused.
/// </summary>
public class GpuRunnerOverviewTests
{
    [Fact]
    public async Task AWallAdmin_SeesTheRunnersOfTheirWalls_AndAnotherWallsJobOnlyAsBusy()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var s = await SeedAsync(h, f);

        var overview = await Service(h, f).GetAsync(default);

        Assert.False(overview.IsAppAdmin);
        Assert.Equal(new[] { "home PC", "shared GPU" }, overview.Runners.Select(r => r.Name).Order());
        var mine = overview.Runners.Single(r => r.Id == s.Mine);
        Assert.Equal((false, 1000, "yours"), (mine.Current!.OtherWall, mine.Current.Job!.Step, mine.IsMine ? "yours" : "other"));
        Assert.True(mine.CanRevoke);
        Assert.False(mine.CanShare);
        var shared = overview.Runners.Single(r => r.Id == s.Shared);
        Assert.Same(GpuRunnerActivity.Busy, shared.Current);
        Assert.Equal([h.WallId], shared.Walls.Select(w => w.WallId));
        Assert.Equal(1, shared.OtherWallCount);
        Assert.Equal((1, GpuRunnerOverviewComposer.OtherWallFailure), (shared.Failures.Count, shared.Failures.LastReason));
        Assert.False(shared.CanRevoke);
    }

    [Fact]
    public async Task ASiteAdmin_SeesEveryRunner_EveryJob_AndEveryReason()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var s = await SeedAsync(h, f);
        await using (var db = h.CreateContext())
        {
            await db.Users.Where(u => u.Id == h.Owner.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.Role, IdentityRole.Admin));
        }

        var overview = await Service(h, f).GetAsync(default);

        Assert.True(overview.IsAppAdmin);
        Assert.Equal(3, overview.Runners.Count);
        var shared = overview.Runners.Single(r => r.Id == s.Shared);
        Assert.Equal((false, s.ForeignJob), (shared.Current!.OtherWall, shared.Current.Job!.GpuJobId!.Value));
        Assert.Equal("out of memory", shared.Failures.LastReason);
        Assert.Equal(2, shared.Walls.Count);
        Assert.True(shared.CanRevoke && shared.CanShare);
    }

    [Fact]
    public async Task AMember_SeesNoRunner_AndAKioskIsRefused()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        await SeedAsync(h, f);
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(h.WallId);

        await Assert.ThrowsAsync<Services.KioskRestrictedException>(() => Service(h, f, kiosk).GetAsync(default));
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
        Assert.Empty((await Service(h, f).GetAsync(default)).Runners);
    }

    [Fact]
    public async Task TheOwnerOfASharedRunner_SeesItsJobAndWalls_ButNotAnotherWallsErrors()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var foreignWall = await f.AddWallAsync("Stranger's wall", foreign: true);
        await using var db = h.CreateContext();
        var stranger = await db.Walls.IgnoreQueryFilters().Where(w => w.Id == foreignWall).Select(w => w.OwnerId).SingleAsync();
        var wallName = await db.Walls.IgnoreQueryFilters().Where(w => w.Id == h.WallId).Select(w => w.Name).SingleAsync();
        var (shared, _) = await f.AddRunnerAsync("shared GPU", shared: true, ownerId: stranger, walls: foreignWall);
        await f.ApproveAsync(h.WallId, shared);
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(shared, null, default);
        await db.GpuJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(x => x.SetProperty(j => j.Error, "the owner's wall's private error"));
        db.GpuRunnerFailures.Add(new GpuRunnerFailure { RunnerId = shared.Id, JobId = job.Id, WallId = h.WallId, At = f.Clock.GetUtcNow(), Reason = "raw" });
        await db.SaveChangesAsync();
        h.ActingUser = await db.Users.SingleAsync(u => u.Id == stranger);

        var row = Assert.Single((await Service(h, f).GetAsync(default)).Runners);

        Assert.Equal((false, job.Id), (row.Current!.OtherWall, row.Current.Job!.GpuJobId!.Value));
        Assert.Null(row.Current.Job.LastError);
        Assert.Equal((2, 0), (row.Walls.Count, row.OtherWallCount));
        Assert.Equal($"{GpuRunnerOverviewComposer.OtherWallFailure} ({wallName})", row.Failures.LastReason);
        Assert.True(row.IsMine && row.CanRevoke);
    }

    [Fact]
    public async Task ARunnersFailure_IsRecordedWithItsOwnReason_WhenItGivesTheJobBack()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, null, default);

        await f.Queue.FailAsync(runner, job.Id, new RunnerFailure("CUDA out of memory", Retryable: true), default);

        await using var db = h.CreateContext();
        var failure = await db.GpuRunnerFailures.SingleAsync();
        Assert.Equal((runner.Id, job.Id, h.WallId), (failure.RunnerId, failure.JobId, failure.WallId));
        Assert.Contains("CUDA out of memory", failure.Reason);
    }

    [Fact]
    public void TheHeldJob_IsTheRunningOne_ThenTheNewestClaim_AndABlankCustomNameIsUnset()
    {
        var runner = Guid.NewGuid();
        var at = DateTimeOffset.UnixEpoch;
        GpuRunnerHeldJob Job(int n, bool running, int minutes) => new(new Guid(n, 0, 0, new byte[8]), runner, Guid.Empty, null, null, running, at.AddMinutes(minutes));

        Assert.Equal(Job(2, true, 1).JobId, GpuRunnerOverviewComposer.Held([Job(1, false, 9), Job(2, true, 1), Job(3, true, 0)], runner)!.JobId);
        Assert.Equal(Job(1, false, 9).JobId, GpuRunnerOverviewComposer.Held([Job(4, false, 2), Job(1, false, 9)], runner)!.JobId);
        Assert.Equal("Display", GpuRunnerOverviewService.OwnerName("  ", "Display"));
        Assert.Equal("Custom", GpuRunnerOverviewService.OwnerName("Custom", "Display"));
    }

    [Theory]
    [InlineData(true, true, GpuRunnerStates.Paused)]
    [InlineData(false, true, GpuRunnerStates.Offline)]
    [InlineData(true, false, GpuRunnerStates.Online)]
    [InlineData(false, false, GpuRunnerStates.Offline)]
    public void OfflineWinsOverPaused(bool seenRecently, bool paused, string state)
    {
        var now = DateTimeOffset.UtcNow;
        var runner = new GpuRunner
        {
            Name = "r", KeyHash = "h", KeyPrefix = "p", Paused = paused, LastSeenAt = seenRecently ? now : now.AddHours(-1),
        };
        var viewer = new GpuRunnerViewer(Guid.NewGuid(), true, new HashSet<Guid>());

        var row = Assert.Single(GpuRunnerOverviewComposer.Compose(
            viewer, [new GpuRunnerOverviewInput(runner, "Owner", [])], [], new Dictionary<Guid, JobProgressItem>(), [], now.AddMinutes(-1)));

        Assert.Equal(state, row.State);
        Assert.Null(row.Current);
        Assert.Equal(GpuRunnerFailures.None, row.Failures);
    }

    private static GpuRunnerOverviewService Service(WallTestHarness h, RunnerFixture f, IKioskContext? kiosk = null) =>
        new(h.DbContextFactory, h.CurrentUser, f.Queue, new JobProgressReader(h.RootContextFactory, clock: f.Clock, runnerQueue: f.Queue), kiosk, f.Clock);

    /// <summary>
    /// The owner's runner training a job of their wall; a stranger's shared runner (approved for the owner's wall) training a
    /// job of the stranger's wall, with a failed job of that wall; and a stranger's private runner.
    /// </summary>
    private static async Task<(Guid Mine, Guid Shared, Guid ForeignJob)> SeedAsync(WallTestHarness h, RunnerFixture f)
    {
        var foreignWall = await f.AddWallAsync("Stranger's wall", foreign: true);
        Guid stranger;
        await using (var db = h.CreateContext())
        {
            stranger = await db.Walls.IgnoreQueryFilters().Where(w => w.Id == foreignWall).Select(w => w.OwnerId).SingleAsync();
        }

        var (shared, _) = await f.AddRunnerAsync("shared GPU", shared: true, ownerId: stranger, walls: foreignWall);
        await f.ApproveAsync(h.WallId, shared);
        await f.AddRunnerAsync("private GPU", ownerId: stranger, walls: foreignWall);
        var foreignJob = await f.AddJobAsync(foreignWall);
        await f.Queue.TryClaimAsync(shared, null, default);
        var failed = await f.AddJobAsync(foreignWall);
        await using (var db = h.CreateContext())
        {
            db.GpuRunnerFailures.Add(new GpuRunnerFailure
            {
                RunnerId = shared.Id, JobId = failed.Id, WallId = foreignWall, At = f.Clock.GetUtcNow(), Reason = "out of memory",
            });
            await db.SaveChangesAsync();
        }

        var (mine, _) = await f.AddRunnerAsync("home PC", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(mine, null, default);
        await f.Queue.ProgressAsync(mine, job.Id, new RunnerProgress(0.1, 1000, 10000, "train", null), default);
        return (mine.Id, shared.Id, foreignJob.Id);
    }
}
