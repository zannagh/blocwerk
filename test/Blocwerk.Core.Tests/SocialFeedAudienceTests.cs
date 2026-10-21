// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The social rows end to end, from a save through the interceptor and the hub to each kind of viewer: "mine" rows reach
/// only their own user (never another member, a setter, a share viewer or a kiosk), stats name nobody, a draft's rows
/// reach setters only, and members-only rows skip share and kiosk viewers.
/// </summary>
public sealed class SocialFeedAudienceTests : IAsyncLifetime
{
    private readonly InProcessFeedSignal signal = new();
    private readonly SocialFeedFixture fixture;
    private readonly WallFeedHub hub;

    public SocialFeedAudienceTests()
    {
        fixture = new SocialFeedFixture(signal: signal);
        var connectionString = fixture.Harness.DbContextFactory.ConnectionString;
        var feed = new WallFeed(() => TestDb.Create(connectionString), NullLogger<WallFeed>.Instance);
        hub = new WallFeedHub(feed, signal, () => TestDb.Create(connectionString), NullLogger<WallFeedHub>.Instance);
    }

    public async Task InitializeAsync() => await hub.StartAsync(CancellationToken.None);

    public async Task DisposeAsync()
    {
        await hub.StopAsync(CancellationToken.None);
        hub.Dispose();
        fixture.Dispose();
    }

    [Fact]
    public async Task EachViewer_GetsExactlyTheSocialRowsTheDesignAllowsIt()
    {
        var boulder = await fixture.SeedAsync();
        var draft = await fixture.AddBoulderAsync(isDraft: true);
        var other = await fixture.Harness.AddMemberAsync("other@test", WallRole.Member);
        var setter = await fixture.Harness.AddMemberAsync("setter@test", WallRole.Admin);
        var me = fixture.OwnerId;
        var anonymous = await SubscribeAsync(FeedViewer.Anonymous());
        var kiosk = await SubscribeAsync(FeedViewer.Kiosk());
        var asOther = await SubscribeAsync(FeedViewer.Member(other.Id));
        var asSetter = await SubscribeAsync(FeedViewer.Setter(setter.Id));
        var asMe = await SubscribeAsync(FeedViewer.Member(me));

        await fixture.AddAsync(new Attempt { BoulderId = boulder, UserId = me, Type = AttemptType.Send }, me);
        await fixture.AddAsync(new BoulderFavorite { BoulderId = boulder, UserId = me }, me);
        await fixture.AddAsync(new BoulderRating { BoulderId = boulder, UserId = me, Stars = 5 }, me);
        await fixture.AddAsync(new BoulderComment { BoulderId = draft, UserId = me, Text = "draft beta" }, me);
        await fixture.AddAsync(new ActivityLogEntry { WallId = fixture.WallId, UserId = me, Type = ActivityType.HoldAdded }, me);
        await fixture.ChangeAsync<User>(u => u.Id == me, (u, _) => u.CustomDisplayName = "Me Again", me);

        string[] stats = [FeedKinds.BoulderStats, FeedKinds.BoulderStats];
        var mine = await asMe.WaitForEventsAsync(7);
        Assert.Equal(
            [FeedKinds.BoulderStats, FeedKinds.TickMine, FeedKinds.FavoriteChanged, FeedKinds.BoulderStats, FeedKinds.RatingMine,
             FeedKinds.ActivityAdded, FeedKinds.UserProfile],
            mine.Select(e => e.Kind));
        Assert.Equal(stats, anonymous.Events.Select(e => e.Kind));
        Assert.Equal(stats, kiosk.Events.Select(e => e.Kind));
        Assert.Equal([.. stats, FeedKinds.ActivityAdded, FeedKinds.UserProfile], asOther.Events.Select(e => e.Kind));
        Assert.Equal(
            [.. stats, FeedKinds.CommentAdded, FeedKinds.ActivityAdded, FeedKinds.UserProfile],
            asSetter.Events.Select(e => e.Kind));
        Assert.All(anonymous.Events, e => Assert.Null(e.Actor));
    }

    [Fact]
    public async Task ACommentHydrates_WithItsTextAndAuthor_AndAProfileRowWithTheNewName()
    {
        var boulder = await fixture.SeedAsync();
        var viewer = await SubscribeAsync(FeedViewer.Member(Guid.NewGuid()));
        var outsider = await SubscribeAsync(FeedViewer.Anonymous());

        await fixture.AddAsync(new BoulderComment { BoulderId = boulder, UserId = fixture.OwnerId, Text = "Heel left" });
        var comment = Assert.Single(await viewer.WaitForEventsAsync(1));
        Assert.Equal((FeedKinds.CommentAdded, "Heel left", false), (comment.Kind, comment.Hydrated!.Text, comment.Tombstone));
        Assert.Equal(((Guid?)fixture.OwnerId, "Owner"), (comment.Actor!.Id, comment.Actor.Name));

        // A share or kiosk viewer sees the byline, but not the id of an author without an avatar (AuthorVisibility).
        var seen = Assert.Single(await outsider.WaitForEventsAsync(1));
        Assert.Equal((FeedKinds.CommentAdded, "Owner", (Guid?)null), (seen.Kind, seen.Actor!.Name, seen.Actor.Id));

        // Names are read at delivery, so the profile row carries the new one.
        await fixture.ChangeAsync<User>(u => u.Id == fixture.OwnerId, (u, _) => u.CustomDisplayName = "Renamed");
        var profile = (await viewer.WaitForEventsAsync(2))[1];
        Assert.Equal((FeedKinds.UserProfile, "Renamed"), (profile.Kind, profile.Actor!.Name));
    }

    [Fact]
    public async Task AnOutsider_SeesTheIdOfAnAuthorWithAnAvatar_OnCommentsAndVideos_ButAMemberAlways()
    {
        var boulder = await fixture.SeedAsync();
        var plain = await fixture.Harness.AddMemberAsync("plain@test", WallRole.Member);
        await fixture.ChangeAsync<User>(u => u.Id == fixture.OwnerId, (u, _) => (u.AvatarImage, u.AvatarContentType) = ([1, 2], "image/png"));
        var outsider = await SubscribeAsync(FeedViewer.Anonymous());
        var member = await SubscribeAsync(FeedViewer.Member(plain.Id));

        await fixture.AddAsync(new BoulderComment { BoulderId = boulder, UserId = fixture.OwnerId, Text = "with avatar" });
        await fixture.AddAsync(new BetaVideo { BoulderId = boulder, UploadedByUserId = fixture.OwnerId, ContentType = "video/mp4" });
        await fixture.AddAsync(new BoulderComment { BoulderId = boulder, UserId = plain.Id, Text = "without" });

        var seen = await outsider.WaitForEventsAsync(3);
        Assert.Equal([FeedKinds.CommentAdded, FeedKinds.VideoAdded, FeedKinds.CommentAdded], seen.Select(e => e.Kind));
        Assert.Equal([(Guid?)fixture.OwnerId, fixture.OwnerId, null], seen.Select(e => e.Actor!.Id));
        Assert.Equal([true, true, false], seen.Select(e => e.Actor!.HasAvatar));
        var known = await member.WaitForEventsAsync(3);
        Assert.Equal([(Guid?)fixture.OwnerId, fixture.OwnerId, plain.Id], known.Select(e => e.Actor!.Id));
    }

    private async Task<RecordingFeedSubscriber> SubscribeAsync(FeedViewer viewer)
    {
        var subscriber = new RecordingFeedSubscriber();
        await hub.SubscribeAsync(fixture.WallId, viewer, subscriber);
        return subscriber;
    }
}
