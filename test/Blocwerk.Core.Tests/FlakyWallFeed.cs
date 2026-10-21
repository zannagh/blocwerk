// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A feed of one full page (500 rows) whose next read fails every other time: a database blip after a page was delivered.
/// From the review's probe.
/// </summary>
internal sealed class FlakyWallFeed : IWallFeed
{
    private readonly Guid wallId;
    private readonly Guid epoch = Guid.NewGuid();
    private int calls;

    public FlakyWallFeed(Guid wallId)
    {
        this.wallId = wallId;
    }

    public int Calls => Volatile.Read(ref calls);

    public Task<Guid> InitializeAsync(CancellationToken cancellationToken = default) => Task.FromResult(epoch);

    public Task<FeedCursor> CurrentAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new FeedCursor(epoch, new FeedPosition(null, 0)));

    public Task<FeedPage> ReadAsync(FeedFilter filter, FeedCursor after, int limit, CancellationToken cancellationToken = default)
    {
        var n = Interlocked.Increment(ref calls);
        if (after.Position.Seq >= WallFeedHub.PageSize)
        {
            return n % 2 == 0
                ? throw new InvalidOperationException("db blip")
                : Task.FromResult(new FeedPage([], after, Resync: false));
        }

        var rows = Enumerable.Range(1, limit)
            .Select(i => new WallChange { WallId = wallId, Seq = after.Position.Seq + i, Kind = "hold.changed", CreatedAt = DateTimeOffset.UtcNow })
            .ToList();
        return Task.FromResult(new FeedPage(rows, new FeedCursor(epoch, new FeedPosition(null, rows[^1].Seq)), Resync: false));
    }

    public Task<long?> AppendAsync(BlocwerkDbContext db, WallChange row, CancellationToken cancellationToken = default) =>
        Task.FromResult<long?>(null);
}
