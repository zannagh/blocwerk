// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>GET /api/v1/users/{id}</c>: another member's profile as the web shows it, to someone who shares a wall with them,
/// and the page's "unavailable" (404 here) to anyone else.
/// </summary>
public sealed class UsersControllerTests
{
    [Fact]
    public async Task AWallMate_SeesTheIdentityCardAndTheProgression()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var member = await h.AddMemberAsync("member@test", WallRole.Member);

        var ok = Assert.IsType<OkObjectResult>(await Controller(h, member).Get(h.Owner.Id));

        var profile = Assert.IsType<UserProfileResponse>(ok.Value);
        Assert.Equal(h.Owner.Id, profile.Id);
        Assert.Equal(h.Owner.Name, profile.Name);
        Assert.Equal("User", profile.Role);
        Assert.Null(profile.AvatarUrl);
        Assert.Equal(UsersController.DefaultWindowDays, profile.Progression.WindowDays);
        Assert.Equal("Week", profile.Progression.GroupBy);
    }

    [Fact]
    public async Task TheProgressionWindow_FollowsTheQuery_ClampedLikeTheWeb()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var member = await h.AddMemberAsync("member@test", WallRole.Member);

        var ok = Assert.IsType<OkObjectResult>(await Controller(h, member).Get(h.Owner.Id, 1000, ProgressionGroupBy.Month));

        var profile = Assert.IsType<UserProfileResponse>(ok.Value);
        Assert.Equal(365, profile.Progression.WindowDays);
        Assert.Equal("Month", profile.Progression.GroupBy);
    }

    [Fact]
    public async Task SomeoneWhoSharesNoWall_AndAnUnknownId_Get404()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var outsider = await MeProfileControllerTests.AddUserAsync(h, "outsider@test");

        Assert.IsType<NotFoundResult>(await Controller(h, outsider).Get(h.Owner.Id));
        Assert.IsType<NotFoundResult>(await Controller(h, h.Owner).Get(outsider.Id));
        Assert.IsType<NotFoundResult>(await Controller(h, h.Owner).Get(Guid.NewGuid()));
    }

    [Fact]
    public async Task TheCaller_SeesTheirOwnPublicProfile()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var outsider = await MeProfileControllerTests.AddUserAsync(h, "outsider@test");

        Assert.IsType<OkObjectResult>(await Controller(h, outsider).Get(outsider.Id));
    }

    [Fact]
    public async Task AnUnknownGrouping_IsA400()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        Assert.IsType<BadRequestObjectResult>(await Controller(h, h.Owner).Get(h.Owner.Id, 90, (ProgressionGroupBy)9));
    }

    private static UsersController Controller(WallTestHarness h, User viewer)
    {
        var currentUser = MeProfileControllerTests.RealCurrentUser(h, viewer);
        var progression = new ProgressionService(h.DbContextFactory, currentUser, h.WallService, NullLogger<ProgressionService>.Instance);
        return new UsersController(currentUser, h.WallService, progression)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = DeviceKeyTestPrincipals.Device() },
            },
        };
    }
}
