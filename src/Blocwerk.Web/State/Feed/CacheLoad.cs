// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed;

namespace Blocwerk.Web.State.Feed;

/// <summary>
/// One cached load and its <c>CursorAtLoad</c> (design §5.2): the feed cursor read just before the query, so the load
/// reflects every change at or below it. An event at or below it is already in the value and is skipped.
/// </summary>
/// <typeparam name="T">The cached value's type.</typeparam>
/// <param name="Value">The loaded value.</param>
/// <param name="At">The cursor read before the query; null when the feed could not be read (then any event evicts).</param>
public sealed record CacheLoad<T>(T Value, FeedCursor? At)
{
    /// <summary>
    /// True when an event at <paramref name="cursor"/> may be missing from this load: the load has no cursor, belongs to
    /// another epoch, or was taken before the event.
    /// </summary>
    public bool Predates(FeedCursor cursor) =>
        At is not { } at || at.Epoch != cursor.Epoch || at.Position < cursor.Position;

    /// <summary>True when this load saw everything up to <paramref name="start"/> (same epoch, at or after it).</summary>
    public bool Covers(FeedCursor start) => At is { } at && at.Epoch == start.Epoch && at.Position >= start.Position;
}
