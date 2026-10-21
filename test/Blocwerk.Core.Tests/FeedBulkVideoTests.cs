// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static Blocwerk.Core.Tests.FeedBulkKit;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The beta-video bulk sites (PR 1.3): the uploader's delete, the admin's re-encode queue and the normalizer's status
/// writes bypass the interceptor, and append their <c>video.*</c> rows in the write's transaction; the wake follows the
/// commit.
/// </summary>
public sealed class FeedBulkVideoTests
{
    private static readonly byte[] Clip = [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70];

    [Fact]
    public async Task DeletingAClip_WritesVideoDeleted_AndWakesTheWallAfterTheCommit()
    {
        using var h = new WallTestHarness();
        var signal = new RecordingFeedSignal();
        var (boulder, video) = await SeedClipAsync(h);

        await Service(h, signal).DeleteVideoAsync(video);

        var row = Assert.Single(await RowsAsync(h));
        Assert.Equal((FeedKinds.VideoDeleted, video, boulder, h.WallId), (row.Kind, row.EntityId!.Value, row.BoulderId!.Value, row.WallId!.Value));
        Assert.Equal(FeedAudience.Wall, row.Audience);
        Assert.Equal(boulder.ToString(), Field(row, "boulderId"));
        Assert.Equal(h.WallId, Assert.Single(signal.Woken).WallId);
    }

    [Fact]
    public async Task DeletingAClipOfADraft_TellsOnlyTheSetters()
    {
        using var h = new WallTestHarness();
        var (boulder, video) = await SeedClipAsync(h);
        await using (var db = h.CreateContext())
        {
            await db.Boulders.Where(b => b.Id == boulder).ExecuteUpdateAsync(s => s.SetProperty(b => b.IsDraft, true));
        }

        await Service(h, new RecordingFeedSignal()).DeleteVideoAsync(video);

        Assert.Equal(FeedAudience.Setters, Assert.Single(await RowsAsync(h)).Audience);
    }

    [Fact]
    public async Task ARefusedDelete_WritesNothing_AndWakesNothing()
    {
        using var h = new WallTestHarness();
        var signal = new RecordingFeedSignal();
        var (_, video) = await SeedClipAsync(h);
        h.ActingUser = await h.AddMemberAsync("other@test", WallRole.Member);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(h, signal).DeleteVideoAsync(video));

        Assert.Empty(await RowsAsync(h));
        Assert.Empty(signal.Woken);
    }

    [Fact]
    public async Task TheReencodeQueue_WritesVideoStatusPerQueuedClip()
    {
        using var h = new WallTestHarness();
        var (_, first) = await SeedClipAsync(h);
        var second = (await h.BetaVideoService.AddVideoAsync(await BoulderOfAsync(h, first), Clip, "video/mp4", null, "b.mp4")).Id;
        await using (var db = h.CreateContext())
        {
            await db.Users.Where(u => u.Id == h.Owner.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, IdentityRole.Admin));
            await db.BetaVideos.Where(v => v.Id == second).ExecuteUpdateAsync(s => s.SetProperty(v => v.EncodingStatus, BetaVideoEncodingStatus.Ready));
        }

        Assert.Equal(1, await Service(h, new RecordingFeedSignal()).RequestReencodeAsync(all: false));

        var row = Assert.Single(await RowsAsync(h));
        Assert.Equal((FeedKinds.VideoStatus, first), (row.Kind, row.EntityId!.Value));
        Assert.Equal(nameof(BetaVideoEncodingStatus.Pending), Field(row, "encodingStatus"));
    }

    // The other transaction moves the first clip out of the queue's predicate and holds its row. The queue's lookup waits for
    // it, judges the clip on its committed state, and narrows the write to what it reports, so no row describes a clip the
    // write skipped. Postgres only: SQLite has one writer at a time.
    [PostgresFact]
    public async Task TheReencodeQueue_ReportsOnlyTheClipsItChanged_WhenAnotherWriterHoldsOne_Postgres()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        var (boulder, first) = await SeedClipAsync(h);
        var second = (await h.BetaVideoService.AddVideoAsync(boulder, Clip, "video/mp4", null, "b.mp4")).Id;
        await ClearAsync(h);
        await using (var admin = h.CreateContext())
        {
            await admin.Users.Where(u => u.Id == h.Owner.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, IdentityRole.Admin));
        }

        await using var other = h.CreateContext();
        await using var held = await other.Database.BeginTransactionAsync();
        await other.BetaVideos.Where(v => v.Id == first).ExecuteUpdateAsync(s => s.SetProperty(v => v.EncodingStatus, BetaVideoEncodingStatus.Ready));
        var queue = Service(h, new RecordingFeedSignal()).RequestReencodeAsync(all: false);
        await Task.Delay(1000);
        Assert.False(queue.IsCompleted);
        await held.CommitAsync();

        Assert.Equal(1, await queue);
        var row = Assert.Single(await RowsAsync(h));
        Assert.Equal(second, row.EntityId);
    }

    [Fact]
    public async Task TheNormalizer_WritesProcessingThenReady()
    {
        using var h = new WallTestHarness();
        var signal = new RecordingFeedSignal();
        var (_, video) = await SeedClipAsync(h);

        Assert.Equal(BetaVideoNormalizeOutcome.Ready, await Normalizer(h, signal, new FeedProbeTranscoder()).ProcessAsync(video, default));

        var rows = await RowsAsync(h);
        Assert.All(rows, r => Assert.Equal((FeedKinds.VideoStatus, video), (r.Kind, r.EntityId!.Value)));
        Assert.Equal(["Processing", "Ready"], rows.Select(r => Field(r, "encodingStatus")));
        Assert.Equal("true", Field(rows[^1], "hasHls"));
        Assert.Equal(2, signal.Woken.Count);
    }

    [Fact]
    public async Task AFailedEncode_WritesProcessingThenFailed()
    {
        using var h = new WallTestHarness();
        var (_, video) = await SeedClipAsync(h);

        var outcome = await Normalizer(h, new RecordingFeedSignal(), new FeedProbeTranscoder { ThrowOnEncode = true }).ProcessAsync(video, default);

        Assert.Equal(BetaVideoNormalizeOutcome.Failed, outcome);
        Assert.Equal(["Processing", "Failed"], (await RowsAsync(h)).Select(r => Field(r, "encodingStatus")));
    }

    private static BetaVideoService Service(WallTestHarness h, RecordingFeedSignal signal) =>
        new(new FeedDbContextFactory(h, signal), h.CurrentUser, h.ActivityLog, h.BetaStorage, NullLogger<BetaVideoService>.Instance);

    private static BetaVideoNormalizer Normalizer(WallTestHarness h, RecordingFeedSignal signal, FeedProbeTranscoder transcoder) =>
        new(new FeedRootDbContextFactory(h, signal), h.BetaStorage, transcoder, new BlocwerkSettings(), NullLogger<BetaVideoNormalizer>.Instance);

    private static async Task<(Guid Boulder, Guid Video)> SeedClipAsync(WallTestHarness h)
    {
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);
        var video = await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/quicktime", null, "clip.mov");
        await ClearAsync(h);
        return (boulder.Id, video.Id);
    }

    private static async Task<Guid> BoulderOfAsync(WallTestHarness h, Guid video)
    {
        await using var db = h.CreateContext();
        return await db.BetaVideos.Where(v => v.Id == video).Select(v => v.BoulderId).SingleAsync();
    }
}
