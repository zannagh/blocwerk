// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The hub after things go wrong (review of PR 1.2a): a cancelled first subscribe leaves no wall behind for the loop to
/// spin on, a wall without a cursor waits for the safety poll, and a failing later page never re-delivers the pages
/// before it.
/// </summary>
public sealed class WallFeedHubRecoveryTests : IDisposable
{
    private readonly WallTestHarness harness = new();

    public void Dispose() => harness.Dispose();

    [Fact]
    public async Task ACancelledFirstSubscribe_LeavesNoWallTracked()
    {
        using var hub = Hub(new WallFeed(() => TestDb.Create(harness.DbContextFactory.ConnectionString), NullLogger<WallFeed>.Instance));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => hub.SubscribeAsync(Guid.NewGuid(), FeedViewer.Anonymous(), new RecordingFeedSubscriber(), cancelled.Token));

        Assert.Equal(0, hub.TrackedWallCount);
    }

    [Fact]
    public async Task AWallWithoutACursor_IsNotDueBeforeTheSafetyPoll()
    {
        using var hub = Hub(new FlakyWallFeed(Guid.NewGuid()));
        var wall = new FeedHubWall(Guid.NewGuid());
        var before = DateTimeOffset.UtcNow;

        await hub.ReadAsync(wall, CancellationToken.None);

        Assert.True(wall.DueAt >= before + FeedRepoll.SafetyPoll);
    }

    [Fact]
    public async Task ALaterPageThatFails_DoesNotRedeliverTheEarlierPages()
    {
        await harness.SeedWallAsync(holdCount: 0);
        var feed = new FlakyWallFeed(harness.WallId);
        using var hub = Hub(feed);
        var subscriber = new RecordingFeedSubscriber();
        using var subscription = await hub.SubscribeAsync(harness.WallId, FeedViewer.Anonymous(), subscriber);

        await hub.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (feed.Calls < 4 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20); // page, failure, retry (250 ms backoff), then the safety poll
        }

        await hub.StopAsync(CancellationToken.None);

        var cursors = subscriber.Events.Select(e => e.Cursor).ToList();
        Assert.True(feed.Calls >= 3, $"only {feed.Calls} reads");
        Assert.Equal(WallFeedHub.PageSize, cursors.Count);
        Assert.Equal(cursors.Count, cursors.Distinct().Count());
    }

    private WallFeedHub Hub(IWallFeed feed) =>
        new(feed, new InProcessFeedSignal(), () => TestDb.Create(harness.DbContextFactory.ConnectionString), NullLogger<WallFeedHub>.Instance);
}
