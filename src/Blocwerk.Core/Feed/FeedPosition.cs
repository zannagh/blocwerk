// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Feed;

/// <summary>
/// A place in the feed within one epoch: <c>(TxId, Seq)</c>, compared lexicographically. On SQLite every
/// <see cref="TxId"/> is null and only <see cref="Seq"/> counts, so a comparison with a null side compares
/// <see cref="Seq"/> alone.
/// </summary>
/// <param name="TxId">The writing transaction's id, or null on SQLite.</param>
/// <param name="Seq">The row's sequence; <see cref="long.MaxValue"/> marks "everything of <paramref name="TxId"/>".</param>
public readonly record struct FeedPosition(long? TxId, long Seq) : IComparable<FeedPosition>
{
    /// <summary>Gets the position before every row.</summary>
    public static FeedPosition Start => new(0, 0);

    /// <summary>Gets a value indicating whether this is <see cref="Start"/> (or SQLite's equivalent).</summary>
    public bool IsStart => (TxId ?? 0) == 0 && Seq == 0;

    public static bool operator <(FeedPosition left, FeedPosition right) => left.CompareTo(right) < 0;

    public static bool operator >(FeedPosition left, FeedPosition right) => left.CompareTo(right) > 0;

    public static bool operator <=(FeedPosition left, FeedPosition right) => left.CompareTo(right) <= 0;

    public static bool operator >=(FeedPosition left, FeedPosition right) => left.CompareTo(right) >= 0;

    /// <summary>The position of a stored row.</summary>
    public static FeedPosition Of(WallChange row) => new(row.TxId, row.Seq);

    /// <summary>The later of two positions.</summary>
    public static FeedPosition Max(FeedPosition a, FeedPosition b) => a >= b ? a : b;

    public int CompareTo(FeedPosition other)
    {
        if (TxId is { } tx && other.TxId is { } otherTx && tx != otherTx)
        {
            return tx.CompareTo(otherTx);
        }

        return Seq.CompareTo(other.Seq);
    }
}
