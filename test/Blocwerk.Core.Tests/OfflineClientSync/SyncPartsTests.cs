// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Sync;

namespace Blocwerk.Core.Tests.OfflineClientSync;

/// <summary>The sync loop's parts: cursor order, the URL's wall and token, and the waits.</summary>
public sealed class SyncPartsTests
{
    private const string Epoch = "0f0e0d0c0b0a09080706050403020100";

    [Theory]
    [InlineData("E.10.5", "E.10.4", true)]
    [InlineData("E.10.5", "E.10.5", false)]
    [InlineData("E.9.99", "E.10.1", false)]
    [InlineData("E.11.1", "E.10.99", true)]
    [InlineData("E..7", "E..6", true)]
    [InlineData("E..6", "E..6", false)]
    [InlineData("E.10.5", "F.10.6", true)]
    [InlineData("garbage", "E.10.6", true)]
    [InlineData("E.10.5", null, true)]
    public void IsAfter_OrdersTxThenSeq_WithinOneEpoch_AndNeverSkipsWhatItCannotOrder(string candidate, string? current, bool after)
    {
        Assert.Equal(after, SyncCursor.IsAfter(Real(candidate)!, Real(current)));
    }

    [Fact]
    public void Earliest_IsTheOldestCursor_OrNullWhenOneIsUnknownOrTheEpochsDiffer()
    {
        Assert.Equal(Real("E.10.5"), SyncCursor.Earliest([Real("E.11.1"), Real("E.10.5"), Real("E.10.6")]));
        Assert.Null(SyncCursor.Earliest([Real("E.11.1"), null]));
        Assert.Null(SyncCursor.Earliest([Real("E.11.1"), Real("F.1.1")]));
        Assert.Null(SyncCursor.Earliest([]));
    }

    [Theory]
    [InlineData("https://b.test/v/walls/11111111-1111-1111-1111-111111111111", true, null)]
    [InlineData("https://b.test/v/walls/11111111-1111-1111-1111-111111111111/boulders/b1111111-1111-1111-1111-111111111111?x=1", true, null)]
    [InlineData("https://b.test/v/walls/11111111-1111-1111-1111-111111111111/shared/a%2Bb", true, "a+b")]
    [InlineData("https://b.test/v/walls/11111111-1111-1111-1111-111111111111?token=q%20r#top", true, "q r")]
    [InlineData("https://b.test/v/walls", false, null)]
    [InlineData("https://b.test/walls/11111111-1111-1111-1111-111111111111", false, null)]
    [InlineData("https://b.test/v/walls/not-a-guid", false, null)]
    [InlineData("/v/walls/11111111-1111-1111-1111-111111111111/shared/tok", true, "tok")]
    public void Target_IsTheWallOfAClientUrl_AndItsShareToken(string uri, bool wall, string? token)
    {
        var target = SyncTarget.Of(uri);

        Assert.Equal(wall, target.WallId is not null);
        Assert.Equal(token, target.Token);
    }

    [Fact]
    public void Delays_SpreadEveryWait()
    {
        Assert.Equal(TimeSpan.FromSeconds(4), SyncDelays.Network(1, 0));
        Assert.Equal(TimeSpan.FromSeconds(36), SyncDelays.Network(2, 1));
        Assert.Equal(TimeSpan.FromSeconds(120), SyncDelays.Network(9, 0.5));
        Assert.Equal(TimeSpan.FromSeconds(36), SyncDelays.RetryAfter(30, 1));
        Assert.Equal(TimeSpan.FromSeconds(5), SyncDelays.RetryAfter(null, 0));
        Assert.Equal(TimeSpan.FromSeconds(9.9), SyncDelays.Online(0.99));
    }

    private static string? Real(string? cursor) =>
        cursor?.Replace("E.", Epoch + ".", StringComparison.Ordinal).Replace("F.", "f" + Epoch[1..] + ".", StringComparison.Ordinal);
}
