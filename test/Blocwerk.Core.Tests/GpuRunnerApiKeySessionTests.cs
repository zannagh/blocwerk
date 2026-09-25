// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Runners;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A session signed in with an API key must not mint, revoke or re-scope runner keys, nor consent to a wall's photos
/// going to a shared runner; cancelling a capture's job stays a normal wall-admin data action.
/// </summary>
public class GpuRunnerApiKeySessionTests
{
    [Fact]
    public async Task EveryKeyOrSharingChange_IsRefusedInAKeySession_AndChangesNothing()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        await GpuRunnerServiceTests.MakeSiteAdminAsync(h, h.Owner);
        var (runner, _) = await f.AddRunnerAsync("mine", shared: true, walls: h.WallId);
        var locked = Service(h, f, keySession: true);

        Assert.False(locked.CanChangeRunners);
        await AssertRefused(() => locked.CreateAsync(h.WallId, "leaked"));
        await AssertRefused(() => locked.RevokeAsync(runner.Id));
        await AssertRefused(() => locked.SetServesWallAsync(runner.Id, h.WallId, false));
        await AssertRefused(() => locked.SetSharedAsync(runner.Id, false));
        await AssertRefused(() => locked.SetRunnerApprovalAsync(h.WallId, runner.Id, true));

        await using var db = h.CreateContext();
        var row = await db.GpuRunners.SingleAsync();
        Assert.Null(row.RevokedAt);
        Assert.True(row.SharedWithOtherWalls);
        Assert.True(await db.GpuRunnerWalls.AnyAsync(rw => rw.RunnerId == runner.Id && rw.WallId == h.WallId));
        Assert.False(await db.GpuRunnerApprovals.AnyAsync());
    }

    [Fact]
    public async Task AKeySession_StillReadsRunners_AndCancelsACapturesJob()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        await f.AddRunnerAsync("mine", walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        var locked = Service(h, f, keySession: true);

        Assert.Single(await locked.ListForWallAsync(h.WallId));
        Assert.Single(await locked.ListJobsForWallAsync(h.WallId));
        Assert.True(await locked.CancelCaptureJobAsync(job.CaptureId));
    }

    [Fact]
    public async Task ANormalSession_MayChangeRunners()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        Assert.True(Service(h, f, keySession: false).CanChangeRunners);
        Assert.True(new GpuRunnerService(h.DbContextFactory, h.CurrentUser, f.Queue, NullLogger<GpuRunnerService>.Instance).CanChangeRunners);
        await Service(h, f, keySession: false).CreateAsync(h.WallId, "fine");
    }

    private static async Task AssertRefused(Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<ApiKeySessionRestrictedException>(action);
        Assert.Equal("Not available in a session signed in with an API key.", ex.Message);
    }

    private static GpuRunnerService Service(WallTestHarness h, RunnerFixture f, bool keySession)
    {
        var session = Substitute.For<IApiKeySessionContext>();
        session.IsApiKeySession.Returns(keySession);
        return new(h.DbContextFactory, h.CurrentUser, f.Queue, NullLogger<GpuRunnerService>.Instance, apiKeySession: session);
    }
}
