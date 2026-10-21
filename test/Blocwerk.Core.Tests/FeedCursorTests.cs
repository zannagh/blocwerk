// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed;

namespace Blocwerk.Core.Tests;

/// <summary>The feed cursor's client form and the position order, including SQLite's null transaction id.</summary>
public sealed class FeedCursorTests
{
    private static readonly Guid Epoch = Guid.Parse("0f8b6a1e-3c2d-4b5a-9e8f-7a6b5c4d3e2f");

    [Fact]
    public void Cursor_RoundTripsThroughItsClientForm()
    {
        var cursor = new FeedCursor(Epoch, new FeedPosition(812345678, 912345));

        Assert.Equal("0f8b6a1e3c2d4b5a9e8f7a6b5c4d3e2f.812345678.912345", cursor.ToString());
        Assert.True(FeedCursor.TryParse(cursor.ToString(), out var parsed));
        Assert.Equal(cursor, parsed);
    }

    [Fact]
    public void SqliteCursor_HasAnEmptyTransactionPart_AndParsesToANullTxId()
    {
        var cursor = new FeedCursor(Epoch, new FeedPosition(null, 42));

        Assert.Equal("0f8b6a1e3c2d4b5a9e8f7a6b5c4d3e2f..42", cursor.ToString());
        Assert.True(FeedCursor.TryParse(cursor.ToString(), out var parsed));
        Assert.Null(parsed.Position.TxId);
        Assert.Equal(42, parsed.Position.Seq);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0f8b6a1e3c2d4b5a9e8f7a6b5c4d3e2f.1")]
    [InlineData("not-a-guid.1.2")]
    [InlineData("0f8b6a1e3c2d4b5a9e8f7a6b5c4d3e2f.-1.2")]
    [InlineData("0f8b6a1e3c2d4b5a9e8f7a6b5c4d3e2f.1.x")]
    [InlineData("0f8b6a1e3c2d4b5a9e8f7a6b5c4d3e2f.1.2.3")]
    public void MalformedCursor_DoesNotParse(string? text) => Assert.False(FeedCursor.TryParse(text, out _));

    [Fact]
    public void Positions_CompareByTransactionFirst_ThenBySeq()
    {
        // The reviewer's interleaving: A (xid 100) took seq 50 after B (xid 101) took seq 49. A comes first.
        var a = new FeedPosition(100, 50);
        var b = new FeedPosition(101, 49);

        Assert.True(a < b);
        Assert.True(new FeedPosition(100, 49) < a);
        Assert.Equal(b, FeedPosition.Max(a, b));
    }

    [Fact]
    public void NullTransactionIds_AreIgnored_SeqAloneCounts()
    {
        Assert.True(new FeedPosition(null, 5) < new FeedPosition(null, 6));
        Assert.True(new FeedPosition(null, 7) > new FeedPosition(900, 6));
        Assert.Equal(0, new FeedPosition(null, 6).CompareTo(new FeedPosition(3, 6)));
    }
}
