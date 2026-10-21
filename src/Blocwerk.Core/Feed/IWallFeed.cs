// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The durable live-update feed (<see cref="WallChange"/>): horizon reads, the current cursor, and appends from bulk
/// write sites. Only a horizon read advances a cursor; a post-commit push is a wake-up and nothing more.
/// </summary>
public interface IWallFeed
{
    /// <summary>Creates the epoch, or starts a new one after a restore. Runs at startup, before anything reads the feed.</summary>
    Task<Guid> InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The current horizon cursor. Taken before an aggregate is loaded it is the <c>CursorAtLoad</c>: the load sees every
    /// change at or below it. After a restart, a reader with nobody to replay to starts here.
    /// </summary>
    Task<FeedCursor> CurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The rows of <paramref name="filter"/> after <paramref name="after"/>, at most <paramref name="limit"/> (1..1000), in
    /// <c>(TxId, Seq)</c> order and only below the horizon. A cursor of another epoch, or at or below the retention floor, is
    /// answered with a resync.
    /// </summary>
    Task<FeedPage> ReadAsync(FeedFilter filter, FeedCursor after, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts <paramref name="row"/> with raw SQL on <paramref name="db"/>'s connection, for the bulk sites
    /// (<c>ExecuteUpdate</c>/<c>ExecuteDelete</c>) that bypass <c>SaveChanges</c>. Valid only inside an open transaction
    /// on <paramref name="db"/>, so the row commits or rolls back with the write; throws otherwise. Returns the row's
    /// transaction id (null on SQLite).
    /// </summary>
    Task<long?> AppendAsync(BlocwerkDbContext db, WallChange row, CancellationToken cancellationToken = default);
}
