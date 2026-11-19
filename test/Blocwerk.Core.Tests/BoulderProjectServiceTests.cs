// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A boulder project is a per-user mark like a favourite: the signed-in member toggles it, nobody else sees it, and
/// anonymous, non-member and kiosk sessions are refused.
/// </summary>
public sealed class BoulderProjectServiceTests
{
    [Fact]
    public async Task Toggle_FlipsTheMark_AndSetIsIdempotent()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h, "B");
        var projects = Service(h);

        Assert.False(await projects.IsProjectAsync(boulder.Id));
        Assert.True(await projects.ToggleProjectAsync(boulder.Id));
        Assert.True(await projects.IsProjectAsync(boulder.Id));
        Assert.True(await projects.SetProjectAsync(boulder.Id, true));
        Assert.Equal(1, await CountAsync(h));
        Assert.False(await projects.ToggleProjectAsync(boulder.Id));
        Assert.False(await projects.SetProjectAsync(boulder.Id, false));
        Assert.Equal(0, await CountAsync(h));
    }

    [Fact]
    public async Task TheMark_IsPerUser()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h, "B");
        var projects = Service(h);
        await projects.SetProjectAsync(boulder.Id, true);

        h.ActingUser = await h.AddMemberAsync("other@test", WallRole.Member);

        Assert.False(await projects.IsProjectAsync(boulder.Id));
        Assert.Empty(await projects.GetMyProjectsAsync(h.WallId));
        Assert.False((await h.FeedbackService.GetBoulderListAsync(h.WallId)).Single().IsProject);
    }

    [Fact]
    public async Task Anonymous_IsRefused()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h, "B");
        h.CurrentUser.GetCurrentUserAsync().ThrowsAsync(new UnauthorizedAccessException("not signed in"));
        var projects = Service(h);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => projects.SetProjectAsync(boulder.Id, true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => projects.ToggleProjectAsync(boulder.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => projects.GetMyProjectsAsync(h.WallId));
    }

    [Fact]
    public async Task ANonMember_AndAKiosk_AreRefused_AndNothingIsStored()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h, "B");
        var stranger = new User { Identifier = "stranger@test", DisplayName = "Stranger" };
        await using (var db = h.CreateContext())
        {
            db.Users.Add(stranger);
            await db.SaveChangesAsync();
        }

        h.ActingUser = stranger;
        var asStranger = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(h).SetProjectAsync(boulder.Id, true));
        Assert.Equal(BoulderMemberGuard.NotAMember, asStranger.Message);

        h.ActingUser = h.Owner;
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(h, kiosk).SetProjectAsync(boulder.Id, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(h, kiosk).IsProjectAsync(boulder.Id));
        Assert.Equal(0, await CountAsync(h));
    }

    [Fact]
    public async Task MyProjects_AreTheMarkedBouldersOfThatWall_NewestMarkFirst_AndTheListFlagsThem()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 3);
        var first = await h.BoulderService.CreateBoulderAsync(h.WallId, "First", "6A", [new BoulderHoldInput(holds[0].Id)]);
        var second = await h.BoulderService.CreateBoulderAsync(h.WallId, "Second", "6B", [new BoulderHoldInput(holds[1].Id)]);
        var unmarked = await h.BoulderService.CreateBoulderAsync(h.WallId, "Unmarked", "6C", [new BoulderHoldInput(holds[2].Id)]);
        var projects = Service(h);
        await projects.SetProjectAsync(first.Id, true);
        await projects.SetProjectAsync(second.Id, true);

        var mine = await projects.GetMyProjectsAsync(h.WallId);
        var list = await h.FeedbackService.GetBoulderListAsync(h.WallId);

        Assert.Equal(["Second", "First"], mine.Select(i => i.Boulder.Name));
        Assert.All(mine, i => Assert.True(i.IsProject));
        Assert.False(list.Single(i => i.Boulder.Id == unmarked.Id).IsProject);
        Assert.Empty(await projects.GetMyProjectsAsync(Guid.NewGuid()));
    }

    private static BoulderProjectService Service(WallTestHarness h, IKioskContext? kiosk = null) =>
        new(h.DbContextFactory, h.CurrentUser, kiosk ?? Substitute.For<IKioskContext>(), h.FeedbackService, NullLogger<BoulderProjectService>.Instance);

    private static async Task<int> CountAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.BoulderProjects.CountAsync();
    }

    private static async Task<Boulder> SeedBoulderAsync(WallTestHarness h, string name)
    {
        var holds = await h.SeedWallAsync(holdCount: 1);
        return await h.BoulderService.CreateBoulderAsync(h.WallId, name, null, [new BoulderHoldInput(holds[0].Id)]);
    }
}
