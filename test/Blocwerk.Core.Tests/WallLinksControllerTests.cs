// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Visits;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

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
    public async Task AnOutsider_Gets404_LikeAnUnknownWall()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        h.ActingUser = await MeProfileControllerTests.AddUserAsync(h, "outsider@test");

        Assert.IsType<NotFoundResult>(await Controller(h).GetInvite(h.WallId));
        Assert.IsType<NotFoundResult>(await Controller(h).RotateInvite(h.WallId));
        Assert.IsType<NotFoundResult>(await Controller(h).CreateVisit(h.WallId, default));
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

    [Fact]
    public async Task ThePreview_NamesTheWall_WithoutJoiningIt()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var token = Link(await Controller(h).GetInvite(h.WallId)).Token;
        var newcomer = await MeProfileControllerTests.AddUserAsync(h, "newcomer@test");
        h.ActingUser = newcomer;

        var ok = Assert.IsType<OkObjectResult>(await Controller(h).PreviewJoin(token, Substitute.For<IKioskContext>(), default));

        Assert.Equal(new JoinPreviewResponse(h.WallId, "Test Wall", 1), ok.Value);
        Assert.IsType<NotFoundObjectResult>(await Controller(h).PreviewJoin("not-a-token", Substitute.For<IKioskContext>(), default));
        await using var db = h.CreateContext();
        Assert.False(await db.WallMembers.AnyAsync(m => m.UserId == newcomer.Id));
    }

    [Fact]
    public async Task ThePreview_IsRefusedOnAKiosk()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var token = Link(await Controller(h).GetInvite(h.WallId)).Token;
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);

        AssertStatus(await Controller(h).PreviewJoin(token, kiosk, default), StatusCodes.Status403Forbidden);
    }

    private static WallLinkResponse Link(IActionResult result) =>
        Assert.IsType<WallLinkResponse>(Assert.IsType<OkObjectResult>(result).Value);

    private static void AssertStatus(IActionResult result, int status) =>
        Assert.Equal(status, Assert.IsType<ObjectResult>(result).StatusCode);

    [Fact]
    public async Task Links_StartWithTheConfiguredServerAddress()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var settings = new BlocwerkSettings();
        settings.Server.Url = "https://blocwerk.app/";

        var invite = Link(await Controller(h, settings).GetInvite(h.WallId));

        Assert.Equal($"https://blocwerk.app/join/{invite.Token}", invite.Url);
    }

    private static WallLinksController Controller(WallTestHarness h, BlocwerkSettings? settings = null)
    {
        var http = new DefaultHttpContext { User = DeviceKeyTestPrincipals.Device() };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("blocwerk.test");
        var visits = new WallVisitService(h.DbContextFactory, h.CurrentUser, NullLogger<WallVisitService>.Instance);
        return new WallLinksController(h.WallService, visits, h.DbContextFactory, h.CurrentUser, settings ?? new BlocwerkSettings())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }
}
