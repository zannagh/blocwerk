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
        var user = new FeedStreamClaim(Guid.NewGuid(), Guid.NewGuid(), "10.0.0.1");

        var a = limits.TryAcquire(user);
        var b = limits.TryAcquire(user);
        Assert.Null(limits.TryAcquire(user));
        var anonymous = limits.TryAcquire(new FeedStreamClaim(null, Guid.NewGuid(), "10.0.0.2"));
        Assert.Null(limits.TryAcquire(new FeedStreamClaim(null, Guid.NewGuid(), "10.0.0.3")));

        a!.Dispose();
        a.Dispose();
        Assert.Equal(2, limits.Open);
        Assert.NotNull(limits.TryAcquire(user));
        b!.Dispose();
        anonymous!.Dispose();
    }

    /// <summary>W2: anonymous streams are capped per address and per wall, signed-in ones only per user.</summary>
    [Fact]
    public void AnonymousStreams_AreCappedPerAddressAndPerWall_SignedInOnesAreNot()
    {
        var limits = new FeedStreamLimits(new FeedStreamOptions { MaxAnonymousPerAddress = 2, MaxAnonymousPerWall = 3 });
        var wall = Guid.NewGuid();

        Assert.NotNull(limits.TryAcquire(new FeedStreamClaim(null, wall, "a")));
        Assert.NotNull(limits.TryAcquire(new FeedStreamClaim(null, wall, "a")));
        Assert.Null(limits.TryAcquire(new FeedStreamClaim(null, wall, "a")));
        Assert.NotNull(limits.TryAcquire(new FeedStreamClaim(null, Guid.NewGuid(), "b")));
        Assert.NotNull(limits.TryAcquire(new FeedStreamClaim(null, wall, "c")));
        Assert.Null(limits.TryAcquire(new FeedStreamClaim(null, wall, "d")));
        Assert.NotNull(limits.TryAcquire(new FeedStreamClaim(Guid.NewGuid(), wall, "a")));
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    public void AnAddress_IsPartitionedByItself_OrByItsIpv6Slash64(string address, string partition)
    {
        Assert.Equal(partition, FeedAddress.PartitionOf(System.Net.IPAddress.Parse(address)));
    }

    [Fact]
    public void TheLifetimeAndRetry_AreSpreadWithinTheJitter()
    {
        var options = new FeedStreamOptions();
        var spreads = Enumerable.Range(0, 200).Select(_ => options.Spread(TimeSpan.FromMinutes(5)).TotalSeconds).ToList();

        Assert.All(spreads, s => Assert.InRange(s, 240, 360));
        Assert.True(spreads.Distinct().Count() > 1);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Heartbeat);
    }

    [Theory]
    [InlineData("/api/me/changes", true)]
    [InlineData("/api/walls/0123456789abcdef0123456789abcdef/changes", true)]
    [InlineData("/API/Walls/0123456789abcdef0123456789abcdef/feed/stream", true)]
    [InlineData("/api/walls/0123456789abcdef0123456789abcdef/a/sha", false)]
    [InlineData("/api/walls/not-a-guid/changes", false)]
    public void FeedPaths_AreRecognisedForTheChallengeBody(string path, bool feed)
    {
        Assert.Equal(feed, FeedEndpoints.IsFeedPath(new Microsoft.AspNetCore.Http.PathString(path)));
    }

    /// <summary>N5: the cookie challenge on a feed route answers the same refusal body the handler would.</summary>
    [Fact]
    public async Task ThePolicyChallenge_OnAFeedRoute_CarriesTheRefusalBody()
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Response.Body = new MemoryStream();

        await FeedRequestAccess.WriteChallengeAsync(http.Response);

        FeedRouteFixture.AssertRefused(http, 401, FeedRefusals.Unauthenticated);
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
