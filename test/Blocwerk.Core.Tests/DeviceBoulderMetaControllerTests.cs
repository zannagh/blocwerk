// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>GET /api/v1/walls/{w}/boulder-meta</c>: creator and setter ids, beta clips, review state and the edit right per
/// boulder, for members only.
/// </summary>
public sealed class DeviceBoulderMetaControllerTests
{
    [Fact]
    public async Task AMember_SeesWhoSetEachBoulder_ItsFlags_AndWhetherTheyMayEditIt()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var setter = await h.AddMemberAsync("setter@test", WallRole.Member);
        var member = await h.AddMemberAsync("member@test", WallRole.Member);
        var set = await h.BoulderService.CreateBoulderAsync(h.WallId, "Set", "6A", [new BoulderHoldInput(holds[0].Id)], setterUserIds: [setter.Id]);
        var plain = await h.BoulderService.CreateBoulderAsync(h.WallId, "Plain", "6A", [new BoulderHoldInput(holds[1].Id)]);
        await h.BetaVideoService.AddVideoAsync(set.Id, [0, 0, 0, 0x18], "video/mp4", null, "beta.mp4");
        await using (var db = h.CreateContext())
        {
            await db.Boulders.Where(b => b.Id == plain.Id).ExecuteUpdateAsync(u => u.SetProperty(b => b.NeedsReview, true));
        }

        h.ActingUser = setter;
        var asSetter = Meta(await Controller(h).Get(h.WallId, default)).ToDictionary(m => m.BoulderId);
        Assert.Equal((h.Owner.Id, true, false, true), (asSetter[set.Id].CreatedByUserId, asSetter[set.Id].HasBetaVideo, asSetter[set.Id].NeedsReview, asSetter[set.Id].CanEdit));
        Assert.Equal([setter.Id], asSetter[set.Id].SetterIds);
        Assert.Equal((true, false), (asSetter[plain.Id].NeedsReview, asSetter[plain.Id].CanEdit));

        h.ActingUser = member;
        Assert.All(Meta(await Controller(h).Get(h.WallId, default)), m => Assert.False(m.CanEdit));
        h.ActingUser = h.Owner;
        Assert.All(Meta(await Controller(h).Get(h.WallId, default)), m => Assert.True(m.CanEdit));
    }

    [Fact]
    public async Task AnOutsider_Gets404()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        await using (var db = h.CreateContext())
        {
            var outsider = new User { Identifier = "outsider@test", DisplayName = "Outsider" };
            db.Users.Add(outsider);
            await db.SaveChangesAsync();
            h.ActingUser = outsider;
        }

        Assert.IsType<NotFoundResult>(await Controller(h).Get(h.WallId, default));
    }

    private static List<DeviceBoulderMeta> Meta(IActionResult result) =>
        Assert.IsType<List<DeviceBoulderMeta>>(Assert.IsType<OkObjectResult>(result).Value);

    private static DeviceBoulderMetaController Controller(WallTestHarness h) =>
        new(h.DbContextFactory, h.CurrentUser)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = DeviceKeyTestPrincipals.Device() },
            },
        };
}
