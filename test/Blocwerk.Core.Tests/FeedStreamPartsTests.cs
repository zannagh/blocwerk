// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Feed;
using Blocwerk.Web.Endpoints.Feed;

namespace Blocwerk.Core.Tests;

/// <summary>The stream's backpressure and caps on their own: the bounded queue never blocks the hub, the limits count.</summary>
public sealed class FeedStreamPartsTests
{
    [Fact]
    public async Task AFullQueue_ClosesInsteadOfBlockingTheHub_AndKeepsWhatItHeld()
    {
        var queue = new FeedStreamQueue(capacity: 1);
        var delivery = new FeedDelivery(Guid.NewGuid(), [], new FeedCursor(Guid.NewGuid(), FeedPosition.Start), Resync: false);

        queue.OnFeed(delivery);
        queue.OnFeed(delivery);

        Assert.True(queue.Overflowed);
        Assert.True(queue.Reader.TryRead(out _));
        Assert.False(await queue.Reader.WaitToReadAsync());
    }

    [Fact]
    public void TheLimits_CapTheProcessAndEachUser_AndReleaseOnce()
    {
        var limits = new FeedStreamLimits(new FeedStreamOptions { MaxStreams = 3, MaxStreamsPerUser = 2 });
        var user = Guid.NewGuid();

        var a = limits.TryAcquire(user);
        var b = limits.TryAcquire(user);
        Assert.Null(limits.TryAcquire(user));
        var anonymous = limits.TryAcquire(null);
        Assert.Null(limits.TryAcquire(null));

        a!.Dispose();
        a.Dispose();
        Assert.Equal(2, limits.Open);
        Assert.NotNull(limits.TryAcquire(user));
        b!.Dispose();
        anonymous!.Dispose();
    }

    /// <summary>0 = none, 1 = decide again, 2 = revoked (<c>FeedStreamCheck</c> is internal to the web project).</summary>
    [Theory]
    [InlineData(FeedKinds.AccessRevoked, false, 2)]
    [InlineData(FeedKinds.MemberChanged, true, 1)]
    [InlineData(FeedKinds.MemberChanged, false, 0)]
    [InlineData(FeedKinds.WallUpdated, false, 1)]
    [InlineData(FeedKinds.WallResync, false, 1)]
    [InlineData(FeedKinds.CommentAdded, false, 0)]
    public void LiveEvents_AreClassifiedByWhatTheyMeanForTheGrant(string kind, bool aboutMe, int expected)
    {
        var me = Guid.NewGuid();
        var envelope = new FeedEnvelope { Cursor = "c", Kind = kind, EntityId = aboutMe ? me : Guid.NewGuid() };

        Assert.Equal((FeedStreamCheck)expected, FeedStreamChecks.Of(envelope, FeedViewer.Member(me)));
    }
}
