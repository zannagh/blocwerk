// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The kiosk facts (<see cref="FeedKioskFacts"/>) cost a wall nothing until a tablet listens: with only members subscribed,
/// or a member catching up, a read runs neither the kiosk-consent lookup nor the kiosk activity lookup. A tablet acting
/// as a member adds the activity lookup only; a resting tablet adds the consent lookup too.
/// </summary>
public sealed class FeedKioskFactsLazinessTests : IAsyncLifetime
{
    private const string ConsentLookup = "\"KioskConsentedAt\"";
    private const string ActivityLookup = "\"ActivityLog\"";

    private readonly InProcessFeedSignal signal = new();
    private readonly SqlCapture capture = new();
    private readonly SocialFeedFixture fixture;
    private readonly WallFeed feed;
    private readonly WallFeedHub hub;
    private readonly FeedCatchUp catchUp;

    public FeedKioskFactsLazinessTests()
    {
        fixture = new SocialFeedFixture(signal: signal);
        var connectionString = fixture.Harness.DbContextFactory.ConnectionString;
        feed = new WallFeed(() => TestDb.Create(connectionString), NullLogger<WallFeed>.Instance);
        hub = new WallFeedHub(feed, signal, () => TestDb.Create(connectionString, capture), NullLogger<WallFeedHub>.Instance);
        catchUp = new FeedCatchUp(feed, () => TestDb.Create(connectionString, capture));
    }

    public async Task InitializeAsync() => await hub.StartAsync(CancellationToken.None);

    public async Task DisposeAsync()
    {
        await hub.StopAsync(CancellationToken.None);
        hub.Dispose();
        fixture.Dispose();
    }

    [Fact]
    public async Task TheHub_LooksUpKioskFacts_OnlyForTheKioskViewersSubscribed()
    {
        var boulder = await fixture.SeedAsync();
        var member = await SubscribeAsync(FeedViewer.Member(fixture.OwnerId));

        Assert.Equal((false, false), await DeliveredAsync(boulder, member));

        await SubscribeAsync(FeedViewer.KioskActing());
        Assert.Equal((false, true), await DeliveredAsync(boulder, member));

        await SubscribeAsync(FeedViewer.Kiosk());
        Assert.Equal((true, true), await DeliveredAsync(boulder, member));
    }

    /// <summary>Each lookup has its own trigger: a byline rule without activity rows asks for consent only.</summary>
    [Fact]
    public async Task TheConsentLookup_AndTheActivityLookup_AreAskedForApart()
    {
        var boulder = await fixture.SeedAsync();
        var member = await SubscribeAsync(FeedViewer.Member(fixture.OwnerId));
        await SubscribeAsync(new FeedViewer([FeedAudience.Wall], null, isRestingKiosk: true));

        Assert.Equal((true, false), await DeliveredAsync(boulder, member));
    }

    [Fact]
    public async Task ACatchUp_LooksUpKioskFacts_OnlyForAKioskCaller()
    {
        var boulder = await fixture.SeedAsync();
        var start = (await feed.CurrentAsync()).ToString();
        await LogAsync(boulder);

        Assert.Equal((false, false), await CaughtUpAsync(FeedViewer.Member(fixture.OwnerId), start));
        Assert.Equal((false, false), await CaughtUpAsync(FeedViewer.Anonymous(), start, getsTheEntry: false));
        Assert.Equal((false, true), await CaughtUpAsync(FeedViewer.KioskActing(), start));
        Assert.Equal((true, true), await CaughtUpAsync(FeedViewer.Kiosk(), start));
    }

    // Writes an entry, waits until the member has it (so the hub's read is over), and says which lookups that read ran.
    private async Task<(bool Consent, bool Activity)> DeliveredAsync(Guid boulder, RecordingFeedSubscriber member)
    {
        capture.Clear();
        var entry = await LogAsync(boulder);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!member.Events.Any(e => e.EntityId == entry))
        {
            Assert.True(DateTime.UtcNow < deadline, "the entry never arrived");
            await Task.Delay(20);
        }

        return Lookups();
    }

    private async Task<(bool Consent, bool Activity)> CaughtUpAsync(FeedViewer viewer, string start, bool getsTheEntry = true)
    {
        capture.Clear();
        var page = await catchUp.ReadWallAsync(fixture.WallId, viewer, start, null);
        Assert.Equal(getsTheEntry, page.Events.Any(e => e.Kind == FeedKinds.ActivityAdded));
        return Lookups();
    }

    private (bool Consent, bool Activity) Lookups() =>
        (capture.Commands.Any(c => c.Contains(ConsentLookup, StringComparison.Ordinal)),
         capture.Commands.Any(c => c.Contains(ActivityLookup, StringComparison.Ordinal)));

    private async Task<Guid> LogAsync(Guid boulder)
    {
        var entry = new ActivityLogEntry { WallId = fixture.WallId, BoulderId = boulder, UserId = fixture.OwnerId, Type = ActivityType.CommentAdded };
        await fixture.AddAsync(entry, fixture.OwnerId);
        return entry.Id;
    }

    private async Task<RecordingFeedSubscriber> SubscribeAsync(FeedViewer viewer)
    {
        var subscriber = new RecordingFeedSubscriber();
        await hub.SubscribeAsync(fixture.WallId, viewer, subscriber);
        return subscriber;
    }
}
