// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The social mappers (PR 1.2c, design §5.4) through the real interceptor: kind, audience, entity, actor and the ids-only
/// payload of each row, with the wall resolved from the boulder the saving context never loaded.
/// </summary>
public sealed class SocialFeedMapperTests : IDisposable
{
    private readonly SocialFeedFixture fixture = new();

    [Fact]
    public async Task AComment_IsCommentAdded_CreditedToItsAuthor_WithTheBoulderOnly()
    {
        var boulder = await fixture.SeedAsync();
        var comment = new BoulderComment { BoulderId = boulder, UserId = fixture.OwnerId, Text = "Secret beta" };

        await fixture.AddAsync(comment, actor: Guid.NewGuid());

        var row = Assert.Single(await fixture.RowsAsync());
        Assert.Equal((FeedKinds.CommentAdded, fixture.WallId, boulder, comment.Id), (row.Kind, row.WallId, row.BoulderId, row.EntityId));
        Assert.Equal((FeedAudience.Wall, fixture.OwnerId), (row.Audience, row.ActorUserId));
        Assert.Equal(["boulderId", "sv"], Properties(row));
        Assert.DoesNotContain("Secret", row.DataJson);
    }

    [Fact]
    public async Task AVideo_IsAddedThenStatusThenDeleted_AndBookkeepingAloneMapsToNothing()
    {
        var boulder = await fixture.SeedAsync();
        var video = new BetaVideo { BoulderId = boulder, UploadedByUserId = fixture.OwnerId, ContentType = "video/mp4" };
        await fixture.AddAsync(video);
        await fixture.ChangeAsync<BetaVideo>(v => v.Id == video.Id, (v, _) => v.EncodingError = "retrying");
        await fixture.ChangeAsync<BetaVideo>(v => v.Id == video.Id, (v, _) => (v.EncodingStatus, v.HasHls) = (BetaVideoEncodingStatus.Ready, true));
        await fixture.ChangeAsync<BetaVideo>(v => v.Id == video.Id, (v, db) => db.Remove(v));

        var rows = await fixture.RowsAsync();
        Assert.Equal([FeedKinds.VideoAdded, FeedKinds.VideoStatus, FeedKinds.VideoDeleted], rows.Select(r => r.Kind));
        Assert.All(rows, r => Assert.Equal((fixture.WallId, video.Id, FeedAudience.Wall), (r.WallId, r.EntityId, r.Audience)));
        Assert.Equal(fixture.OwnerId, rows[0].ActorUserId);
        Assert.Equal(["boulderId", "encodingStatus", "hasHls", "sv"], Properties(rows[1]));
        Assert.Equal("Ready", Data(rows[1]).GetProperty("encodingStatus").GetString());
        Assert.True(Data(rows[1]).GetProperty("hasHls").GetBoolean());
    }

    [Fact]
    public async Task Ticks_WriteOneActorlessStatsRowPerBoulder_AndTheTickersOwnRow()
    {
        var boulder = await fixture.SeedAsync();
        await using (var db = fixture.Context(fixture.OwnerId))
        {
            db.Attempts.Add(new Attempt { BoulderId = boulder, UserId = fixture.OwnerId, Type = AttemptType.Attempt, Notes = "knee bar" });
            db.Attempts.Add(new Attempt { BoulderId = boulder, UserId = fixture.OwnerId, Type = AttemptType.Send });
            await db.SaveChangesAsync();
        }

        var rows = await fixture.RowsAsync();
        Assert.Equal(2, rows.Count);
        var stats = Assert.Single(rows, r => r.Kind == FeedKinds.BoulderStats);
        Assert.Equal((fixture.WallId, boulder, FeedAudience.Wall, (Guid?)null, (string?)null), (stats.WallId, stats.EntityId, stats.Audience, stats.ActorUserId, stats.DataJson));
        var mine = Assert.Single(rows, r => r.Kind == FeedKinds.TickMine);
        Assert.Equal((fixture.WallId, boulder, FeedAudience.User, fixture.OwnerId), (mine.WallId, mine.EntityId, mine.Audience, mine.AudienceUserId));
        Assert.Null(mine.DataJson);
    }

    [Fact]
    public async Task MovingATickBetweenActivities_MapsToNothing()
    {
        var boulder = await fixture.SeedAsync();
        var attempt = new Attempt { BoulderId = boulder, UserId = fixture.OwnerId, Type = AttemptType.Send };
        await fixture.AddAsync(attempt);
        await fixture.ClearRowsAsync();

        var activity = new Activity { UserId = fixture.OwnerId, WallId = fixture.WallId, StartedAt = attempt.Timestamp, LastEventAt = attempt.Timestamp };
        await using (var db = fixture.Harness.CreateContext())
        {
            db.Activities.Add(activity);
            await db.SaveChangesAsync();
        }

        await fixture.ChangeAsync<Attempt>(a => a.Id == attempt.Id, (a, _) => a.ActivityId = activity.Id);
        Assert.Empty(await fixture.RowsAsync());
        await fixture.ChangeAsync<Attempt>(a => a.Id == attempt.Id, (a, _) => a.Notes = "crimpy");

        Assert.Equal([FeedKinds.BoulderStats, FeedKinds.TickMine], (await fixture.RowsAsync()).Select(r => r.Kind));
    }

    [Fact]
    public async Task ARating_IsStatsAndRatingMine_WithoutTheStars_AndAGradeProposalIsStatsOnly()
    {
        var boulder = await fixture.SeedAsync();
        await fixture.AddAsync(new BoulderRating { BoulderId = boulder, UserId = fixture.OwnerId, Stars = 4 }, fixture.OwnerId);
        await fixture.AddAsync(new GradeProposal { BoulderId = boulder, ProposedByUserId = fixture.OwnerId, ProposedGrade = "7a" }, fixture.OwnerId);

        var rows = await fixture.RowsAsync();
        Assert.Equal([FeedKinds.BoulderStats, FeedKinds.RatingMine, FeedKinds.BoulderStats], rows.Select(r => r.Kind));
        Assert.All(rows, r => Assert.Null(r.DataJson));
        Assert.All(rows.Where(r => r.Kind == FeedKinds.BoulderStats), r => Assert.Null(r.ActorUserId));
        Assert.Equal(fixture.OwnerId, rows[1].AudienceUserId);
    }

    [Fact]
    public async Task AFavourite_IsOnlyTheUsersOwnRow()
    {
        var boulder = await fixture.SeedAsync();
        await fixture.AddAsync(new BoulderFavorite { BoulderId = boulder, UserId = fixture.OwnerId });
        await fixture.ChangeAsync<BoulderFavorite>(f => f.BoulderId == boulder, (f, db) => db.Remove(f));

        var rows = await fixture.RowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(
            (FeedKinds.FavoriteChanged, FeedAudience.User, (Guid?)fixture.OwnerId, (Guid?)fixture.WallId),
            (r.Kind, r.Audience, r.AudienceUserId, r.WallId)));
    }

    [Fact]
    public async Task AGalleryImage_IsChangedThenDeleted()
    {
        await fixture.SeedAsync();
        var image = new WallImage { WallId = fixture.WallId, StoragePath = "a.jpg", ContentType = "image/jpeg" };
        await fixture.AddAsync(image);
        await fixture.ChangeAsync<WallImage>(i => i.Id == image.Id, (i, _) => i.Caption = "Overhang");
        await fixture.ChangeAsync<WallImage>(i => i.Id == image.Id, (i, db) => db.Remove(i));

        var rows = await fixture.RowsAsync();
        Assert.Equal([FeedKinds.GalleryChanged, FeedKinds.GalleryChanged, FeedKinds.GalleryDeleted], rows.Select(r => r.Kind));
        Assert.All(rows, r => Assert.Equal((image.Id, FeedAudience.Wall, (string?)null), (r.EntityId, r.Audience, r.DataJson)));
    }

    [Fact]
    public async Task AnActivityEntry_IsForMembers_CreditedToItsUser_WithTheTypeOnly()
    {
        var boulder = await fixture.SeedAsync();
        var entry = new ActivityLogEntry
        {
            WallId = fixture.WallId, BoulderId = boulder, UserId = fixture.OwnerId, Type = ActivityType.AttemptLogged, Details = "Send",
        };
        await fixture.AddAsync(entry);
        await fixture.AddAsync(new ActivityLogEntry { WallId = fixture.WallId, UserId = fixture.OwnerId, Type = ActivityType.HoldAdded });

        var rows = await fixture.RowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal((FeedKinds.ActivityAdded, FeedAudience.Members, (Guid?)fixture.OwnerId, (Guid?)fixture.WallId), (r.Kind, r.Audience, r.ActorUserId, r.WallId)));
        Assert.Equal((entry.Id, (Guid?)boulder), (rows[0].EntityId, rows[0].BoulderId));
        Assert.Equal(["sv", "type"], Properties(rows[0]));
        Assert.Equal(nameof(ActivityType.AttemptLogged), Data(rows[0]).GetProperty("type").GetString());
    }

    [Fact]
    public async Task ADraftBouldersComment_VideoAndActivity_ReachSettersOnly_ButTheTickersOwnRowStaysTheirs()
    {
        await fixture.SeedAsync();
        var draft = await fixture.AddBoulderAsync(isDraft: true);
        await fixture.AddAsync(new BoulderComment { BoulderId = draft, UserId = fixture.OwnerId, Text = "wip" });
        await fixture.AddAsync(new BetaVideo { BoulderId = draft, UploadedByUserId = fixture.OwnerId, ContentType = "video/mp4" });
        await fixture.AddAsync(new ActivityLogEntry { WallId = fixture.WallId, BoulderId = draft, UserId = fixture.OwnerId, Type = ActivityType.BoulderCreated });
        await fixture.AddAsync(new BoulderFavorite { BoulderId = draft, UserId = fixture.OwnerId });

        var rows = await fixture.RowsAsync();
        Assert.Equal(
            [FeedAudience.Setters, FeedAudience.Setters, FeedAudience.Setters, FeedAudience.User],
            rows.Select(r => r.Audience));
        Assert.All(rows, r => Assert.Equal(fixture.WallId, r.WallId));
    }

    public void Dispose() => fixture.Dispose();

    private static JsonElement Data(WallChange row) => JsonSerializer.Deserialize<JsonElement>(row.DataJson!);

    private static List<string> Properties(WallChange row) => Data(row).EnumerateObject().Select(p => p.Name).Order().ToList();
}
