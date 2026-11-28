// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.DeviceWrites;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>POST/PUT /api/v1/walls/{w}/boulders</c> for the companion app: the web setter's service calls under the web's
/// rules (members set, the creator or an admin revises, holds on the wall), once per <c>clientRequestId</c>, with the
/// feed rows other devices update from. Who may call at all is <see cref="DeviceApiRoutePostureTests"/>.
/// </summary>
public sealed class DeviceBouldersControllerTests
{
    [Fact]
    public async Task AMember_CreatesABoulder_Once_AndTheFeedAnnouncesIt()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
        var body = Create(holds, Guid.NewGuid());

        var first = Applied(await FeedController(h).Create(h.WallId, body));
        var replay = Applied(await FeedController(h).Create(h.WallId, body));

        await using var db = h.CreateContext();
        var boulder = Assert.Single(await db.Boulders.Include(b => b.BoulderHolds).ToListAsync());
        Assert.Equal(boulder.Id, BoulderId(first));
        Assert.Equal(boulder.Id, BoulderId(replay));
        Assert.Equal(("Crimp line", "6B+", h.ActingUser.Id, 2), (boulder.Name, boulder.Grade, boulder.CreatedByUserId, boulder.BoulderHolds.Count));
        Assert.True(await db.WallChanges.AnyAsync(c => c.WallId == h.WallId && c.BoulderId == boulder.Id && c.Kind == FeedKinds.BoulderChanged));
    }

    [Fact]
    public async Task AHoldOfAnotherWall_OrAnUnknownHold_IsAPermanent400()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var foreign = await ForeignHoldAsync(h);

        Refused(await Controller(h).Create(h.WallId, Create([holds[0], foreign], null)), 400, BoulderService.HoldOtherWallMessage);
        var unknown = new Hold { Id = Guid.NewGuid(), WallId = h.WallId };
        Refused(await Controller(h).Create(h.WallId, Create([holds[0], unknown], null)), 400, BoulderService.HoldOtherWallMessage);

        await using var db = h.CreateContext();
        Assert.Empty(await db.Boulders.ToListAsync());
    }

    [Fact]
    public async Task ABadBody_IsAPermanent400_AndAnOutsiderGets404()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var controller = Controller(h);

        Refused(await controller.Create(h.WallId, With(Create(holds, null), r => r.Grade = "6Z")), 400);
        Refused(await controller.Create(h.WallId, With(Create(holds, null), r => r.Styles = 1 << 30)), 400);
        Refused(await controller.Create(h.WallId, With(Create(holds, null), r => r.Name = " ")), 400);
        Refused(await controller.Create(h.WallId, With(Create(holds, null), r => r.CustomRule = new string('x', 201))), 400);
        Refused(await controller.Create(h.WallId, With(Create(holds, null), r => r.Holds = [])), 400);
        Refused(await controller.Create(h.WallId, With(Create(holds, null), r => r.WallId = Guid.NewGuid())), 400);
        Refused(await controller.Create(h.WallId, Create(holds, Guid.Empty)), 400);

        h.ActingUser = await OutsiderAsync(h);
        Refused(await Controller(h).Create(h.WallId, Create(holds, null)), 404);
    }

    [Fact]
    public async Task TheCreator_Revises_AnotherMemberCannot_AndAnOldRetryNeverRollsBack()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var id = BoulderId(Applied(await Controller(h).Create(h.WallId, Create(holds, null))));
        var firstRevise = new ReviseBoulderRequest { ClientRequestId = Guid.NewGuid(), Name = "Renamed", Grade = "7A", Holds = null };

        Applied(await Controller(h).Revise(h.WallId, id, firstRevise));
        Applied(await Controller(h).Revise(h.WallId, id, new ReviseBoulderRequest { Name = "Newer", Grade = "7A", Holds = null }));
        Applied(await Controller(h).Revise(h.WallId, id, firstRevise)); // the queue's retry of the first one
        await using (var db = h.CreateContext())
        {
            Assert.Equal("Newer", (await db.Boulders.SingleAsync(b => b.Id == id)).Name);
        }

        Refused(await Controller(h).Revise(Guid.NewGuid(), id, new ReviseBoulderRequest { Name = "x", Holds = null }), 404);
        Refused(await Controller(h).Revise(h.WallId, id, new ReviseBoulderRequest { HoldTypes = ["Nope"], Holds = null }), 400);
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
        Refused(await Controller(h).Revise(h.WallId, id, new ReviseBoulderRequest { Name = "Mine now", Holds = null }), 403);
    }

    [Fact]
    public async Task AMember_CreatesAVariant_OfABoulderOnTheWall()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var parent = BoulderId(Applied(await Controller(h).Create(h.WallId, Create(holds, null))));

        var variant = BoulderId(Applied(await Controller(h).Create(h.WallId, With(Create(holds, null), r => r.VariantOfBoulderId = parent))));

        await using var db = h.CreateContext();
        Assert.NotEqual(parent, variant);
        Assert.Equal(2, await db.Boulders.CountAsync(b => b.WallId == h.WallId));
    }

    private static CreateBoulderRequest Create(IReadOnlyList<Hold> holds, Guid? key) => new()
    {
        ClientRequestId = key,
        Name = "Crimp line",
        Grade = "6B+",
        Holds = holds.Take(2).Select(hold => new BoulderHoldDto { HoldId = hold.Id }).ToList(),
    };

    private static CreateBoulderRequest With(CreateBoulderRequest request, Action<CreateBoulderRequest> change)
    {
        change(request);
        return request;
    }

    private static async Task<Hold> ForeignHoldAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var wall = new Wall { Name = "Other", OwnerId = h.Owner.Id };
        var hold = new Hold { WallId = wall.Id, X = 0.5, Y = 0.5, Radius = 0.02 };
        db.Walls.Add(wall);
        db.Holds.Add(hold);
        await db.SaveChangesAsync();
        return hold;
    }

    private static async Task<User> OutsiderAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var user = new User { Identifier = "outsider@test", DisplayName = "Outsider" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static Guid BoulderId(OfflineActionResponse body) =>
        (Guid)body.Result!.GetType().GetProperty("boulderId")!.GetValue(body.Result)!;

    private static OfflineActionResponse Applied(IActionResult result)
    {
        var body = Assert.IsType<OfflineActionResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.True(body.Applied);
        return body;
    }

    private static void Refused(IActionResult result, int status, string? message = null)
    {
        var refused = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, refused.StatusCode);
        var error = Assert.IsType<OfflineActionError>(refused.Value);
        Assert.True(error.Permanent);
        if (message is not null)
        {
            Assert.Equal(message, error.Message);
        }
    }

    private static DeviceBouldersController Controller(WallTestHarness h) => Controller(h, h.BoulderService);

    // The production feed interceptor on the service's contexts, so the save writes its feed rows as it does live.
    private static DeviceBouldersController FeedController(WallTestHarness h)
    {
        var feed = new FeedInterceptor(
            new FeedMapperRegistry(FeedMappers.Create()), new RecordingFeedSignal(), NullLogger<FeedInterceptor>.Instance, TestFeedCommits.Unobserved());
        var factory = new InterceptedDbContextFactory(h.DbContextFactory.ConnectionString, [feed]);
        return Controller(h, new BoulderService(factory, h.CurrentUser, h.ActivityLog, NullLogger<BoulderService>.Instance));
    }

    private static DeviceBouldersController Controller(WallTestHarness h, IBoulderService service) =>
        new(service, h.CurrentUser, new DeviceWriteReceiptService(h.DbContextFactory))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = DeviceKeyTestPrincipals.Device() },
            },
        };
}
