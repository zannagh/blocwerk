// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using Blocwerk.Core.Assets;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>GET /api/v1/me/walls</c>: the web My Walls list for a linked device key. The same walls in the same order as
/// <c>IWallService.GetMyWallsAsync</c> (the owner's memberships, nothing else), each with its member count and card
/// photo, and nothing at all for a plain personal key.
/// </summary>
public sealed class MeWallsControllerTests
{
    [Fact]
    public async Task ADeviceKey_GetsItsOwnersWalls_AsTheWebListShowsThem()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        await h.AddMemberAsync("member@test", WallRole.Member);
        var second = await AddWallAsync(h, "Second wall", h.Owner);

        var walls = await ListAsync(h, DeviceKeyTestPrincipals.Device());

        var web = await h.WallService.GetMyWallsAsync();
        Assert.Equal(web.Select(w => w.Id), walls.Select(w => w.Id));
        var seeded = walls.Single(w => w.Id == h.WallId);
        Assert.Equal(2, seeded.MemberCount);
        Assert.True(seeded.HasPhoto);
        var other = walls.Single(w => w.Id == second);
        Assert.Equal("Second wall", other.Name);
        Assert.Equal(1, other.MemberCount);
        Assert.False(other.HasPhoto);
        Assert.Null(other.PhotoSha);
    }

    [Fact]
    public async Task AMembersDeviceKey_SeesOnlyTheWallsTheyBelongTo()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var member = await h.AddMemberAsync("member@test", WallRole.Member);
        await AddWallAsync(h, "Owner only", h.Owner);
        h.ActingUser = member;

        var walls = await ListAsync(h, DeviceKeyTestPrincipals.Device());

        Assert.Equal([h.WallId], walls.Select(w => w.Id));
    }

    [Fact]
    public async Task AnOutsidersDeviceKey_GetsAnEmptyList()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var outsider = new User { Identifier = "outsider@test" };
        await using (var db = h.CreateContext())
        {
            db.Users.Add(outsider);
            await db.SaveChangesAsync();
        }

        h.ActingUser = outsider;

        Assert.Empty(await ListAsync(h, DeviceKeyTestPrincipals.Device()));
    }

    [Fact]
    public async Task APlainPersonalKey_OrAWallKey_Gets404()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        Assert.IsType<NotFoundResult>(await Controller(h, DeviceKeyTestPrincipals.Plain()).GetWalls(default));
        Assert.IsType<NotFoundResult>(await Controller(h, DeviceKeyTestPrincipals.Wall()).GetWalls(default));
    }

    private static async Task<List<MeWallResponse>> ListAsync(WallTestHarness h, ClaimsPrincipal user)
    {
        var controller = Controller(h, user);
        var ok = Assert.IsType<OkObjectResult>(await controller.GetWalls(default));
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        return Assert.IsType<List<MeWallResponse>>(ok.Value);
    }

    private static MeWallsController Controller(WallTestHarness h, ClaimsPrincipal user) =>
        new(h.WallService, new AssetHashReader(h.DbContextFactory, h.WallService))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } },
        };

    private static async Task<Guid> AddWallAsync(WallTestHarness h, string name, User owner)
    {
        await using var db = h.CreateContext();
        var wall = new Wall { Name = name, OwnerId = owner.Id };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = owner.Id, Role = WallRole.Admin });
        await db.SaveChangesAsync();
        return wall.Id;
    }
}
