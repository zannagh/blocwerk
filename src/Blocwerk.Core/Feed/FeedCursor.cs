// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;

namespace Blocwerk.Core.Feed;

/// <summary>
/// A client's place in the feed: the epoch plus a <see cref="FeedPosition"/>. Clients see it as the opaque string
/// <c>"{epoch}.{tx}.{seq}"</c> (<c>tx</c> is empty on SQLite). A cursor of another epoch means "resync".
/// </summary>
/// <param name="Epoch">The feed epoch the position belongs to.</param>
/// <param name="Position">The place within that epoch.</param>
public readonly record struct FeedCursor(Guid Epoch, FeedPosition Position)
{
    /// <summary>Parses the client form; false for anything malformed (the caller answers with a resync).</summary>
    public static bool TryParse(string? text, out FeedCursor cursor)
    {
        cursor = default;
        var parts = text?.Split('.');
        if (parts is not { Length: 3 } || !Guid.TryParseExact(parts[0], "N", out var epoch) || !TryParseTx(parts[1], out var tx)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seq))
        {
            return false;
        }

        cursor = new FeedCursor(epoch, new FeedPosition(tx, seq));
        return true;
    }

    /// <summary>The client form, <c>"{epoch}.{tx}.{seq}"</c>.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Epoch:N}.{Position.TxId}.{Position.Seq}");

    // SQLite cursors carry no transaction id (E5): an empty middle part parses to null and is ignored by the comparison.
    private static bool TryParseTx(string text, out long? tx)
    {
        tx = null;
        if (text.Length == 0)
        {
            return true;
        }

        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            tx = value;
            return true;
        }

        return false;
    }
}
