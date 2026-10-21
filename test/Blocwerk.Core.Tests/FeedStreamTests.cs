// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Web.Endpoints.Feed;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>GET /api/walls/{w}/feed/stream</c>: the gap replayed from <c>?after=</c> or <c>Last-Event-ID</c>, then the hub's live
/// deliveries, every event's id a cursor, nothing written twice, and a <c>bye</c> when the grant or the lifetime ends.
/// </summary>
public sealed class FeedStreamTests
{
    [Fact]
    public async Task ItReplaysTheGap_ThenRelaysLiveRows_EachWithItsCursorAsId()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        await using var run = FeedStreamRun.Open(f, "?after=" + await f.BeginningAsync());
        await run.Body.WaitUntilAsync(b => b.Events.Count == 4);

        await f.CommitAsync(true, f.Row("live"));
        await run.Body.WaitUntilAsync(b => b.Events.Count == 5);

        Assert.InRange(int.Parse(run.Body.Frames[0].Retry!, System.Globalization.CultureInfo.InvariantCulture), 2400, 3600);
        Assert.Equal(["wall", "members", "setters", "owner.mine", "live"], run.Body.Events.Select(e => e.Kind));
        var frames = run.Body.Frames.Where(x => x.Event is null && x.Data is not null).ToList();
        Assert.All(frames, x => Assert.Equal(JsonSerializer.Deserialize<FeedEnvelope>(x.Data!, FeedJson.Options)!.Cursor, x.Id));
    }

    [Fact]
    public async Task AReconnectWithLastEventId_ResumesRightAfterIt_AndWinsOverTheQuery()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        var beginning = await f.BeginningAsync();
        string resumeAt;
        await using (var first = FeedStreamRun.Open(f, "?after=" + beginning))
        {
            await first.Body.WaitUntilAsync(b => b.Events.Count == 4);
            resumeAt = first.Body.Events[1].Cursor;
        }

        await using var second = FeedStreamRun.Open(f, "?after=" + beginning, lastEventId: resumeAt);
        await second.Body.WaitUntilAsync(b => b.Events.Count >= 2);
        await f.CommitAsync(true, f.Row("live"));
        await second.Body.WaitUntilAsync(b => b.Events.Count == 3);

        Assert.Equal(["setters", "owner.mine", "live"], second.Body.Events.Select(e => e.Kind));
    }

    [Fact]
    public async Task WithoutACursor_ItOpensWithAResync_ThenStaysLive()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        await using var run = FeedStreamRun.Open(f);
        await run.Body.WaitUntilAsync(b => b.Frames.Any(x => x.Event == FeedStreamEvents.Resync));

        var resync = run.Body.Frames.Single(x => x.Event == FeedStreamEvents.Resync);
        Assert.True(JsonSerializer.Deserialize<FeedChanges>(resync.Data!, FeedJson.Options)!.Resync);
        Assert.Equal((await f.Feed.CurrentAsync()).ToString(), resync.Id);

        await f.CommitAsync(true, f.Row("live"));
        await run.Body.WaitUntilAsync(b => b.Events.Count == 1);
        Assert.Equal("live", run.Body.Events[0].Kind);
    }

    [Fact]
    public async Task ABacklogLongerThanTheReplay_EndsInAResync()
    {
        await using var f = new FeedRouteFixture(options: new FeedStreamOptions { PageSize = 2, MaxCatchUpPages = 1 });
        await f.StartAsync();
        await f.SeedAudiencesAsync();
        await using var run = FeedStreamRun.Open(f, "?after=" + await f.BeginningAsync());

        await run.Body.WaitUntilAsync(b => b.Frames.Any(x => x.Event == FeedStreamEvents.Resync));

        Assert.Equal(["wall", "members"], run.Body.Events.Select(e => e.Kind));
        Assert.Equal((await f.Feed.CurrentAsync()).ToString(), run.Body.Frames.Single(x => x.Event == FeedStreamEvents.Resync).Id);
    }

    /// <summary>
    /// The hub already tracks the wall at an older cursor, so its delivery repeats rows the replay just wrote: they are
    /// skipped, and only what follows is written.
    /// </summary>
    [Fact]
    public async Task RowsBothTheReplayAndTheHubDeliver_AreWrittenOnce()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        var early = new RecordingFeedSubscriber();
        using var tracked = await f.Hub.SubscribeAsync(f.WallId, FeedViewer.Anonymous(), early);
        await f.CommitAsync(false, f.Row("x"), f.Row("y"));

        await using var run = FeedStreamRun.Open(f, "?after=" + await f.BeginningAsync());
        await run.Body.WaitUntilAsync(b => b.Events.Count == 6);
        f.Signal.Wake(new FeedWake(f.WallId, null));
        await early.WaitForEventsAsync(2);
        await f.CommitAsync(true, f.Row("z"));
        await run.Body.WaitUntilAsync(b => b.Events.Any(e => e.Kind == "z"));

        Assert.Equal(["wall", "members", "setters", "owner.mine", "x", "y", "z"], run.Body.Events.Select(e => e.Kind));
    }

    [Fact]
    public async Task ItsOwnRevocation_EndsTheStream_AndNothingAfterItIsWritten()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        f.Caller = f.Member;
        await using var run = FeedStreamRun.Open(f, "?after=" + await f.BeginningAsync());
        await run.Body.WaitUntilAsync(b => b.Events.Count == 3);

        await f.CommitAsync(true, f.Row(FeedKinds.AccessRevoked, FeedAudience.User, f.Member.Id, entityId: f.WallId), f.Row("after"));
        await run.EndedAsync();

        Assert.Equal(FeedKinds.AccessRevoked, run.Body.Events[^1].Kind);
        AssertBye(run, FeedStreamEvents.ByeAccess);
    }

    /// <summary>
    /// W1: a revocation committed before the stream connects (the gate still granted: the membership row was not yet
    /// gone) arrives in the replay, and the hub's later copy is a repeat. Either way it ends the stream.
    /// </summary>
    [Fact]
    public async Task ARevocationInTheReplay_EndsTheStream_AndItsHubRepeatDoesNotSlipPast()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        f.Caller = f.Member;
        var early = new RecordingFeedSubscriber();
        using var tracked = await f.Hub.SubscribeAsync(f.WallId, FeedViewer.Anonymous(), early);
        await f.CommitAsync(false, f.Row(FeedKinds.AccessRevoked, FeedAudience.User, f.Member.Id, entityId: f.WallId), f.Row("after"));

        await using var run = FeedStreamRun.Open(f, "?after=" + await f.BeginningAsync());
        await run.EndedAsync();

        Assert.Equal(["wall", "members", "member.mine", FeedKinds.AccessRevoked], run.Body.Events.Select(e => e.Kind));
        AssertBye(run, FeedStreamEvents.ByeAccess);
    }

    /// <summary>N3: the rows after a <c>wall.updated</c> in the same batch go out only once access was decided again.</summary>
    [Fact]
    public async Task RowsAfterAnAccessChangeInOneBatch_AreNotWritten_WhenTheGrantIsGone()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        f.Caller = null;
        await using var run = FeedStreamRun.Open(f, $"?after={await f.BeginningAsync()}&token={FeedRouteFixture.ShareToken}");
        await run.Body.WaitUntilAsync(b => b.Events.Count == 1);
        await using (var db = f.Harness.CreateContext())
        {
            await db.Walls.Where(w => w.Id == f.WallId).ExecuteUpdateAsync(u => u.SetProperty(w => w.ShareToken, "rotated"));
        }

        await f.CommitAsync(true, f.Row(FeedKinds.WallUpdated, entityId: f.WallId), f.Row("secret"));
        await run.EndedAsync();

        Assert.Equal(["wall", FeedKinds.WallUpdated], run.Body.Events.Select(e => e.Kind));
        AssertBye(run, FeedStreamEvents.ByeAccess);
    }

    /// <summary>N1: a kiosk whose key is revoked mid-stream is cut off at the next access check.</summary>
    [Fact]
    public async Task AKioskStream_EndsWhenItsKeyIsRevoked()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        f.BindKiosk(f.WallId);
        f.Caller = null;
        await using var run = FeedStreamRun.Open(f, "?after=" + await f.BeginningAsync());
        await run.Body.WaitUntilAsync(b => b.Events.Count == 1);

        f.KioskKeys.IsKeyValidAsync(f.KioskKeyId, f.WallId, Arg.Any<CancellationToken>()).Returns(false);
        await f.CommitAsync(true, f.Row(FeedKinds.WallUpdated, entityId: f.WallId), f.Row("later"));
        await run.EndedAsync();

        Assert.DoesNotContain(run.Body.Events, e => e.Kind == "later");
        AssertBye(run, FeedStreamEvents.ByeAccess);
    }

    [Fact]
    public async Task APromotion_EndsTheStream_AnotherMembersChange_DoesNot()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        f.Caller = f.Member;
        await using var run = FeedStreamRun.Open(f, "?after=" + await f.BeginningAsync());
        await run.Body.WaitUntilAsync(b => b.Events.Count == 3);

        await f.CommitAsync(true, f.Row(FeedKinds.MemberChanged, FeedAudience.Members, entityId: f.Harness.Owner.Id), f.Row("still"));
        await run.Body.WaitUntilAsync(b => b.Events.Any(e => e.Kind == "still"));
        Assert.DoesNotContain(run.Body.Frames, x => x.Event == FeedStreamEvents.Bye);

        await using (var db = f.Harness.CreateContext())
        {
            await db.WallMembers.Where(m => m.UserId == f.Member.Id).ExecuteUpdateAsync(u => u.SetProperty(m => m.Role, WallRole.Admin));
        }

        await f.CommitAsync(true, f.Row(FeedKinds.MemberChanged, FeedAudience.Members, entityId: f.Member.Id));
        await run.EndedAsync();
        AssertBye(run, FeedStreamEvents.ByeAccess);
    }

    [Fact]
    public async Task ARotatedShareToken_EndsAnAnonymousStream()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        f.Caller = null;
        await using var run = FeedStreamRun.Open(f, $"?after={await f.BeginningAsync()}&token={FeedRouteFixture.ShareToken}");
        await run.Body.WaitUntilAsync(b => b.Events.Count == 1);

        await using (var db = f.Harness.CreateContext())
        {
            await db.Walls.Where(w => w.Id == f.WallId).ExecuteUpdateAsync(u => u.SetProperty(w => w.ShareToken, "rotated"));
        }

        await f.CommitAsync(true, f.Row(FeedKinds.WallUpdated, entityId: f.WallId));
        await run.EndedAsync();
        AssertBye(run, FeedStreamEvents.ByeAccess);
    }

    [Fact]
    public async Task AKioskStream_NeverCarriesTheActingMembersOwnRows()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        f.BindKiosk(f.WallId);
        f.Caller = f.Member;
        await using var run = FeedStreamRun.Open(f, "?after=" + await f.BeginningAsync());
        await run.Body.WaitUntilAsync(b => b.Events.Count == 1);

        await f.CommitAsync(true, f.Row(FeedKinds.TickMine, FeedAudience.User, f.Member.Id), f.Row("shared"));
        await run.Body.WaitUntilAsync(b => b.Events.Any(e => e.Kind == "shared"));

        Assert.Equal(["wall", "shared"], run.Body.Events.Select(e => e.Kind));
    }

    [Fact]
    public async Task AnIdleStream_SendsHeartbeats_AndSaysGoodbyeAtItsLifetime()
    {
        var options = new FeedStreamOptions { Lifetime = TimeSpan.FromSeconds(1), Heartbeat = TimeSpan.FromMilliseconds(150) };
        await using var f = new FeedRouteFixture(options: options);
        await f.StartAsync();
        await using var run = FeedStreamRun.Open(f, "?after=" + await f.BeginningAsync());

        await run.EndedAsync();

        Assert.Contains(run.Body.Frames, x => x.Comment == "ping");
        AssertBye(run, FeedStreamEvents.ByeLifetime);
        Assert.Equal(0, f.Limits.Open);
    }

    [Fact]
    public async Task TheAppStopping_EndsOpenStreams_WithAGoodbye()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        await using var run = FeedStreamRun.Open(f, "?after=" + await f.BeginningAsync());
        await run.Body.WaitUntilAsync(b => b.Events.Count == 4);

        await f.Stopping.CancelAsync();
        await run.EndedAsync();

        AssertBye(run, FeedStreamEvents.ByeLifetime);
    }

    [Fact]
    public async Task ARefusedStream_AnswersLikeTheCatchUp_AndTooManyStreamsIs429()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        var stream = FeedEndpoints.StreamRoute;

        f.Caller = null;
        FeedRouteFixture.AssertRefused(await f.GetAsync(stream, f.WallId), 401, FeedRefusals.Unauthenticated);
        f.Principal = FeedRouteFixture.SignedIn();
        FeedRouteFixture.AssertRefused(await f.GetAsync(stream, f.WallId), 410, FeedRefusals.AccountGone);
        f.Principal = null;
        f.Caller = f.Member;
        FeedRouteFixture.AssertRefused(await f.GetAsync(stream, f.OtherWallId), 403, FeedRefusals.NoAccess);

        var claim = new FeedStreamClaim(f.Member.Id, f.WallId, "unknown");
        var leases = Enumerable.Range(0, f.Options.MaxStreamsPerUser).Select(_ => f.Limits.TryAcquire(claim)!).ToList();
        try
        {
            var busy = await f.GetAsync(stream, f.WallId);
            Assert.Equal(StatusCodes.Status429TooManyRequests, busy.Response.StatusCode);
            Assert.Equal("30", busy.Response.Headers.RetryAfter.ToString());
        }
        finally
        {
            leases.ForEach(l => l.Dispose());
        }
    }

    private static void AssertBye(FeedStreamRun run, string reason)
    {
        var bye = run.Body.Frames[^1];
        Assert.Equal(FeedStreamEvents.Bye, bye.Event);
        Assert.Equal(reason, JsonSerializer.Deserialize<FeedStreamBye>(bye.Data!, FeedJson.Options)!.Reason);
    }
}
