// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Feed.Payloads;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="WallFeedHub"/> on the SQLite harness: a wake delivers the rows after the subscription's start, hydrated and
/// narrowed to each viewer's audiences; tombstones and erased authors; unsubscribing and a throwing subscriber.
/// </summary>
public sealed class WallFeedHubTests : IAsyncLifetime
{
    private readonly WallTestHarness harness = new();
    private readonly InProcessFeedSignal signal = new();
    private readonly WallFeedHub hub;

    public WallFeedHubTests()
    {
        var connectionString = harness.DbContextFactory.ConnectionString;
        var feed = new WallFeed(() => TestDb.Create(connectionString), NullLogger<WallFeed>.Instance);
        hub = new WallFeedHub(feed, signal, () => TestDb.Create(connectionString), NullLogger<WallFeedHub>.Instance);
    }

    public async Task InitializeAsync()
    {
        await harness.SeedWallAsync(holdCount: 0);
        await hub.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await hub.StopAsync(CancellationToken.None);
        hub.Dispose();
        harness.Dispose();
    }

    [Fact]
    public async Task AWake_DeliversTheRowsAfterTheStart_HydratedAndInOrder()
    {
        await WriteAsync(Row(FeedKinds.HoldChanged)); // before the subscription: not delivered
        var sam = await harness.AddMemberAsync("sam@test", WallRole.Member);
        var comment = await AddCommentAsync(sam.Id, "Nice heel hook");
        var subscriber = new RecordingFeedSubscriber();
        using var subscription = await hub.SubscribeAsync(harness.WallId, FeedViewer.Member(sam.Id), subscriber);

        await WriteAsync(
            Row(FeedKinds.CommentAdded, entityId: comment, actor: sam.Id, data: new CommentPayload(Guid.NewGuid())),
            Row(FeedKinds.HoldChanged, data: new HoldPayload(6, null)));
        var events = await subscriber.WaitForEventsAsync(2);

        Assert.Equal([FeedKinds.CommentAdded, FeedKinds.HoldChanged], events.Select(e => e.Kind));
        Assert.Equal(("sam@test", "Nice heel hook", false), (events[0].Actor!.Name, events[0].Hydrated!.Text, events[0].Tombstone));
        Assert.Equal(6, events[1].Data!.Value.GetProperty("generation").GetInt32());
        Assert.Equal(1, events[1].Data!.Value.GetProperty("sv").GetInt32());
        Assert.All(events, e => Assert.True(FeedCursor.TryParse(e.Cursor, out var c) && c.Position > subscription.Start.Position));
    }

    [Fact]
    public async Task EachSubscriber_GetsOnlyItsAudiences_AndUserRowsOnlyForItsUser()
    {
        var member = Guid.NewGuid();
        var setter = Guid.NewGuid();
        var anonymous = await SubscribeAsync(FeedViewer.Anonymous());
        var asMember = await SubscribeAsync(FeedViewer.Member(member));
        var asSetter = await SubscribeAsync(FeedViewer.Setter(setter));

        await WriteAsync(
            Row("wall"),
            Row("members", audience: FeedAudience.Members),
            Row("setters", audience: FeedAudience.Setters),
            Row("member-own", audience: FeedAudience.User, user: member),
            Row("someone-else", audience: FeedAudience.User, user: Guid.NewGuid()));

        Assert.Equal(["wall", "members", "setters"], (await asSetter.WaitForEventsAsync(3)).Select(e => e.Kind));
        Assert.Equal(["wall", "members", "member-own"], (await asMember.WaitForEventsAsync(3)).Select(e => e.Kind));
        Assert.Equal(["wall"], (await anonymous.WaitForEventsAsync(1)).Select(e => e.Kind));
    }

    [Fact]
    public async Task AGoneComment_IsATombstone_AndAGoneOrErasedAuthor_IsGhost()
    {
        var erased = await harness.AddMemberAsync("erased@test", WallRole.Member);
        await using (var db = harness.CreateContext())
        {
            var user = await db.Users.SingleAsync(u => u.Id == erased.Id);
            user.DeletedAt = DateTimeOffset.UtcNow;
            user.DisplayName = PlaceholderIdentity.DisplayName;
            await db.SaveChangesAsync();
        }

        var subscriber = await SubscribeAsync(FeedViewer.Anonymous());
        await WriteAsync(
            Row(FeedKinds.CommentAdded, entityId: Guid.NewGuid(), actor: erased.Id),
            Row(FeedKinds.HoldChanged, actor: Guid.NewGuid()));
        var events = await subscriber.WaitForEventsAsync(2);

        Assert.True(events[0].Tombstone);
        Assert.Null(events[0].Hydrated);
        Assert.All(events, e => Assert.Equal(((Guid?)null, PlaceholderIdentity.DisplayName), (e.Actor!.Id, e.Actor.Name))); // no ids outside the wall
    }

    [Fact]
    public async Task AfterUnsubscribing_NothingIsDelivered_AndAThrowingSubscriberDoesNotStopTheOthers()
    {
        var gone = new RecordingFeedSubscriber();
        (await hub.SubscribeAsync(harness.WallId, FeedViewer.Anonymous(), gone)).Dispose();
        using var bug = await hub.SubscribeAsync(harness.WallId, FeedViewer.Anonymous(), new ThrowingFeedSubscriber());
        var stays = await SubscribeAsync(FeedViewer.Anonymous());

        await WriteAsync(Row("first"));
        await stays.WaitForEventsAsync(1);
        await WriteAsync(Row("second"));

        Assert.Equal(["first", "second"], (await stays.WaitForEventsAsync(2)).Select(e => e.Kind));
        Assert.Empty(gone.Deliveries);
    }

    private async Task<RecordingFeedSubscriber> SubscribeAsync(FeedViewer viewer)
    {
        var subscriber = new RecordingFeedSubscriber();
        await hub.SubscribeAsync(harness.WallId, viewer, subscriber);
        return subscriber;
    }

    private WallChange Row(
        string kind, Guid? entityId = null, Guid? actor = null, FeedPayload? data = null,
        FeedAudience audience = FeedAudience.Wall, Guid? user = null) => new()
    {
        WallId = harness.WallId,
        Kind = kind,
        EntityId = entityId ?? Guid.NewGuid(),
        ActorUserId = actor,
        DataJson = data is null ? null : FeedJson.Serialize(data),
        Audience = audience,
        AudienceUserId = user,
    };

    private async Task WriteAsync(params WallChange[] rows)
    {
        await using var db = harness.CreateContext();
        db.WallChanges.AddRange(rows);
        await db.SaveChangesAsync();
        signal.Wake(new FeedWake(harness.WallId, null));
    }

    private async Task<Guid> AddCommentAsync(Guid userId, string text)
    {
        await using var db = harness.CreateContext();
        var boulder = new Boulder { WallId = harness.WallId, Name = "B1", CreatedByUserId = harness.Owner.Id };
        var comment = new BoulderComment { BoulderId = boulder.Id, UserId = userId, Text = text };
        db.AddRange(boulder, comment);
        await db.SaveChangesAsync();
        return comment.Id;
    }
}
