// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.StructureFeedKit;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The wall-structure mappers (PR 1.2b) through a real save: the wall, its members, its structural parts and its holds,
/// each to its kind, audience and ids-only payload (design §5.4).
/// </summary>
public sealed class FeedStructureMapperTests
{
    [Fact]
    public async Task AWallEdit_WritesWallUpdated_WithTheLiveGeneration()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: 0, generation: 3);

        await using (var db = Context(harness))
        {
            (await db.Walls.SingleAsync()).Name = "Renamed";
            await db.SaveChangesAsync();
        }

        var row = Assert.Single(await RowsAsync(harness));
        Assert.Equal((FeedKinds.WallUpdated, harness.WallId, harness.WallId, FeedAudience.Wall), (row.Kind, row.WallId, row.EntityId, row.Audience));
        Assert.Equal(3, Payload(row).GetProperty("generation").GetInt32());
    }

    [Fact]
    public async Task AStagedPhotoOnlySave_OfTheWallOrAPanel_WritesNothing()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: 0);
        await using (var seed = harness.CreateContext())
        {
            seed.WallPanels.Add(new WallPanel { WallId = harness.WallId });
            await seed.SaveChangesAsync();
        }

        await using (var db = Context(harness))
        {
            var wall = await db.Walls.SingleAsync();
            (wall.StagedPhoto, wall.StagedAt) = ([9], DateTimeOffset.UtcNow);
            wall.LinkedHoldSyncVersion = 4;
            (await db.WallPanels.SingleAsync()).StagedPhoto = [9];
            await db.SaveChangesAsync();
        }

        Assert.Empty(await RowsAsync(harness));
        await using var read = harness.CreateContext();
        Assert.NotNull((await read.Walls.SingleAsync()).StagedPhotoSha256); // the hash stamp ran in that save
        Assert.NotNull((await read.WallPanels.SingleAsync()).StagedPhotoSha256);
    }

    [Fact]
    public async Task AMember_WritesMemberChanged_ForMembers_AndARemovalRevokesTheUsersAccess()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: 0);
        var user = new User { Identifier = "climber@test", DisplayName = "Climber" };
        await using (var seed = harness.CreateContext())
        {
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
        }

        await using (var db = Context(harness))
        {
            db.WallMembers.Add(new WallMember { WallId = harness.WallId, UserId = user.Id, Role = WallRole.Moderator });
            await db.SaveChangesAsync();
            db.WallMembers.Remove(await db.WallMembers.SingleAsync(m => m.UserId == user.Id));
            await db.SaveChangesAsync();
        }

        var rows = await RowsAsync(harness);
        Assert.Equal([FeedKinds.MemberChanged, FeedKinds.MemberChanged, FeedKinds.AccessRevoked], rows.Select(r => r.Kind));
        Assert.All(rows.Take(2), r => Assert.Equal((FeedAudience.Members, user.Id), (r.Audience, r.EntityId)));
        Assert.Equal("Moderator", Payload(rows[0]).GetProperty("role").GetString());
        var revoked = rows[2];
        Assert.Equal((FeedAudience.User, user.Id, harness.WallId, harness.WallId), (revoked.Audience, revoked.AudienceUserId, revoked.WallId, revoked.EntityId));
        Assert.Null(revoked.DataJson);
    }

    [Theory]
    [InlineData(WallRole.Admin, WallRole.Member, true)]
    [InlineData(WallRole.Moderator, WallRole.Member, true)]
    [InlineData(WallRole.Admin, WallRole.Moderator, true)]
    [InlineData(WallRole.Member, WallRole.Moderator, false)]
    public async Task ADemotion_RevokesTheUsersAccess_WithTheNewRole_AndAPromotionDoesNot(WallRole from, WallRole to, bool revokes)
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: 0);
        var user = await harness.AddMemberAsync("setter@test", from);

        await using (var db = Context(harness))
        {
            (await db.WallMembers.SingleAsync(m => m.UserId == user.Id)).Role = to;
            await db.SaveChangesAsync();
        }

        var rows = await RowsAsync(harness);
        Assert.Equal(to.ToString(), Payload(rows[0]).GetProperty("role").GetString());
        var revoked = rows.SingleOrDefault(r => r.Kind == FeedKinds.AccessRevoked);
        Assert.Equal(revokes, revoked is not null);
        if (revoked is not null)
        {
            Assert.Equal((FeedAudience.User, user.Id, harness.WallId), (revoked.Audience, revoked.AudienceUserId, revoked.WallId));
            Assert.Equal(to.ToString(), Payload(revoked).GetProperty("role").GetString());
        }
    }

    [Fact]
    public async Task StructuralParts_WriteTheirKinds_AndOnlyAPanelCarriesAGeneration()
    {
        using var harness = new WallTestHarness();
        var holds = await harness.SeedWallAsync(holdCount: 2);
        var model = new WallGeometryModel { WallId = harness.WallId, Json = "{}", Source = "test" };
        await using (var seed = harness.CreateContext())
        {
            seed.WallGeometryModels.Add(model);
            await seed.SaveChangesAsync();
        }

        var segment = new WallSegment { WallId = harness.WallId, Name = "Slab" };
        var panel = new WallPanel { WallId = harness.WallId, Generation = 2 };
        var link = new HoldLink { WallId = harness.WallId, HoldAId = holds[0].Id, HoldBId = holds[1].Id };
        var volume = new WallVolume { WallId = harness.WallId, GeometryModelId = model.Id, FacetId = "f0", FootprintJson = "[]", SurfaceJson = "{}" };
        await using (var db = Context(harness))
        {
            db.AddRange(segment, panel, link, volume);
            await db.SaveChangesAsync();
        }

        var rows = (await RowsAsync(harness)).ToDictionary(r => r.Kind);
        Assert.Equal(segment.Id, rows[FeedKinds.SegmentChanged].EntityId);
        Assert.Equal(link.Id, rows[FeedKinds.LinkChanged].EntityId);
        Assert.Equal(volume.Id, rows[FeedKinds.VolumeChanged].EntityId);
        Assert.Equal(2, Payload(rows[FeedKinds.PanelChanged]).GetProperty("generation").GetInt32());
        Assert.All(new[] { FeedKinds.SegmentChanged, FeedKinds.LinkChanged, FeedKinds.VolumeChanged }, k => Assert.Null(rows[k].DataJson));
        Assert.All(rows.Values, r => Assert.Equal((harness.WallId, FeedAudience.Wall), (r.WallId!.Value, r.Audience)));
    }

    [Fact]
    public async Task AHold_WritesChangedOrDeleted_WithItsGenerationAndPanel()
    {
        using var harness = new WallTestHarness();
        var holds = await harness.SeedWallAsync(holdCount: 2, generation: 1);

        await using (var db = Context(harness))
        {
            (await db.Holds.SingleAsync(h => h.Id == holds[0].Id)).X = 0.5;
            await db.SaveChangesAsync();
            db.Holds.Remove(await db.Holds.SingleAsync(h => h.Id == holds[1].Id));
            await db.SaveChangesAsync();
        }

        var rows = await RowsAsync(harness);
        Assert.Equal([(FeedKinds.HoldChanged, holds[0].Id), (FeedKinds.HoldDeleted, holds[1].Id)], rows.Select(r => (r.Kind, r.EntityId!.Value)));
        Assert.All(rows, r => Assert.Equal(1, Payload(r).GetProperty("generation").GetInt32()));
        Assert.All(rows, r => Assert.False(Payload(r).TryGetProperty("panelId", out _))); // null is omitted
    }

    [Fact]
    public async Task AHoldRestampedOntoAnotherGeneration_AlsoReachesTheSetItLeft()
    {
        using var harness = new WallTestHarness();
        var hold = (await harness.SeedWallAsync(holdCount: 1, generation: 1))[0];

        await using (var db = Context(harness))
        {
            (await db.Holds.SingleAsync()).Generation = 2;
            await db.SaveChangesAsync();
        }

        var rows = await RowsAsync(harness);
        Assert.Equal([2, 1], rows.Select(r => Payload(r).GetProperty("generation").GetInt32()));
        Assert.All(rows, r => Assert.Equal((FeedKinds.HoldChanged, hold.Id), (r.Kind, r.EntityId!.Value)));
    }
}
