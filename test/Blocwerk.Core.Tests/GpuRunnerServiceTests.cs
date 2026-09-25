// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>Managing runners from the UI: wall admins only, never from a kiosk, key shown once and stored hashed.</summary>
public class GpuRunnerServiceTests
{
    [Fact]
    public async Task Create_ReturnsTheKeyOnce_StoresOnlyItsHash_AndServesTheWall()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);

        var created = await Service(h, f).CreateAsync(h.WallId, "Cellar PC");

        Assert.StartsWith("bwr_", created.Key);
        await using var db = h.CreateContext();
        var row = await db.GpuRunners.SingleAsync();
        Assert.Equal(GpuRunnerTokens.Hash(created.Key), row.KeyHash);
        Assert.DoesNotContain(created.Key, row.KeyHash + row.KeyPrefix + row.Name);
        Assert.True(await db.GpuRunnerWalls.AnyAsync(rw => rw.RunnerId == row.Id && rw.WallId == h.WallId));
        Assert.True(created.Runner.ServesThisWall);
        Assert.Equal(row.Id, (await f.Queue.AuthenticateAsync(created.Key, CancellationToken.None))?.Id);
    }

    [Fact]
    public async Task Create_FromAKiosk_IsRefused()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(_ => h.WallId);

        await Assert.ThrowsAsync<KioskRestrictedException>(() => Service(h, f, kiosk).CreateAsync(h.WallId, "tablet"));
    }

    [Fact]
    public async Task NonAdmins_CannotCreateOrChangeRunners()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var created = await Service(h, f).CreateAsync(h.WallId, "mine");
        var stranger = new User { Identifier = "stranger@test", DisplayName = "Stranger" };
        await using (var db = h.CreateContext())
        {
            db.Users.Add(stranger);
            await db.SaveChangesAsync();
        }

        h.ActingUser = stranger;
        var service = Service(h, f);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync(h.WallId, "theirs"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SetSharedAsync(created.Runner.Id, true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RevokeAsync(created.Runner.Id));
    }

    [Fact]
    public async Task Revoke_StopsTheKeyAtOnce_AndRequeuesItsJob()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var created = await Service(h, f).CreateAsync(h.WallId, "gpu");
        var runner = (await f.Queue.AuthenticateAsync(created.Key, CancellationToken.None))!;
        var job = await f.AddJobAsync(h.WallId);
        await f.Queue.TryClaimAsync(runner, null, CancellationToken.None);

        await Service(h, f).RevokeAsync(runner.Id);

        Assert.Null(await f.Queue.AuthenticateAsync(created.Key, CancellationToken.None));
        await using var db = h.CreateContext();
        var row = await db.GpuJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Equal(GpuJobStatus.Queued, row.Status);
        Assert.Null(row.ClaimedByRunnerId);
    }

    [Fact]
    public async Task OnlyASiteAdmin_SharesARunner_AndEachWallAdminApprovesThatRunner()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        var created = await Service(h, f).CreateAsync(h.WallId, "mine");
        var id = created.Runner.Id;

        // The owner (a wall admin, not a site admin) cannot offer it; nobody can approve a runner not offered.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(h, f).SetSharedAsync(id, true));
        await Assert.ThrowsAsync<UserFacingException>(() => Service(h, f).SetRunnerApprovalAsync(h.WallId, id, true));

        await MakeSiteAdminAsync(h, h.Owner);
        await Service(h, f).SetSharedAsync(id, true);
        await Service(h, f).SetRunnerApprovalAsync(h.WallId, id, true);
        Assert.True((await Service(h, f).ListForWallAsync(h.WallId)).Single().ApprovedForThisWall);

        h.ActingUser = await h.AddMemberAsync("setter@test", Enums.WallRole.Member);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(h, f).SetRunnerApprovalAsync(h.WallId, id, false));

        // Un-sharing drops every approval: a later re-share needs fresh consent.
        h.ActingUser = h.Owner;
        await Service(h, f).SetSharedAsync(id, false);
        await using var db = h.CreateContext();
        Assert.False(await db.GpuRunnerApprovals.AnyAsync());
    }

    [Fact]
    public async Task Create_IsCappedPerUser_AndRefusedWhenRunnersAreOff()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, new GpuRunnerOptions { ClaimWait = TimeSpan.Zero, MaxRunnersPerUser = 2 });
        await Service(h, f).CreateAsync(h.WallId, "one");
        var two = await Service(h, f).CreateAsync(h.WallId, "two");
        await Assert.ThrowsAsync<UserFacingException>(() => Service(h, f).CreateAsync(h.WallId, "three"));
        await Service(h, f).RevokeAsync(two.Runner.Id);
        await Service(h, f).CreateAsync(h.WallId, "three");

        using var h2 = new WallTestHarness();
        using var off = await RunnerFixture.CreateAsync(h2, new GpuRunnerOptions { Mode = GpuRunnerMode.Off });
        Assert.False(Service(h2, off).Enabled);
        await Assert.ThrowsAsync<UserFacingException>(() => Service(h2, off).CreateAsync(h2.WallId, "any"));
    }

    internal static async Task MakeSiteAdminAsync(WallTestHarness h, User user)
    {
        await using var db = h.CreateContext();
        await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, Enums.IdentityRole.Admin));
    }

    private static GpuRunnerService Service(WallTestHarness h, RunnerFixture f, IKioskContext? kiosk = null) =>
        new(h.DbContextFactory, h.CurrentUser, f.Queue, NullLogger<GpuRunnerService>.Instance, kiosk);
}
