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

    /// <summary>Gets how many walls the hub tracks (walls with a subscriber, or one on its way).</summary>
    internal int TrackedWallCount => walls.Count;

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
            await EnterAsync(wall, cancellationToken);
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
            if (wall.IsRemoved)
            {
                return;
            }

            if (wall.Cursor is not { } cursor)
            {
                // No subscriber has taken a cursor yet (a subscribe in flight or one that failed): never due again
                // before the safety poll, so no path can make the loop spin on it.
                wall.Defer(DateTimeOffset.UtcNow);
                return;
            }

            cursor = await ReadPagesAsync(wall, cursor, cancellationToken);
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

    // Page by page from the cursor. The wall's cursor moves after every delivered page, not once at the end: a later
    // page that fails must not make the retry hand out the pages already delivered again.
    private async Task<FeedCursor> ReadPagesAsync(FeedHubWall wall, FeedCursor cursor, CancellationToken cancellationToken)
    {
        var filter = FeedFilter.AllOfWall(wall.WallId);
        while (true)
        {
            var page = await feed.ReadAsync(filter, cursor, PageSize, cancellationToken);
            if (page.Resync)
            {
                FeedFanOut.DeliverResync(wall, page.Next, logger);
                cursor = page.Next;
                wall.Cursor = cursor;
                break;
            }

            if (page.Changes.Count > 0)
            {
                await using var db = createContext();
                var hydrated = await FeedHydration.LoadAsync(db, page.Changes, cancellationToken);
                FeedFanOut.Deliver(wall, page.Changes, hydrated, page.Next, logger);
            }

            cursor = page.Next;
            wall.Cursor = cursor;
            if (page.Changes.Count < PageSize)
            {
                break;
            }
        }

        return cursor;
    }

    // Takes the wall's gate. A wait that is cancelled or fails retires the wall if nobody holds it, so a cancelled first
    // subscribe leaves no cursor-less wall behind for the loop.
    private async Task EnterAsync(FeedHubWall wall, CancellationToken cancellationToken)
    {
        try
        {
            await wall.Gate.WaitAsync(cancellationToken);
        }
        catch
        {
            Retire(wall, null);
            throw;
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
