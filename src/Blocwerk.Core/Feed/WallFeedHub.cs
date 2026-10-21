// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;
using Blocwerk.Core.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The <see cref="IWallFeedHub"/>: one background loop over the subscribed walls (design §5.2). A wake makes a wall due
/// now; a woken transaction not yet below the horizon is re-polled with backoff (<see cref="FeedRepoll"/>); every wall is
/// read at least every 30 s. Only reads advance the cursors, never wakes. A wall with no subscriber is not tracked at all:
/// its wakes are ignored and nothing reads it, so with nobody subscribed (PR 1.2a) the hub costs nothing. After a
/// restart a wall's cursor starts at the current horizon when its first subscriber arrives (E5).
/// </summary>
public sealed class WallFeedHub : BackgroundService, IWallFeedHub
{
    /// <summary>The rows one read of a wall asks for; a full page is followed by the next straight away.</summary>
    public const int PageSize = 500;

    private readonly IWallFeed feed;
    private readonly IFeedSignal signal;
    private readonly Func<BlocwerkDbContext> createContext;
    private readonly ILogger<WallFeedHub> logger;
    private readonly ConcurrentDictionary<Guid, FeedHubWall> walls = new();

    public WallFeedHub(IWallFeed feed, IFeedSignal signal, Func<BlocwerkDbContext> createContext, ILogger<WallFeedHub> logger)
    {
        this.feed = feed;
        this.signal = signal;
        this.createContext = createContext;
        this.logger = logger;
    }

    public async Task<FeedSubscription> SubscribeAsync(
        Guid wallId, FeedViewer viewer, IFeedSubscriber subscriber, CancellationToken cancellationToken = default)
    {
        var entry = new FeedHubSubscriber(viewer, subscriber);
        while (true)
        {
            var wall = walls.GetOrAdd(wallId, id => new FeedHubWall(id));
            await wall.Gate.WaitAsync(cancellationToken);
            try
            {
                if (wall.IsRemoved)
                {
                    continue; // its last subscriber left while this one waited; the next round gets a fresh wall
                }

                if (wall.Cursor is null)
                {
                    try
                    {
                        wall.Cursor = await feed.CurrentAsync(cancellationToken);
                    }
                    catch
                    {
                        Retire(wall, null); // a wall nobody holds must not stay behind with no cursor
                        throw;
                    }
                }

                if (wall.TryAdd(entry))
                {
                    var start = wall.Cursor.Value;
                    return new FeedSubscription(wallId, start, () => Retire(wall, entry));
                }
            }
            finally
            {
                wall.Gate.Release();
            }
        }
    }

    // Reads the wall's rows after its cursor, page by page, and hands them out. Under the wall's gate, so a subscribe in
    // the meantime starts exactly where this read ends.
    internal async Task ReadAsync(FeedHubWall wall, CancellationToken cancellationToken)
    {
        await wall.Gate.WaitAsync(cancellationToken);
        try
        {
            if (wall.IsRemoved || wall.Cursor is not { } cursor)
            {
                return;
            }

            var filter = FeedFilter.AllOfWall(wall.WallId);
            while (true)
            {
                var page = await feed.ReadAsync(filter, cursor, PageSize, cancellationToken);
                if (page.Resync)
                {
                    FeedFanOut.DeliverResync(wall, page.Next, logger);
                    cursor = page.Next;
                    break;
                }

                if (page.Changes.Count > 0)
                {
                    await using var db = createContext();
                    var hydrated = await FeedHydration.LoadAsync(db, page.Changes, cancellationToken);
                    FeedFanOut.Deliver(wall, page.Changes, hydrated, page.Next, logger);
                }

                cursor = page.Next;
                if (page.Changes.Count < PageSize)
                {
                    break;
                }
            }

            wall.AfterRead(cursor, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Feed read of wall {WallId} failed; retrying with backoff", wall.WallId);
            wall.AfterFailure(DateTimeOffset.UtcNow);
        }
        finally
        {
            wall.Gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            DrainWakes(DateTimeOffset.UtcNow);
            foreach (var wall in walls.Values.Where(w => w.DueAt <= DateTimeOffset.UtcNow).ToList())
            {
                await ReadAsync(wall, stoppingToken);
            }

            await WaitAsync(NextDelay(DateTimeOffset.UtcNow), stoppingToken);
        }
    }

    private void Retire(FeedHubWall wall, FeedHubSubscriber? entry)
    {
        if (wall.RemoveAndRetireIfEmpty(entry))
        {
            walls.TryRemove(new KeyValuePair<Guid, FeedHubWall>(wall.WallId, wall));
        }
    }

    // Wakes of walls nobody subscribed to are dropped: there is nobody to deliver to, and a later subscriber starts at
    // the horizon anyway.
    private void DrainWakes(DateTimeOffset now)
    {
        while (signal.Wakes.TryRead(out var wake))
        {
            if (walls.TryGetValue(wake.WallId, out var wall))
            {
                wall.Wake(wake.TxId, now);
            }
        }
    }

    private TimeSpan NextDelay(DateTimeOffset now)
    {
        var next = walls.Values.Select(w => w.DueAt).DefaultIfEmpty(now + FeedRepoll.SafetyPoll).Min();
        var delay = next - now;
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay > FeedRepoll.SafetyPoll ? FeedRepoll.SafetyPoll : delay;
    }

    // Until the next wake or the next due wall, whichever comes first.
    private async Task WaitAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        if (delay == TimeSpan.Zero)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(delay);
        try
        {
            if (!await signal.Wakes.WaitToReadAsync(timeout.Token))
            {
                await Task.Delay(delay, stoppingToken); // the channel was completed; fall back to polling
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // The delay passed with no wake.
        }
    }
}
