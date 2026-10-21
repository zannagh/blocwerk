// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Feed;

/// <summary>
/// One batch of committed feed rows, as <see cref="FeedCommits"/> hands it to its observers: one actor-free event per row
/// (<see cref="FeedCommitEvent"/>). The rows themselves, with their actor and addressee ids, stay inside Core.
/// </summary>
public sealed class FeedCommit
{
    internal FeedCommit(IReadOnlyList<WallChange> rows)
    {
        Rows = rows;
        Events = rows.Select(r => new FeedCommitEvent(FeedEnvelopes.Committed(r), r.Audience)).ToList();
    }

    /// <summary>
    /// Gets the cursor every committed envelope carries: of an epoch no stored row has, so every cached load "predates" it
    /// and an own-write eviction is unconditional. A load taken after the commit is evicted once more, which only costs a
    /// reload; one taken before it is never kept.
    /// </summary>
    public static FeedCursor Cursor { get; } = new(Guid.Empty, new FeedPosition(long.MaxValue, long.MaxValue));

    /// <summary>Gets one event per committed row, in commit order.</summary>
    public IReadOnlyList<FeedCommitEvent> Events { get; }

    /// <summary>Gets the committed rows (Core and tests only). Bulk-site rows carry the <c>Seq</c> and <c>TxId</c> read back on insert.</summary>
    internal IReadOnlyList<WallChange> Rows { get; }
}
