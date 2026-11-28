// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.DeviceSetterFixture;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>POST /api/v1/walls/{w}/boulders</c> for the companion app: the web setter's create under the web's rules (members
/// set, holds on the wall, a variant on its parent's wall), once per required <c>clientRequestId</c>, with the feed rows
/// other devices update from. Revise is <see cref="DeviceBoulderReviseTests"/>; who may call at all is
/// <see cref="DeviceApiRoutePostureTests"/>.
/// </summary>
public sealed class DeviceBouldersControllerTests
{
    [Fact]
    public async Task AMember_CreatesABoulder_Once_AndTheFeedAnnouncesIt()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
        var body = Create(holds);

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
        var (_, foreign) = await OtherWallAsync(h, h.Owner, ownerIsMember: true);
        var unknown = new Hold { Id = Guid.NewGuid(), WallId = h.WallId };

        Refused(await Controller(h).Create(h.WallId, Create([holds[0], foreign])), 400, BoulderService.HoldOtherWallMessage);
        Refused(await Controller(h).Create(h.WallId, Create([holds[0], unknown])), 400, BoulderService.HoldOtherWallMessage);

        await using var db = h.CreateContext();
        Assert.Empty(await db.Boulders.ToListAsync());
    }

    [Fact]
    public async Task ABadBody_IsAPermanent400_AndAnOutsiderGets404()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var c = Controller(h);
        var many = Enumerable.Range(0, DeviceBoulderRequestCheck.MaxHolds + 1).Select(_ => new BoulderHoldDto { HoldId = Guid.NewGuid() }).ToList();

        Refused(await c.Create(h.WallId, Create(holds, change: r => r.Grade = "6Z")), 400);
        Refused(await c.Create(h.WallId, Create(holds, change: r => r.Styles = 1 << 30)), 400);
        Refused(await c.Create(h.WallId, Create(holds, change: r => r.Name = " ")), 400);
        Refused(await c.Create(h.WallId, Create(holds, change: r => r.CustomRule = new string('x', 201))), 400);
        Refused(await c.Create(h.WallId, Create(holds, change: r => r.FootColorOnly = new string('x', 33))), 400);
        Refused(await c.Create(h.WallId, Create(holds, change: r => r.Holds = [])), 400);
        Refused(await c.Create(h.WallId, Create(holds, change: r => r.Holds = many)), 400);
        Refused(await c.Create(h.WallId, Create(holds, change: r => r.Holds.Add(r.Holds[0]))), 400);
        Refused(await c.Create(h.WallId, Create(holds, change: r => r.Setters = Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToList())), 400);
        Refused(await c.Create(h.WallId, Create(holds, change: r => r.Setters = [h.Owner.Id, h.Owner.Id])), 400);
        Refused(await c.Create(h.WallId, Create(holds, change: r => r.WallId = Guid.NewGuid())), 400);
        Refused(await c.Create(h.WallId, Create(holds, change: r => r.ClientRequestId = null)), 400, DeviceBoulderRequestCheck.ClientRequestIdRequired);
        Refused(await c.Create(h.WallId, Create(holds, Guid.Empty)), 400);

        h.ActingUser = await OutsiderAsync(h);
        Refused(await Controller(h).Create(h.WallId, Create(holds)), 404);
    }

    [Fact]
    public async Task AVariant_OfABoulderOnTheWall_IsLinked_OneOnAWallYouCannotSee_IsLikeAMissingOrigin()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var parent = BoulderId(Applied(await Controller(h).Create(h.WallId, Create(holds))));
        var (hiddenWall, hiddenHold) = await OtherWallAsync(h, new User { Identifier = "stranger@test", DisplayName = "S" }, ownerIsMember: false);
        Guid hidden;
        await using (var db = h.CreateContext())
        {
            var b = new Boulder { WallId = hiddenWall, Name = "Hidden", CreatedByUserId = h.Owner.Id };
            db.Boulders.Add(b);
            await db.SaveChangesAsync();
            hidden = b.Id;
        }

        var variant = BoulderId(Applied(await Controller(h).Create(h.WallId, Create(holds, change: r => r.VariantOfBoulderId = parent))));
        var ofHidden = BoulderId(Applied(await Controller(h).Create(h.WallId, Create(holds, change: r => r.VariantOfBoulderId = hidden))));
        var ofMissing = BoulderId(Applied(await Controller(h).Create(h.WallId, Create(holds, change: r => r.VariantOfBoulderId = Guid.NewGuid()))));

        await using var check = h.CreateContext();
        Assert.Equal(parent, (await check.Boulders.SingleAsync(b => b.Id == variant)).ParentBoulderId);
        Assert.Null((await check.Boulders.SingleAsync(b => b.Id == ofHidden)).ParentBoulderId);
        Assert.Null((await check.Boulders.SingleAsync(b => b.Id == ofMissing)).ParentBoulderId);
        Assert.NotEqual(hiddenHold.WallId, h.WallId);
    }

    [Fact]
    public async Task AnIdOfYourOwnBoulderOnAnotherWall_IsAPermanent409_NotAReplay()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var (otherWall, otherHold) = await OtherWallAsync(h, h.Owner, ownerIsMember: true);
        var id = Guid.NewGuid();
        Applied(await Controller(h).Create(otherWall, Create([otherHold], change: r => r.Id = id)));

        Refused(await Controller(h).Create(h.WallId, Create(holds, change: r => r.Id = id)), 409, BoulderService.IdOnOtherWallMessage);
    }
}
