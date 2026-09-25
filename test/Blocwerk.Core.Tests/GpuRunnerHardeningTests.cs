// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// What a runner may hold and for how long: nothing when its owner is deleted, the Ghost or locked out; one claim at a
/// time; never past the wall-clock cap however often it heartbeats; a few free shutdowns, then they cost; an approval
/// that lapses with its approver's admin right; and a sweep that cannot overwrite an upload that won the race.
/// </summary>
public class GpuRunnerHardeningTests
{
    [Theory]
    [InlineData("deleted")]
    [InlineData("locked")]
    [InlineData("ghost")]
    public async Task Runner_OfADeletedLockedOutOrGhostOwner_IsRefused(string state)
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var other = await f.AddWallAsync("Other wall");
        var owner = state == "ghost" ? GhostUser.Id : await AddUserAsync(h, f, state);
        var (runner, key) = await f.AddRunnerAsync("shared", shared: true, ownerId: owner, walls: other);
        await f.ApproveAsync(h.WallId, runner);
        await f.AddJobAsync(h.WallId);

        Assert.Null(await f.Queue.AuthenticateAsync(key, CancellationToken.None));
        Assert.Null(await f.Queue.TryClaimAsync(runner, null, CancellationToken.None));
        Assert.False(await f.Queue.HasEligibleRunnerOnlineAsync(h.WallId, SplatQuality.Draft, CancellationToken.None));
    }

    [Fact]
    public async Task Runner_HoldsOneClaimAtATime()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("greedy", walls: h.WallId);
        var first = await f.AddJobAsync(h.WallId);
        var second = await f.AddJobAsync(h.WallId);

        Assert.Equal(first.Id, (await f.Queue.TryClaimAsync(runner, null, CancellationToken.None))?.Id);
        Assert.Null(await f.Queue.TryClaimAsync(runner, null, CancellationToken.None));
        await f.Queue.FailAsync(runner, first.Id, new RunnerFailure("bad", false), CancellationToken.None);
        Assert.Equal(second.Id, (await f.Queue.TryClaimAsync(runner, null, CancellationToken.None))?.Id);
    }

    [Fact]
    public async Task Heartbeats_NeverKeepAClaimPastTheMaxJobDuration()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, new GpuRunnerOptions { ClaimWait = TimeSpan.Zero, MaxJobDuration = TimeSpan.FromMinutes(30) });
        var (runner, _) = await f.AddRunnerAsync("forever", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);
        var report = new RunnerProgress(0.1, null, null, "train", null);

        for (var i = 0; i < 7; i++)
        {
            f.Clock.Advance(TimeSpan.FromMinutes(4));
            Assert.Equal(RunnerJobOutcome.Ok, await f.Queue.ProgressAsync(runner, job.Id, report, CancellationToken.None));
        }

        await using (var db = h.CreateContext())
        {
            // 28 min in: the lease ends at the 30 min cap, not 5 min after the heartbeat.
            var row = await db.GpuJobs.SingleAsync();
            Assert.Equal(row.ClaimedAt + f.Options.MaxJobDuration, row.LeaseExpiresAt);
        }

        f.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(1, await f.Queue.SweepAsync(CancellationToken.None));
        Assert.Equal(RunnerJobOutcome.NotYours, await f.Queue.ProgressAsync(runner, job.Id, report, CancellationToken.None));
        await using var after = h.CreateContext();
        var released = await after.GpuJobs.SingleAsync();
        Assert.Equal((GpuJobStatus.Queued, 1, 0), (released.Status, released.FailureCount, released.LostLeaseCount));
    }

    [Fact]
    public async Task Shutdowns_PastTheFreeBudget_CostAnAttempt()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, new GpuRunnerOptions { ClaimWait = TimeSpan.Zero, MaxFreeShutdowns = 2, MaxAttempts = 2 });
        var (runner, _) = await f.AddRunnerAsync("flapping", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        var shutdown = new RunnerFailure("the runner was shut down", true, Shutdown: true);

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(job.Id, (await f.Queue.TryClaimAsync(runner, null, CancellationToken.None))?.Id);
            await f.Queue.FailAsync(runner, job.Id, shutdown, CancellationToken.None);
        }

        await using var db = h.CreateContext();
        var row = await db.GpuJobs.SingleAsync();
        Assert.Equal((GpuJobStatus.Failed, 4, 2), (row.Status, row.ShutdownCount, row.FailureCount));
    }

    [Fact]
    public async Task Approval_LapsesWhenTheApproverIsNoLongerTheWallsAdmin()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var other = await f.AddWallAsync("Other wall");
        var (shared, _) = await f.AddRunnerAsync("shared", shared: true, walls: other);
        var coAdmin = await h.AddMemberAsync("co-admin@test", Enums.WallRole.Admin);
        await f.ApproveAsync(h.WallId, shared, coAdmin.Id);
        await f.AddJobAsync(h.WallId);
        await using (var db = h.CreateContext())
        {
            await db.WallMembers.Where(m => m.UserId == coAdmin.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, Enums.WallRole.Member));
        }

        Assert.Null(await f.Queue.TryClaimAsync(shared, null, CancellationToken.None));
    }

    [Fact]
    public async Task ASweepThatLostTheRace_DoesNotOverwriteADeliveredResult()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("gpu", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);
        await using var db = h.CreateContext();
        var stale = await db.GpuJobs.AsNoTracking().SingleAsync(); // what a sweep read just before the upload landed

        Assert.Equal(RunnerJobOutcome.Ok, await f.Queue.AcceptResultAsync(runner, job.Id, new MemoryStream(RunnerFixture.SlimPly()), null, null, CancellationToken.None));
        Assert.False(await f.Queue.ReleaseAsync(db, stale, GpuJobQueue.ReleaseKind.LostLease, "late sweep", CancellationToken.None));

        var row = await db.GpuJobs.AsNoTracking().SingleAsync();
        Assert.Equal((GpuJobStatus.Succeeded, 0), (row.Status, row.LostLeaseCount));
    }

    private static async Task<Guid> AddUserAsync(WallTestHarness h, RunnerFixture f, string state)
    {
        var user = new User
        {
            Identifier = $"{state}-{Guid.NewGuid():N}",
            DisplayName = state,
            DeletedAt = state == "deleted" ? f.Clock.GetUtcNow() : null,
            LockoutUntil = state == "locked" ? f.Clock.GetUtcNow() + TimeSpan.FromHours(1) : null,
        };
        await using var db = h.CreateContext();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }
}
