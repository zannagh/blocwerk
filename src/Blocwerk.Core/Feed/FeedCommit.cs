// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Feed;

/// <summary>
/// One batch of committed feed rows, as <see cref="FeedCommits"/> hands it to its observers: the rows (read-only, detached
/// from their context) and, aligned with them, their envelopes without actor or content.
/// </summary>
public sealed class FeedCommit
{
    internal FeedCommit(IReadOnlyList<WallChange> rows)
    {
        Rows = rows;
        Events = rows.Select(FeedEnvelopes.Committed).ToList();
    }

    /// <summary>
    /// Gets the cursor every committed envelope carries: of an epoch no stored row has, so every cached load "predates" it
    /// and an own-write eviction is unconditional. A load taken after the commit is evicted once more, which only costs a
    /// reload; one taken before it is never kept.
    /// </summary>
    public static FeedCursor Cursor { get; } = new(Guid.Empty, new FeedPosition(long.MaxValue, long.MaxValue));

    /// <summary>Gets the committed rows. Observers must not change them.</summary>
    public IReadOnlyList<WallChange> Rows { get; }

    /// <summary>Gets one envelope per row (same order), carrying <see cref="Cursor"/>, no actor and no hydrated content.</summary>
    public IReadOnlyList<FeedEnvelope> Events { get; }
}
