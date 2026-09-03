using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Deleting a boulder used to be the one boulder mutation with no authority check at all: the
/// method resolved the current user only to log them, then removed the row by id. <see cref="Boulder"/>
/// carries no query filter — only <see cref="Wall"/> does — so any signed-in account in the
/// installation could destroy any boulder on anybody's wall. These tests pin the rule the sibling
/// mutations already use: creator, any setter, or a wall admin.
/// </summary>
public class BoulderDeleteAuthorizationTests
{
    [Fact]
    public async Task TheCreatorCanDeleteTheirOwnBoulder()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var creator = await h.AddMemberAsync("creator@test", WallRole.Member);

        h.ActingUser = creator;
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Mine", null, [new BoulderHoldInput(holds[0].Id)]);

        await h.BoulderService.DeleteBoulderAsync(boulder.Id);

        await using var db = h.CreateContext();
        Assert.False(await db.Boulders.AnyAsync(b => b.Id == boulder.Id));
    }

    [Fact]
    public async Task ACreditedSetterCanDeleteABoulderTheyDidNotCreate()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var setter = await h.AddMemberAsync("setter@test", WallRole.Member);

        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Credited", null, [new BoulderHoldInput(holds[0].Id)], setterUserIds: [setter.Id]);

        h.ActingUser = setter;
        await h.BoulderService.DeleteBoulderAsync(boulder.Id);

        await using var db = h.CreateContext();
        Assert.False(await db.Boulders.AnyAsync(b => b.Id == boulder.Id));
    }

    [Fact]
    public async Task AWallAdminCanDeleteSomebodyElsesBoulder()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var creator = await h.AddMemberAsync("creator@test", WallRole.Member);
        var admin = await h.AddMemberAsync("admin@test", WallRole.Admin);

        h.ActingUser = creator;
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Theirs", null, [new BoulderHoldInput(holds[0].Id)]);

        h.ActingUser = admin;
        await h.BoulderService.DeleteBoulderAsync(boulder.Id);

        await using var db = h.CreateContext();
        Assert.False(await db.Boulders.AnyAsync(b => b.Id == boulder.Id));
    }

    /// <summary>
    /// An ordinary member of the same wall neither set the boulder nor administers the wall, so the
    /// rule refuses them — and the row is still there afterwards.
    /// </summary>
    [Fact]
    public async Task AnUnrelatedMemberOfTheSameWallCannotDelete()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Owner's", null, [new BoulderHoldInput(holds[0].Id)]);
        var stranger = await h.AddMemberAsync("stranger@test", WallRole.Member);

        h.ActingUser = stranger;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.BoulderService.DeleteBoulderAsync(boulder.Id));

        await using var db = h.CreateContext();
        Assert.True(await db.Boulders.AnyAsync(b => b.Id == boulder.Id));
    }

    /// <summary>
    /// The hole as it actually stood: a signed-in member of a DIFFERENT wall, who cannot even read
    /// the boulder through <c>GetBoulderAsync</c>, could still delete it by guessing its id. Being
    /// an admin of their own wall buys them nothing here.
    /// </summary>
    [Fact]
    public async Task AMemberOfAnotherWallCannotDelete()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Owner's", null, [new BoulderHoldInput(holds[0].Id)]);

        var outsider = new User { Identifier = "outsider@test", DisplayName = "Outsider" };
        var otherWall = new Wall
        {
            Name = "Other Wall",
            OwnerId = outsider.Id,
            Photo = [1, 2, 3],
            PhotoContentType = "image/jpeg",
        };

        await using (var seed = h.CreateContext())
        {
            seed.Users.Add(outsider);
            seed.Walls.Add(otherWall);
            seed.WallMembers.Add(new WallMember { WallId = otherWall.Id, UserId = outsider.Id, Role = WallRole.Admin });
            await seed.SaveChangesAsync();
        }

        h.ActingUser = outsider;

        // They cannot read it either way; the point is that deleting used to ignore that entirely.
        Assert.Null(await h.BoulderService.GetBoulderAsync(boulder.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.BoulderService.DeleteBoulderAsync(boulder.Id));

        await using var db = h.CreateContext();
        Assert.True(await db.Boulders.AnyAsync(b => b.Id == boulder.Id));
    }
}
