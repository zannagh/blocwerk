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

        // A sentinel every viewer receives: once each has it, everything before it has been delivered, so a missing
        // event below is really withheld, not late.
        var sentinel = new WallImage { WallId = fixture.WallId, StoragePath = "s.jpg", ContentType = "image/jpeg" };
        await fixture.AddAsync(sentinel);
        RecordingFeedSubscriber[] all = [anonymous, kiosk, asOther, asSetter, asMe];
        foreach (var viewer in all)
        {
            await UntilAsync(viewer, sentinel.Id);
        }

        string[] stats = [FeedKinds.BoulderStats, FeedKinds.BoulderStats];
        Assert.Equal(
            [FeedKinds.BoulderStats, FeedKinds.TickMine, FeedKinds.FavoriteChanged, FeedKinds.BoulderStats, FeedKinds.RatingMine,
             FeedKinds.ActivityAdded, FeedKinds.UserProfile],
            Before(asMe, sentinel.Id));
        Assert.Equal(stats, Before(anonymous, sentinel.Id));
        Assert.Equal(stats, Before(kiosk, sentinel.Id));
        Assert.Equal([.. stats, FeedKinds.ActivityAdded, FeedKinds.UserProfile], Before(asOther, sentinel.Id));
        Assert.Equal([.. stats, FeedKinds.CommentAdded, FeedKinds.ActivityAdded, FeedKinds.UserProfile], Before(asSetter, sentinel.Id));
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

    [Fact]
    public void AKioskViewer_CarriesNoUser_AndOnlyTheWallAudience()
    {
        var kiosk = FeedViewer.Kiosk();

        Assert.Null(kiosk.UserId);
        Assert.Equal(FeedAudience.Wall, Assert.Single(kiosk.Audiences));
        Assert.False(kiosk.Allows(new WallChange { Kind = FeedKinds.TickMine, Audience = FeedAudience.User, AudienceUserId = Guid.NewGuid() }));
    }

    private static async Task UntilAsync(RecordingFeedSubscriber viewer, Guid entityId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!viewer.Events.Any(e => e.EntityId == entityId))
        {
            Assert.True(DateTime.UtcNow < deadline, "the sentinel never arrived");
            await Task.Delay(20);
        }
    }

    // The sentinel gallery upload also writes its wall's wall.manifest (PR 2.4), just ahead of its own row: not a social row.
    private static List<string> Before(RecordingFeedSubscriber viewer, Guid sentinel) =>
        viewer.Events.TakeWhile(e => e.EntityId != sentinel).Select(e => e.Kind).Where(k => k != FeedKinds.WallManifest).ToList();

    private async Task<RecordingFeedSubscriber> SubscribeAsync(FeedViewer viewer)
    {
        var subscriber = new RecordingFeedSubscriber();
        await hub.SubscribeAsync(fixture.WallId, viewer, subscriber);
        return subscriber;
    }
}
