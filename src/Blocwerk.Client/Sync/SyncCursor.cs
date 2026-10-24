// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;

namespace Blocwerk.Client.Sync;

/// <summary>
/// The feed cursor <c>"{epoch}.{tx}.{seq}"</c> as the client compares it (the server's <c>FeedCursor</c>): within one
/// epoch <c>(tx, seq)</c> orders lexicographically, and a cursor without a transaction id (SQLite) orders by
/// <c>seq</c> alone. Anything the client cannot order (malformed, another epoch) is treated as new, never skipped.
/// </summary>
public static class SyncCursor
{
    /// <summary>True when an event at <paramref name="candidate"/> is not yet reflected at <paramref name="current"/>.</summary>
    /// <param name="candidate">The event's cursor.</param>
    /// <param name="current">The cursor applied so far, or null.</param>
    /// <returns>False only when both parse, share an epoch and the candidate is at or before the current one.</returns>
    public static bool IsAfter(string candidate, string? current)
    {
        if (!TryParse(candidate, out var a) || !TryParse(current, out var b) || a.Epoch != b.Epoch)
        {
            return true;
        }

        return Compare(a, b) > 0;
    }

    /// <summary>True when both cursors parse and belong to the same feed epoch.</summary>
    /// <param name="a">One cursor.</param>
    /// <param name="b">The other.</param>
    /// <returns>Whether they can be ordered.</returns>
    public static bool SameEpoch(string? a, string? b) => TryParse(a, out var x) && TryParse(b, out var y) && x.Epoch == y.Epoch;

    /// <summary>
    /// The earliest of several cursors (the documents' build cursors: replay from the oldest), or null when there is
    /// none, one does not parse, or they span epochs: the stream then opens without a cursor, which the server answers
    /// with a resync.
    /// </summary>
    /// <param name="cursors">The cursors.</param>
    /// <returns>The earliest, or null.</returns>
    public static string? Earliest(IEnumerable<string?> cursors)
    {
        string? earliest = null;
        (string Epoch, long? Tx, long Seq) best = default;
        foreach (var cursor in cursors)
        {
            if (!TryParse(cursor, out var parsed) || (earliest is not null && parsed.Epoch != best.Epoch))
            {
                return null;
            }

            if (earliest is null || Compare(parsed, best) < 0)
            {
                earliest = cursor;
                best = parsed;
            }
        }

        return earliest;
    }

    private static int Compare((string Epoch, long? Tx, long Seq) a, (string Epoch, long? Tx, long Seq) b)
    {
        if (a.Tx is { } ta && b.Tx is { } tb && ta != tb)
        {
            return ta.CompareTo(tb);
        }

        return a.Seq.CompareTo(b.Seq);
    }

    private static bool TryParse(string? text, out (string Epoch, long? Tx, long Seq) cursor)
    {
        cursor = default;
        var parts = text?.Split('.');
        if (parts is not { Length: 3 } || parts[0].Length == 0
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seq))
        {
            return false;
        }

        long? tx = null;
        if (parts[1].Length > 0)
        {
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            tx = value;
        }

        cursor = (parts[0], tx, seq);
        return true;
    }
}
