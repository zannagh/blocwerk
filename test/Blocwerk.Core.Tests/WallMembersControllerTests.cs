// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>GET /api/v1/walls/{w}/members</c> and <c>/hold-links</c>: the wall page's member list to members, the link
/// reminder's numbers to admins and moderators, and 404 for anyone who is not on the wall.
/// </summary>
public sealed class WallMembersControllerTests
{
    [Fact]
    public async Task AMember_SeesTheMembersInJoinOrder_WithRolesAndTheOwnerMarked()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var moderator = await h.AddMemberAsync("mod@test", WallRole.Moderator);
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);

        var ok = Assert.IsType<OkObjectResult>(await Controller(h).GetMembers(h.WallId, default));

        var members = Assert.IsType<List<WallMemberResponse>>(ok.Value);
        Assert.Equal([h.Owner.Id, moderator.Id, h.ActingUser.Id], members.Select(m => m.UserId));
        Assert.True(members[0].IsOwner);
        Assert.Equal("Admin", members[0].Role);
        Assert.Equal("Moderator", members[1].Role);
        Assert.Equal("Member", members[2].Role);
        Assert.Equal("mod@test", members[1].Name);
    }

    [Fact]
    public async Task AnOutsider_Gets404_OnBothRoutes()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        h.ActingUser = await MeProfileControllerTests.AddUserAsync(h, "outsider@test");

        Assert.IsType<NotFoundResult>(await Controller(h).GetMembers(h.WallId, default));
        Assert.IsType<NotFoundResult>(await Controller(h).GetHoldLinks(h.WallId, default));
        Assert.IsType<NotFoundResult>(await Controller(h).GetMembers(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task APlainMember_Gets403_OnTheLinkNumbers_AModeratorGetsThem()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
        var refused = Assert.IsType<ObjectResult>(await Controller(h).GetHoldLinks(h.WallId, default));
        Assert.Equal(StatusCodes.Status403Forbidden, refused.StatusCode);

        h.ActingUser = await h.AddMemberAsync("mod@test", WallRole.Moderator);
        var ok = Assert.IsType<OkObjectResult>(await Controller(h).GetHoldLinks(h.WallId, default));
        Assert.Equal(new WallHoldLinksResponse(0, 0, false), ok.Value);
    }

    [Fact]
    public async Task TheDeviceRoutes_RemoveAndLeave_WithTheServicesRules()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var member = await h.AddMemberAsync("member@test", WallRole.Member);
        var other = await h.AddMemberAsync("other@test", WallRole.Member);
        var service = WallMembershipServiceTests.Service(h);

        h.ActingUser = member;
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(await Controller(h).RemoveMember(h.WallId, other.Id, service, default)).StatusCode);
        Assert.IsType<NoContentResult>(await Controller(h).Leave(h.WallId, service, default));
        Assert.IsType<NotFoundResult>(await Controller(h).Leave(h.WallId, service, default));

        h.ActingUser = h.Owner;
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(await Controller(h).Leave(h.WallId, service, default)).StatusCode);
        Assert.IsType<NotFoundObjectResult>(await Controller(h).RemoveMember(h.WallId, member.Id, service, default));
        Assert.IsType<NoContentResult>(await Controller(h).RemoveMember(h.WallId, other.Id, service, default));
    }

    [Fact]
    public async Task TheLinkCount_CountsOnlyLinksBetweenLiveHoldsOfLivePanels()
    {
        var wallId = Guid.NewGuid();
        var (a, b, c, old) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var panelA = Panel(live: true);
        var panelB = Panel(live: true);
        var panels = Substitute.For<IWallPanelService>();
        panels.GetPanelsAsync(wallId).Returns([panelA, panelB, Panel(live: false)]);
        panels.GetPanelHoldsAsync(wallId, panelA.Id, false).Returns([Hold(a), Hold(c)]);
        panels.GetPanelHoldsAsync(wallId, panelB.Id, false).Returns([Hold(b)]);
        panels.GetHoldLinksAsync(wallId).Returns([new HoldLinkPair(a, b), new HoldLinkPair(c, b), new HoldLinkPair(old, b)]);

        var counted = await WallHoldLinkCount.CountAsync(panels, wallId, linksFinalized: true);

        Assert.Equal(new WallHoldLinksResponse(2, 2, true), counted);
    }

    [Fact]
    public async Task OnePanel_CountsNoLinks()
    {
        var wallId = Guid.NewGuid();
        var panels = Substitute.For<IWallPanelService>();
        panels.GetPanelsAsync(wallId).Returns([Panel(live: true)]);

        Assert.Equal(new WallHoldLinksResponse(1, 0, false), await WallHoldLinkCount.CountAsync(panels, wallId, false));
    }

    private static WallPanelInfo Panel(bool live) => new(Guid.NewGuid(), 0, 0, live, false, 0, false);

    private static PanelHold Hold(Guid id) => new(id, 0.5, 0.5, 0.01, null, HoldCategory.Hand);

    private static WallMembersController Controller(WallTestHarness h)
    {
        var panels = Substitute.For<IWallPanelService>();
        panels.GetPanelsAsync(Arg.Any<Guid>()).Returns(new List<WallPanelInfo>());
        return new(h.DbContextFactory, h.CurrentUser, h.WallService, panels)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = DeviceKeyTestPrincipals.Device() },
            },
        };
    }
}
