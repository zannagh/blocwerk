// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The two delivery rules for viewers outside the wall, together as the hub and the catch-up apply them: the kiosk's
/// activity rule (<see cref="FeedKioskActivity"/>) and the outside classes' comment rule (<c>FeedOutsideComments</c>).
/// The resting tablet gets a published boulder's comments and activity live and on catch-up, bylined as "A member" for
/// an author without kiosk consent; a draft's or an archived boulder's comments and activity reach neither the tablet
/// nor a share link, also on a replay after the boulder was archived; a share link gets the published comment but no
/// activity; a member gets everything.
/// </summary>
public sealed class KioskFeedCompositionTests : IAsyncLifetime
{
    private readonly InProcessFeedSignal signal = new();
    private readonly SocialFeedFixture fixture;
    private readonly WallFeed feed;
    private readonly WallFeedHub hub;
    private readonly FeedCatchUp catchUp;

    public KioskFeedCompositionTests()
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
    public async Task BothRules_Apply_AndTheRestingKioskGetsAPublishedBouldersCommentAndActivity_LiveAndOnCatchUp()
    {
        var published = await fixture.SeedAsync();
        var draft = await fixture.AddBoulderAsync(isDraft: true);
        var archived = await fixture.AddBoulderAsync();
        await ArchiveAsync(archived);
        var climber = await fixture.Harness.AddMemberAsync("climber@test", WallRole.Member);
        var kiosk = await SubscribeAsync(FeedViewer.Kiosk());
        var share = await SubscribeAsync(FeedViewer.Anonymous());
        var member = await SubscribeAsync(FeedViewer.Member(fixture.OwnerId));
        var start = (await feed.CurrentAsync()).ToString();

        var shown = new[] { await CommentAsync(published, climber.Id), await LogAsync(published, climber.Id) };
        var hidden = new[]
        {
            await CommentAsync(draft, climber.Id), await LogAsync(draft, climber.Id),
            await CommentAsync(archived, climber.Id), await LogAsync(archived, climber.Id),
        };
        var sentinel = await SentinelAsync(kiosk, share, member);

        Assert.Equal(shown, Social(kiosk, sentinel).Select(e => e.EntityId!.Value));
        Assert.All(Social(kiosk, sentinel), e => Assert.Equal((null, KioskBylines.PlaceholderName), (e.Actor!.Id, e.Actor.Name)));
        Assert.Equal([shown[0]], Social(share, sentinel).Select(e => e.EntityId!.Value));
        Assert.Equal([.. shown, .. hidden], Social(member, sentinel).Select(e => e.EntityId!.Value));

        var caughtUp = (await catchUp.ReadWallAsync(fixture.WallId, FeedViewer.Kiosk(), start, null)).Events
            .Where(e => e.Kind is FeedKinds.CommentAdded or FeedKinds.ActivityAdded).ToList();
        Assert.Equal(shown, caughtUp.Select(e => e.EntityId!.Value));
        Assert.All(caughtUp, e => Assert.Equal(KioskBylines.PlaceholderName, e.Actor!.Name));

        // Archived since: a replay from the old cursor hands the tablet neither row any more, by both rules at once.
        await ArchiveAsync(published);
        Assert.DoesNotContain(
            (await catchUp.ReadWallAsync(fixture.WallId, FeedViewer.Kiosk(), start, null)).Events,
            e => e.Kind is FeedKinds.CommentAdded or FeedKinds.ActivityAdded);
    }

    private async Task ArchiveAsync(Guid boulder)
    {
        await using var db = fixture.Harness.CreateContext();
        await db.Boulders.Where(b => b.Id == boulder).ExecuteUpdateAsync(u => u.SetProperty(b => b.IsArchived, true));
    }

    private static List<FeedEnvelope> Social(RecordingFeedSubscriber viewer, Guid sentinel) =>
        viewer.Events.TakeWhile(e => e.EntityId != sentinel)
            .Where(e => e.Kind is FeedKinds.CommentAdded or FeedKinds.ActivityAdded).ToList();

    private async Task<Guid> CommentAsync(Guid boulder, Guid userId)
    {
        var comment = new BoulderComment { BoulderId = boulder, UserId = userId, Text = "beta" };
        await fixture.AddAsync(comment, userId);
        return comment.Id;
    }

    private async Task<Guid> LogAsync(Guid boulder, Guid userId)
    {
        var entry = new ActivityLogEntry { WallId = fixture.WallId, BoulderId = boulder, UserId = userId, Type = ActivityType.CommentAdded };
        await fixture.AddAsync(entry, userId);
        return entry.Id;
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
