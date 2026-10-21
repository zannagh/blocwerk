// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;

namespace Blocwerk.Core.Tests;

/// <summary><c>user.profile</c> and <c>view3d.changed</c>: who is told, and which changes are invisible.</summary>
public sealed class SocialFeedProfileTests : IDisposable
{
    private readonly SocialFeedFixture fixture = new();

    [Fact]
    public async Task ANameChange_TellsTheUserAndTheMembersOfEachOfTheirWalls()
    {
        await fixture.SeedAsync();
        var otherWall = await AddOtherWallAsync();

        await fixture.ChangeAsync<User>(u => u.Id == fixture.OwnerId, (u, _) => u.CustomDisplayName = "Sam the Setter");

        var rows = await fixture.RowsAsync();
        Assert.All(rows, r => Assert.Equal((FeedKinds.UserProfile, (Guid?)fixture.OwnerId, (Guid?)fixture.OwnerId, (string?)null), (r.Kind, r.EntityId, r.ActorUserId, r.DataJson)));
        var self = Assert.Single(rows, r => r.Audience == FeedAudience.User);
        Assert.Equal(((Guid?)null, (Guid?)fixture.OwnerId), (self.WallId, self.AudienceUserId));
        var walls = rows.Where(r => r.Audience != FeedAudience.User).ToList();
        Assert.All(walls, r => Assert.Equal((FeedAudience.Members, (Guid?)null), (r.Audience, r.AudienceUserId)));
        Assert.Equal(new[] { fixture.WallId, otherWall }.Order(), walls.Select(r => r.WallId!.Value).Order());
    }

    [Fact]
    public async Task APreferenceChange_TellsOnlyTheUser_AndALoginTellsNobody()
    {
        await fixture.SeedAsync();

        await fixture.ChangeAsync<User>(u => u.Id == fixture.OwnerId, (u, _) => u.PreferFontGrades = !u.PreferFontGrades);
        await fixture.ChangeAsync<User>(u => u.Id == fixture.OwnerId, (u, _) => (u.FailedAuthCount, u.LockoutUntil) = (3, DateTimeOffset.UtcNow));

        var row = Assert.Single(await fixture.RowsAsync());
        Assert.Equal((FeedAudience.User, (Guid?)fixture.OwnerId, (Guid?)null), (row.Audience, row.AudienceUserId, row.WallId));
    }

    [Fact]
    public async Task OnlyTheActiveGeometryModel_ChangesThe3DView()
    {
        await fixture.SeedAsync();
        var model = new WallGeometryModel { WallId = fixture.WallId, Json = "{}", Source = "test", IsActive = false };
        await fixture.AddAsync(model);
        await fixture.ChangeAsync<WallGeometryModel>(m => m.Id == model.Id, (m, _) => m.Notes = "imported");
        Assert.Empty(await fixture.RowsAsync());

        await fixture.ChangeAsync<WallGeometryModel>(m => m.Id == model.Id, (m, _) => m.IsActive = true);
        await fixture.ChangeAsync<WallGeometryModel>(m => m.Id == model.Id, (m, _) => (m.IsActive, m.RetiredAt) = (false, DateTimeOffset.UtcNow));

        var all = await fixture.RowsAsync();
        Assert.Equal(2, all.Count(r => r.Kind == FeedKinds.WallManifest)); // activation and retirement move the manifest (PR 2.4)
        var rows = all.Where(r => r.Kind != FeedKinds.WallManifest).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal((FeedKinds.View3DChanged, (Guid?)fixture.WallId, (Guid?)model.Id, FeedAudience.Wall), (r.Kind, r.WallId, r.EntityId, r.Audience)));
    }

    [Fact]
    public async Task AnAudienceScope_NarrowsTheSocialRows_ButTheUsersOwnRowsKeepTheirAudience()
    {
        var boulder = await fixture.SeedAsync();
        using (FeedAudienceScope.Begin(FeedAudience.Setters))
        {
            await fixture.AddAsync(new Attempt { BoulderId = boulder, UserId = fixture.OwnerId, Type = AttemptType.Send }, fixture.OwnerId);
            await fixture.ChangeAsync<User>(u => u.Id == fixture.OwnerId, (u, _) => u.CustomDisplayName = "Narrowed");
        }

        var rows = await fixture.RowsAsync();
        Assert.Equal(
            [(FeedKinds.BoulderStats, FeedAudience.Setters), (FeedKinds.TickMine, FeedAudience.User),
             (FeedKinds.UserProfile, FeedAudience.User), (FeedKinds.UserProfile, FeedAudience.Setters)],
            rows.Select(r => (r.Kind, r.Audience)));
    }

    public void Dispose() => fixture.Dispose();

    private async Task<Guid> AddOtherWallAsync()
    {
        await using var db = fixture.Harness.CreateContext();
        var wall = new Wall { Name = "Other", OwnerId = fixture.OwnerId, Photo = [1], PhotoContentType = "image/jpeg" };
        db.Walls.Add(wall);
        await db.SaveChangesAsync();
        return wall.Id;
    }
}
