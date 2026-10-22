// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The kiosk tablet reads its wall's activity live (<see cref="FeedKioskActivity"/>) although <c>activity.added</c> is a
/// members row: only what <c>KioskActivityVisibility</c> lets it read, decided at delivery. Resting, it names consenting
/// members and setters only (<see cref="KioskBylines"/>); acting as a member it sees names. A share link gets no activity,
/// live or on catch-up.
/// </summary>
public sealed class KioskActivityFeedTests : IAsyncLifetime
{
    private readonly InProcessFeedSignal signal = new();
    private readonly SocialFeedFixture fixture;
    private readonly WallFeed feed;
    private readonly WallFeedHub hub;
    private readonly FeedCatchUp catchUp;

    public KioskActivityFeedTests()
    {
        fixture = new SocialFeedFixture(signal: signal);
        var connectionString = fixture.Harness.DbContextFactory.ConnectionString;
        feed = new WallFeed(() => TestDb.Create(connectionString), NullLogger<WallFeed>.Instance);
        hub = new WallFeedHub(feed, signal, () => TestDb.Create(connectionString), NullLogger<WallFeedHub>.Instance);
        catchUp = new FeedCatchUp(feed, () => TestDb.Create(connectionString));
    }

    public async Task InitializeAsync() => await hub.StartAsync(CancellationToken.None);

    public async Task DisposeAsync()
    {
        await hub.StopAsync(CancellationToken.None);
        hub.Dispose();
        fixture.Dispose();
    }

    [Fact]
    public async Task TheKiosk_GetsAPublishedBouldersActivityLive_NeverADraftsAnArchivedOnesOrTheWallWideRows_AndAShareLinkNone()
    {
        var published = await fixture.SeedAsync();
        var draft = await fixture.AddBoulderAsync(isDraft: true);
        var archived = await ArchivedBoulderAsync();
        var kiosk = await SubscribeAsync(FeedViewer.Kiosk());
        var share = await SubscribeAsync(FeedViewer.Anonymous());
        var member = await SubscribeAsync(FeedViewer.Member(fixture.OwnerId));

        var shown = await LogAsync(published, ActivityType.AttemptLogged);
        await LogAsync(draft, ActivityType.AttemptLogged);
        await LogAsync(archived, ActivityType.CommentAdded);
        await LogAsync(null, ActivityType.MemberJoined);
        await LogAsync(null, ActivityType.MemberRoleChanged);
        await LogAsync(null, ActivityType.WallPhotoStaged);
        var wallLevel = await LogAsync(null, ActivityType.HoldAdded);
        var sentinel = await SentinelAsync(kiosk, share, member);

        Assert.Equal([shown], ActivityIds(kiosk, sentinel)); // per boulder only: no wall-wide entry, not even a hold's
        Assert.Empty(ActivityIds(share, sentinel));
        Assert.Equal(7, ActivityIds(member, sentinel).Count);
    }

    [Fact]
    public async Task OnCatchUp_TheKioskGetsTheSameRows_DecidedWhenRead_AndAShareLinkNone()
    {
        var boulder = await fixture.SeedAsync();
        var start = (await feed.CurrentAsync()).ToString();
        var entry = await LogAsync(boulder, ActivityType.AttemptLogged);
        await LogAsync(null, ActivityType.MemberJoined);

        Assert.Equal([entry], await CaughtUpAsync(FeedViewer.Kiosk(), start));
        Assert.Empty(await CaughtUpAsync(FeedViewer.Anonymous(), start));

        // Archived after the row was written: a replay from the old cursor no longer hands it to the tablet.
        await using (var db = fixture.Harness.CreateContext())
        {
            await db.Boulders.Where(b => b.Id == boulder).ExecuteUpdateAsync(u => u.SetProperty(b => b.IsArchived, true));
        }

        Assert.Empty(await CaughtUpAsync(FeedViewer.Kiosk(), start));
        Assert.Equal(2, (await CaughtUpAsync(FeedViewer.Member(fixture.OwnerId), start)).Count);
    }

    [Fact]
    public async Task TheRestingKiosk_NamesConsentingMembersAndSetters_ElseAMember_ActingAndShareLinksSeeTheName()
    {
        var boulder = await fixture.SeedAsync();
        var climber = await fixture.Harness.AddMemberAsync("climber@test", WallRole.Member);
        await fixture.ChangeAsync<User>(u => u.Id == climber.Id, (u, _) => (u.AvatarImage, u.AvatarContentType) = ([1, 2], "image/png"));
        var cora = await KioskBylineChecks.ConsentingMemberAsync(fixture.Harness, "cora@test");
        var erased = await KioskBylineChecks.ErasedUserAsync(fixture.Harness);
        var resting = await SubscribeAsync(FeedViewer.Kiosk());
        var acting = await SubscribeAsync(FeedViewer.KioskActing());
        var share = await SubscribeAsync(FeedViewer.Anonymous());

        await LogAsync(boulder, ActivityType.AttemptLogged, climber.Id);
        await LogAsync(boulder, ActivityType.AttemptLogged, cora.Id);
        await LogAsync(boulder, ActivityType.AttemptLogged, erased);
        await LogAsync(boulder, ActivityType.BoulderCreated, climber.Id);
        await fixture.AddAsync(new BoulderComment { BoulderId = boulder, UserId = climber.Id, Text = "beta" }, climber.Id);
        var sentinel = await SentinelAsync(resting, acting, share);

        (Guid?, string, bool)[] masked = [(null, KioskBylines.PlaceholderName, false)];
        Assert.Equal(
            [.. masked, (null, cora.Name, false), (null, PlaceholderIdentity.DisplayName, false), (climber.Id, climber.Name, true), .. masked],
            Bylines(resting, sentinel));
        (Guid?, string, bool)[] named =
        [
            (climber.Id, climber.Name, true), (null, cora.Name, false), (null, PlaceholderIdentity.DisplayName, false),
            (climber.Id, climber.Name, true), (climber.Id, climber.Name, true),
        ];
        Assert.Equal(named, Bylines(acting, sentinel));
        Assert.Equal(named.Where((_, i) => i == 4), Bylines(share, sentinel)); // the comment; a share link gets no activity
    }

    [Fact]
    public void TheKioskViewer_ReadsActivityAsAMemberKind_AndIsNotTheShareViewer()
    {
        Assert.Equal([FeedKinds.ActivityAdded, FeedKinds.ActivityRemoved], FeedViewer.Kiosk().MemberKinds);
        Assert.Empty(FeedViewer.Anonymous().MemberKinds);
        Assert.Empty(new FeedViewer([FeedAudience.Wall, FeedAudience.Members], null, [FeedKinds.ActivityAdded]).MemberKinds);
        Assert.False(FeedViewer.Kiosk().SameAs(FeedViewer.Anonymous()));
        Assert.True(FeedViewer.Kiosk().SameAs(FeedViewer.Kiosk()));
        Assert.False(FeedViewer.Kiosk().SameAs(FeedViewer.KioskActing()));
        Assert.True(FeedViewer.Kiosk().IsRestingKiosk);
        Assert.False(FeedViewer.KioskActing().IsRestingKiosk);
        Assert.Equal(FeedViewer.Kiosk().MemberKinds, FeedViewer.KioskActing().MemberKinds);

        // The audience check alone never hands a members row to the tablet: the delivery rule has to say yes.
        Assert.False(FeedViewer.Kiosk().Allows(new WallChange { Kind = FeedKinds.ActivityAdded, Audience = FeedAudience.Members }));
    }

    private static List<FeedEnvelope> Activity(RecordingFeedSubscriber viewer, Guid sentinel) =>
        viewer.Events.TakeWhile(e => e.EntityId != sentinel).Where(e => e.Kind == FeedKinds.ActivityAdded).ToList();

    // Each activity and comment row's byline, in delivery order: (id, name, has avatar).
    private static List<(Guid?, string, bool)> Bylines(RecordingFeedSubscriber viewer, Guid sentinel) =>
        viewer.Events.TakeWhile(e => e.EntityId != sentinel)
            .Where(e => e.Kind is FeedKinds.ActivityAdded or FeedKinds.CommentAdded)
            .Select(e => (e.Actor!.Id, e.Actor.Name, e.Actor.HasAvatar)).ToList();

    private static List<Guid?> ActivityIds(RecordingFeedSubscriber viewer, Guid sentinel) =>
        Activity(viewer, sentinel).Select(e => e.EntityId).ToList();

    private async Task<List<Guid?>> CaughtUpAsync(FeedViewer viewer, string after) =>
        (await catchUp.ReadWallAsync(fixture.WallId, viewer, after, null)).Events
            .Where(e => e.Kind == FeedKinds.ActivityAdded).Select(e => e.EntityId).ToList();

    private async Task<Guid?> LogAsync(Guid? boulderId, ActivityType type, Guid? userId = null)
    {
        var entry = new ActivityLogEntry { WallId = fixture.WallId, BoulderId = boulderId, UserId = userId ?? fixture.OwnerId, Type = type };
        await fixture.AddAsync(entry, entry.UserId);
        return entry.Id;
    }

    private async Task<Guid> ArchivedBoulderAsync()
    {
        var id = await fixture.AddBoulderAsync();
        await using var db = fixture.Harness.CreateContext();
        await db.Boulders.Where(b => b.Id == id).ExecuteUpdateAsync(u => u.SetProperty(b => b.IsArchived, true));
        return id;
    }

    // A row every viewer receives: once each has it, everything before it was delivered, so a missing event is withheld.
    private async Task<Guid> SentinelAsync(params RecordingFeedSubscriber[] viewers)
    {
        var sentinel = new WallImage { WallId = fixture.WallId, StoragePath = "s.jpg", ContentType = "image/jpeg" };
        await fixture.AddAsync(sentinel);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!viewers.All(v => v.Events.Any(e => e.EntityId == sentinel.Id)))
        {
            Assert.True(DateTime.UtcNow < deadline, "the sentinel never arrived");
            await Task.Delay(20);
        }

        return sentinel.Id;
    }

    private async Task<RecordingFeedSubscriber> SubscribeAsync(FeedViewer viewer)
    {
        var subscriber = new RecordingFeedSubscriber();
        await hub.SubscribeAsync(fixture.WallId, viewer, subscriber);
        return subscriber;
    }
}
