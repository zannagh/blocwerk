// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Blocwerk.Core.Visits;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The wall page's link buttons for a device key: Invite for any member, Generate Share Link for admins, Visit for members
/// up to the hourly cap, and joining from an invite token. Outsiders are refused.
/// </summary>
public sealed class WallLinksControllerTests
{
    [Fact]
    public async Task AMember_GetsTheSameInviteLinkEachTime()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);

        var first = Link(await Controller(h).GetInvite(h.WallId));
        var again = Link(await Controller(h).GetInvite(h.WallId));

        Assert.Equal(first.Token, again.Token);
        Assert.Equal($"https://blocwerk.test/join/{first.Token}", first.Url);
        Assert.Null(first.ExpiresAt);
    }

    [Fact]
    public async Task OnlyAnAdmin_RotatesTheInvite_AndTheOldOneStopsWorking()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var old = Link(await Controller(h).GetInvite(h.WallId)).Token;
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
        AssertStatus(await Controller(h).RotateInvite(h.WallId), StatusCodes.Status403Forbidden);

        h.ActingUser = h.Owner;
        var rotated = Link(await Controller(h).RotateInvite(h.WallId)).Token;

        Assert.NotEqual(old, rotated);
        h.ActingUser = await MeProfileControllerTests.AddUserAsync(h, "late@test");
        Assert.IsType<NotFoundObjectResult>(await Controller(h).Join(old));
        Assert.IsType<OkObjectResult>(await Controller(h).Join(rotated));
    }

    [Fact]
    public async Task AnOutsider_GetsNoInviteAndNoVisitorLink()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        h.ActingUser = await MeProfileControllerTests.AddUserAsync(h, "outsider@test");

        AssertStatus(await Controller(h).GetInvite(h.WallId), StatusCodes.Status403Forbidden);
        AssertStatus(await Controller(h).RotateInvite(h.WallId), StatusCodes.Status403Forbidden);
        AssertStatus(await Controller(h).CreateVisit(h.WallId, default), StatusCodes.Status403Forbidden);
        Assert.IsType<NotFoundResult>(await Controller(h).GetInvite(Guid.NewGuid()));
    }

    [Fact]
    public async Task AMember_MakesVisitorLinks_UpToTheHourlyCap()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);

        var first = Link(await Controller(h).CreateVisit(h.WallId, default));
        for (var i = 1; i < WallVisitService.HourlyLimit; i++)
        {
            Link(await Controller(h).CreateVisit(h.WallId, default));
        }

        Assert.Equal($"https://blocwerk.test/visit/{first.Token}", first.Url);
        Assert.True(first.ExpiresAt > DateTimeOffset.UtcNow);
        AssertStatus(await Controller(h).CreateVisit(h.WallId, default), StatusCodes.Status429TooManyRequests);
    }

    [Fact]
    public async Task Join_MakesTheCallerAMember_Once_AndABadTokenIs404()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var token = Link(await Controller(h).GetInvite(h.WallId)).Token;
        var newcomer = await MeProfileControllerTests.AddUserAsync(h, "newcomer@test");
        h.ActingUser = newcomer;

        var joined = Assert.IsType<JoinedWallResponse>(Assert.IsType<OkObjectResult>(await Controller(h).Join(token)).Value);
        Assert.IsType<OkObjectResult>(await Controller(h).Join(token));
        Assert.IsType<NotFoundObjectResult>(await Controller(h).Join("not-a-token"));

        Assert.Equal(h.WallId, joined.WallId);
        await using var db = h.CreateContext();
        var membership = await db.WallMembers.SingleAsync(m => m.WallId == h.WallId && m.UserId == newcomer.Id);
        Assert.Equal(WallRole.Member, membership.Role);
    }

    private static WallLinkResponse Link(IActionResult result) =>
        Assert.IsType<WallLinkResponse>(Assert.IsType<OkObjectResult>(result).Value);

    private static void AssertStatus(IActionResult result, int status) =>
        Assert.Equal(status, Assert.IsType<ObjectResult>(result).StatusCode);

    private static WallLinksController Controller(WallTestHarness h)
    {
        var http = new DefaultHttpContext { User = DeviceKeyTestPrincipals.Device() };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("blocwerk.test");
        var visits = new WallVisitService(h.DbContextFactory, h.CurrentUser, NullLogger<WallVisitService>.Instance);
        return new WallLinksController(h.WallService, visits)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }
}
