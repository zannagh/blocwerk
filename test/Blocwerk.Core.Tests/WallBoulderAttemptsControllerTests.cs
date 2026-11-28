// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>GET /api/v1/walls/{w}/boulders/{b}/attempts</c> for the companion app: the web boulder page's History, everyone's
/// rows with their names, newest first, for the wall's members only. Who may call at all is
/// <see cref="DeviceApiRoutePostureTests"/>.
/// </summary>
public sealed class WallBoulderAttemptsControllerTests
{
    [Fact]
    public async Task AMember_SeesEveryMembersRows_NewestFirst_WithNames_AndTheirOwnMarked()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedAsync(h);
        var member = await h.AddMemberAsync("member@test", WallRole.Member);
        var at = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        await AddAttemptsAsync(
            h,
            new Attempt { BoulderId = boulder, UserId = h.Owner.Id, Type = AttemptType.Attempt, Timestamp = at },
            new Attempt { BoulderId = boulder, UserId = member.Id, Type = AttemptType.Flash, Timestamp = at.AddHours(1) });

        var rows = Rows(await Controller(h).GetAttempts(h.WallId, boulder));

        Assert.Equal(2, rows.Count);
        Assert.Equal((member.Id, member.Name, "Flash", false), (rows[0].UserId, rows[0].UserName, rows[0].Type, rows[0].Mine));
        Assert.Equal((h.Owner.Id, "Attempt", true), (rows[1].UserId, rows[1].Type, rows[1].Mine));
    }

    [Fact]
    public async Task AnOutsider_AnotherWall_AndAnUnknownBoulder_AllAnswerABare404()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedAsync(h);
        await AddAttemptsAsync(h, new Attempt { BoulderId = boulder, UserId = h.Owner.Id, Type = AttemptType.Send });

        Assert.IsType<NotFoundResult>(await Controller(h).GetAttempts(Guid.NewGuid(), boulder));
        Assert.IsType<NotFoundResult>(await Controller(h).GetAttempts(h.WallId, Guid.NewGuid()));

        await using (var db = h.CreateContext())
        {
            var outsider = new User { Identifier = "outsider@test", DisplayName = "Outsider" };
            db.Users.Add(outsider);
            await db.SaveChangesAsync();
            h.ActingUser = outsider;
        }

        Assert.IsType<NotFoundResult>(await Controller(h).GetAttempts(h.WallId, boulder));
    }

    private static List<BoulderAscentResponse> Rows(IActionResult result) =>
        Assert.IsType<List<BoulderAscentResponse>>(Assert.IsType<OkObjectResult>(result).Value);

    private static async Task AddAttemptsAsync(WallTestHarness h, params Attempt[] rows)
    {
        await using var db = h.CreateContext();
        db.Attempts.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static WallBoulderAttemptsController Controller(WallTestHarness h) =>
        new(h.BoulderService, h.AttemptService, h.CurrentUser)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = DeviceKeyTestPrincipals.Device() },
            },
        };

    private static async Task<Guid> SeedAsync(WallTestHarness h)
    {
        await h.SeedWallAsync(holdCount: 0);
        return await VisitFixture.SeedBoulderAsync(h, h.WallId);
    }
}
