// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The 3D-runner queue: key authentication, who may claim what (own runners first, shared runners
/// only for walls without an own runner online), the lease and its requeue, and job scoping.
/// </summary>
public class GpuRunnerQueueTests
{
    [Fact]
    public void Tokens_AreRunnerShaped_AndHashCompareInConstantTimeHelper()
    {
        var (token, prefix) = GpuRunnerTokens.Create();

        Assert.StartsWith("bwr_", token);
        Assert.Equal(68, token.Length);
        Assert.True(GpuRunnerTokens.LooksLikeRunnerKey(token));
        Assert.False(GpuRunnerTokens.LooksLikeRunnerKey("bwk_" + token[4..]));
        Assert.False(GpuRunnerTokens.LooksLikeRunnerKey(token.ToUpperInvariant()));
        Assert.Equal(token[..12], prefix);
        Assert.True(GpuRunnerTokens.HashEquals(GpuRunnerTokens.Hash(token), GpuRunnerTokens.Hash(token)));
        Assert.False(GpuRunnerTokens.HashEquals(GpuRunnerTokens.Hash(token), GpuRunnerTokens.Hash(token + "x")));
    }

    [Fact]
    public async Task Authenticate_AcceptsTheKey_RefusesOthersAndRevokedAtOnce()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, token) = await f.AddRunnerAsync("gpu", walls: h.WallId);

        Assert.Equal(runner.Id, (await f.Queue.AuthenticateAsync(token, CancellationToken.None))?.Id);
        Assert.Null(await f.Queue.AuthenticateAsync(GpuRunnerTokens.Create().Token, CancellationToken.None));
        Assert.Null(await f.Queue.AuthenticateAsync(null, CancellationToken.None));

        await using (var db = h.CreateContext())
        {
            await db.GpuRunners.Where(r => r.Id == runner.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, DateTimeOffset.UtcNow));
        }

        Assert.Null(await f.Queue.AuthenticateAsync(token, CancellationToken.None));
    }

    [Theory]
    [InlineData(true, false, false, 0)]
    [InlineData(true, true, true, 0)]
    [InlineData(false, true, false, 1)]
    [InlineData(false, true, true, null)]
    [InlineData(false, false, false, null)]
    public void Rank_OwnFirst_SharedOnlyWithoutAnOwnRunnerOnline(bool serves, bool shared, bool ownOnline, int? expected)
    {
        Assert.Equal(expected, GpuJobQueue.Rank(serves, shared, ownOnline));
    }

    [Fact]
    public async Task SharedRunner_WaitsWhileTheWallsOwnRunnerIsOnline_ThenHelps()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var other = await f.AddWallAsync("Other wall");
        var (own, _) = await f.AddRunnerAsync("own", walls: h.WallId);
        var (shared, _) = await f.AddRunnerAsync("shared", shared: true, walls: other);
        var job = await f.AddJobAsync(h.WallId);

        Assert.Null(await f.Queue.TryClaimAsync(shared, CancellationToken.None));

        f.Clock.Advance(TimeSpan.FromMinutes(2)); // the own runner went quiet
        var claimed = await f.Queue.TryClaimAsync(shared, CancellationToken.None);
        Assert.Equal(job.Id, claimed?.Id);
        Assert.Equal(shared.Id, claimed?.ClaimedByRunnerId);
        Assert.Null(await f.Queue.TryClaimAsync(own, CancellationToken.None));
    }

    [Fact]
    public async Task UnsharedRunner_NeverTakesAnotherWallsJob()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var other = await f.AddWallAsync("Other wall");
        var (mine, _) = await f.AddRunnerAsync("mine", walls: h.WallId);
        await f.AddJobAsync(other);

        Assert.Null(await f.Queue.TryClaimAsync(mine, CancellationToken.None));
    }

    [Fact]
    public async Task Claim_PrefersTheRunnersOwnWall_OverAnOlderSharedJob()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var other = await f.AddWallAsync("Other wall");
        var (runner, _) = await f.AddRunnerAsync("both", shared: true, walls: h.WallId);
        var older = await f.AddJobAsync(other);
        var own = await f.AddJobAsync(h.WallId);

        Assert.Equal(own.Id, (await f.Queue.TryClaimAsync(runner, CancellationToken.None))?.Id);
        Assert.Equal(older.Id, (await f.Queue.TryClaimAsync(runner, CancellationToken.None))?.Id);
    }

    [Fact]
    public async Task ExpiredLease_Requeues_ThenFailsAfterMaxAttempts_AndTheCaptureEnds()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("flaky", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);

        for (var attempt = 1; attempt <= f.Options.MaxAttempts; attempt++)
        {
            await MarkOnlineAsync(h, runner, f.Clock.GetUtcNow());
            Assert.Equal(job.Id, (await f.Queue.TryClaimAsync(runner, CancellationToken.None))?.Id);
            f.Clock.Advance(f.Options.Lease + TimeSpan.FromSeconds(1));
            Assert.Equal(1, await f.Queue.SweepAsync(CancellationToken.None));
        }

        await using var db = h.CreateContext();
        var row = await db.GpuJobs.SingleAsync();
        Assert.Equal(GpuJobStatus.Failed, row.Status);
        Assert.Equal(f.Options.MaxAttempts, row.Attempts);
        var capture = await db.WallCaptures.SingleAsync(c => c.Id == row.CaptureId);
        Assert.Equal(WallCaptureStatus.SucceededWithoutSplat, capture.Status);
        Assert.Contains("3D runner", capture.Error);
    }

    [Fact]
    public async Task Progress_ExtendsTheLease()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var (runner, _) = await f.AddRunnerAsync("steady", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, CancellationToken.None);

        f.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(RunnerJobOutcome.Ok, await f.Queue.ProgressAsync(runner, job.Id, new RunnerProgress(0.5, 2500, 5000, "train", null), CancellationToken.None));
        f.Clock.Advance(TimeSpan.FromMinutes(4));

        Assert.Equal(0, await f.Queue.SweepAsync(CancellationToken.None));
        await using var db = h.CreateContext();
        var capture = await db.WallCaptures.SingleAsync();
        Assert.Contains("step 2500/5000", capture.Stage);
    }

    [Fact]
    public async Task JobCalls_AreScopedToTheClaimingRunner()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var other = await f.AddWallAsync("Other wall");
        var (a, _) = await f.AddRunnerAsync("a", walls: h.WallId);
        var (b, _) = await f.AddRunnerAsync("b", shared: true, walls: other);
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(a, CancellationToken.None);

        Assert.Equal(RunnerJobOutcome.Ok, (await f.Queue.FindClaimedAsync(a, job.Id, CancellationToken.None)).Outcome);
        Assert.Equal(RunnerJobOutcome.NotYours, (await f.Queue.FindClaimedAsync(b, job.Id, CancellationToken.None)).Outcome);
        Assert.Equal(RunnerJobOutcome.NotYours, await f.Queue.ProgressAsync(b, job.Id, new RunnerProgress(1, null, null, "train", null), CancellationToken.None));
        Assert.Equal(RunnerJobOutcome.NotYours, await f.Queue.AcceptResultAsync(b, job.Id, new MemoryStream(RunnerFixture.SlimPly()), null, CancellationToken.None));

        await f.Queue.CancelForCaptureAsync(job.CaptureId, "cancelled by an admin", CancellationToken.None);
        Assert.Equal(RunnerJobOutcome.Gone, (await f.Queue.FindClaimedAsync(a, job.Id, CancellationToken.None)).Outcome);
        Assert.Equal(RunnerJobOutcome.Gone, await f.Queue.ProgressAsync(a, job.Id, new RunnerProgress(1, null, null, "train", null), CancellationToken.None));
    }

    private static async Task MarkOnlineAsync(WallTestHarness h, GpuRunner runner, DateTimeOffset at)
    {
        await using var db = h.CreateContext();
        await db.GpuRunners.Where(r => r.Id == runner.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.LastSeenAt, at));
    }
}
