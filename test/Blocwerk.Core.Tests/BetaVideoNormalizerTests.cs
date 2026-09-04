using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The normalize pipeline and the admin re-encode query, exercised with a fake transcoder (ffmpeg
/// cannot run in a unit test). What is pinned down here is the status machine and the atomic file
/// swap, not the encoder itself.
/// </summary>
public class BetaVideoNormalizerTests
{
    private static readonly byte[] Clip = [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70];
    private static readonly byte[] Encoded = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06];

    [Fact]
    public async Task Normalize_TranscodesANonWebSafeClip_IntoAReadyMp4()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h);
        var info = await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/quicktime", null, "clip.mov");
        var oldStoragePath = await StoragePathAsync(h, info.Id);

        var transcoder = new FakeTranscoder { Probe = new VideoProbeResult(5, "hevc", "aac", "yuv420p", true) };
        var outcome = await NewNormalizer(h, transcoder).ProcessAsync(info.Id, default);

        Assert.Equal(BetaVideoNormalizeOutcome.Ready, outcome);
        Assert.Equal(1, transcoder.TranscodeCalls);
        Assert.Equal(0, transcoder.RemuxCalls);

        var row = await RowAsync(h, info.Id);
        Assert.Equal(BetaVideoEncodingStatus.Ready, row.EncodingStatus);
        Assert.Equal("video/mp4", row.ContentType);
        Assert.Equal(Encoded.Length, row.SizeBytes);
        Assert.NotNull(row.LastEncodedUtc);
        Assert.Null(row.EncodingError);
        Assert.Null(row.Data);
        Assert.NotNull(row.StoragePath);
        Assert.NotEqual(oldStoragePath, row.StoragePath);

        // Old rendition gone, new one on disk with the encoder's bytes.
        Assert.False(File.Exists(h.BetaStorage.ResolvePhysicalPath(oldStoragePath!)));
        var served = await h.BetaVideoService.GetVideoContentAsync(info.Id);
        Assert.Equal(Encoded, served!.Data);
    }

    [Fact]
    public async Task Normalize_OnlyRemuxes_AnAlreadyWebSafeClip()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h);
        var info = await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/mp4", null, "clip.mp4");

        var transcoder = new FakeTranscoder { Probe = new VideoProbeResult(5, "h264", "aac", "yuv420p", true) };
        var outcome = await NewNormalizer(h, transcoder).ProcessAsync(info.Id, default);

        Assert.Equal(BetaVideoNormalizeOutcome.Ready, outcome);
        Assert.Equal(1, transcoder.RemuxCalls);
        Assert.Equal(0, transcoder.TranscodeCalls);
        Assert.Equal(BetaVideoEncodingStatus.Ready, (await RowAsync(h, info.Id)).EncodingStatus);
    }

    [Fact]
    public async Task Normalize_MarksFailed_AndKeepsTheOriginal_WhenFfmpegFails()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h);
        var info = await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/quicktime", null, "clip.mov");
        var oldStoragePath = await StoragePathAsync(h, info.Id);

        var transcoder = new FakeTranscoder { ThrowOnEncode = true, Probe = new VideoProbeResult(5, "hevc", "aac", "yuv420p", true) };
        var outcome = await NewNormalizer(h, transcoder).ProcessAsync(info.Id, default);

        Assert.Equal(BetaVideoNormalizeOutcome.Failed, outcome);

        var row = await RowAsync(h, info.Id);
        Assert.Equal(BetaVideoEncodingStatus.Failed, row.EncodingStatus);
        Assert.NotNull(row.EncodingError);
        Assert.Equal(oldStoragePath, row.StoragePath);

        // The original clip is untouched and still serves.
        var served = await h.BetaVideoService.GetVideoContentAsync(info.Id);
        Assert.Equal(Clip, served!.Data);
    }

    [Fact]
    public async Task Normalize_HandlesALegacyByteaClip_MovingItToDisk()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h);

        Guid videoId;
        await using (var db = h.CreateContext())
        {
            var legacy = new BetaVideo
            {
                BoulderId = boulder.Id,
                UploadedByUserId = h.Owner.Id,
                ContentType = "video/quicktime",
                FileName = "legacy.mov",
                SizeBytes = Clip.Length,
                Data = Clip,
                StoragePath = null,
                EncodingStatus = BetaVideoEncodingStatus.Pending,
            };
            db.BetaVideos.Add(legacy);
            await db.SaveChangesAsync();
            videoId = legacy.Id;
        }

        var transcoder = new FakeTranscoder { Probe = new VideoProbeResult(5, "hevc", "aac", "yuv420p", true) };
        var outcome = await NewNormalizer(h, transcoder).ProcessAsync(videoId, default);

        Assert.Equal(BetaVideoNormalizeOutcome.Ready, outcome);
        var row = await RowAsync(h, videoId);
        Assert.Null(row.Data);
        Assert.NotNull(row.StoragePath);
        Assert.True(File.Exists(h.BetaStorage.ResolvePhysicalPath(row.StoragePath!)));
    }

    [Fact]
    public async Task RequestReencode_NotAll_QueuesOnlyNonReadyClips()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h);
        await MakeOwnerAdminAsync(h);
        var ready = await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/mp4", null, "a.mp4");
        var failed = await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/mp4", null, "b.mp4");

        await SetStatusAsync(h, ready.Id, BetaVideoEncodingStatus.Ready);
        await SetStatusAsync(h, failed.Id, BetaVideoEncodingStatus.Failed);

        var queued = await h.BetaVideoService.RequestReencodeAsync(all: false);

        Assert.Equal(1, queued);
        Assert.Equal(BetaVideoEncodingStatus.Ready, (await RowAsync(h, ready.Id)).EncodingStatus);
        Assert.Equal(BetaVideoEncodingStatus.Pending, (await RowAsync(h, failed.Id)).EncodingStatus);
    }

    [Fact]
    public async Task RequestReencode_LeavesAnInFlightProcessingClipAlone()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h);
        await MakeOwnerAdminAsync(h);
        var processing = await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/mp4", null, "a.mp4");
        var pending = await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/mp4", null, "b.mp4");
        await SetStatusAsync(h, processing.Id, BetaVideoEncodingStatus.Processing);

        // "all" must NOT flip a Processing row (that races the worker's MarkReady); a stranded
        // Processing row is re-picked by the boot-time reset instead.
        var queued = await h.BetaVideoService.RequestReencodeAsync(all: true);

        Assert.Equal(1, queued);
        Assert.Equal(BetaVideoEncodingStatus.Processing, (await RowAsync(h, processing.Id)).EncodingStatus);
        Assert.Equal(BetaVideoEncodingStatus.Pending, (await RowAsync(h, pending.Id)).EncodingStatus);
    }

    [Fact]
    public async Task RequestReencode_ThrowsForANonAdminCaller()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h);
        await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/mp4", null, "a.mp4");

        // The seeded Owner is a wall admin but NOT an installation admin, so the internal
        // AppAdmin assert must reject the installation-wide re-encode even though the page gate
        // is bypassed here.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => h.BetaVideoService.RequestReencodeAsync(all: true));
    }

    [Fact]
    public async Task GetVideoFile_ReportsPending_ForABrandNewUpload_SoTheEndpointCanWithholdIt()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h);
        var info = await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/mp4", null, "a.mp4");

        var file = await h.BetaVideoService.GetVideoFileAsync(info.Id);

        Assert.NotNull(file);
        Assert.Equal(BetaVideoEncodingStatus.Pending, file!.EncodingStatus);
    }

    [Fact]
    public async Task GetVideoFile_ReportsFailed_AndStillServesTheOriginal()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h);
        var info = await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/quicktime", null, "clip.mov");

        var transcoder = new FakeTranscoder { ThrowOnEncode = true, Probe = new VideoProbeResult(5, "hevc", "aac", "yuv420p", true) };
        await NewNormalizer(h, transcoder).ProcessAsync(info.Id, default);

        // Failed reports its status (so the endpoint serves it as a fallback) and still resolves the
        // original file — a clip that played before is never made unplayable.
        var file = await h.BetaVideoService.GetVideoFileAsync(info.Id);
        Assert.NotNull(file);
        Assert.Equal(BetaVideoEncodingStatus.Failed, file!.EncodingStatus);
        var served = await h.BetaVideoService.GetVideoContentAsync(info.Id);
        Assert.Equal(Clip, served!.Data);
    }

    [Fact]
    public async Task RequestReencode_All_ForcesEveryClipBackToPending()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h);
        await MakeOwnerAdminAsync(h);
        var one = await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/mp4", null, "a.mp4");
        var two = await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/mp4", null, "b.mp4");
        await SetStatusAsync(h, one.Id, BetaVideoEncodingStatus.Ready);
        await SetStatusAsync(h, two.Id, BetaVideoEncodingStatus.Ready);

        var queued = await h.BetaVideoService.RequestReencodeAsync(all: true);

        Assert.Equal(2, queued);
        Assert.Equal(BetaVideoEncodingStatus.Pending, (await RowAsync(h, one.Id)).EncodingStatus);
        Assert.Equal(BetaVideoEncodingStatus.Pending, (await RowAsync(h, two.Id)).EncodingStatus);
    }

    private static BetaVideoNormalizer NewNormalizer(WallTestHarness h, IVideoTranscoder transcoder) =>
        new(h.RootContextFactory, h.BetaStorage, transcoder, NullLogger<BetaVideoNormalizer>.Instance);

    private static async Task<Boulder> SeedBoulderAsync(WallTestHarness h)
    {
        var holds = await h.SeedWallAsync(holdCount: 1);
        return await h.BoulderService.CreateBoulderAsync(
            h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);
    }

    private static async Task<BetaVideo> RowAsync(WallTestHarness h, Guid videoId)
    {
        await using var db = h.CreateContext();
        return await db.BetaVideos.AsNoTracking().FirstAsync(v => v.Id == videoId);
    }

    private static async Task<string?> StoragePathAsync(WallTestHarness h, Guid videoId) =>
        (await RowAsync(h, videoId)).StoragePath;

    private static async Task SetStatusAsync(WallTestHarness h, Guid videoId, BetaVideoEncodingStatus status)
    {
        await using var db = h.CreateContext();
        await db.BetaVideos.Where(v => v.Id == videoId)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.EncodingStatus, status));
    }

    /// <summary>Promotes the harness Owner to an installation admin so the AppAdmin-gated calls pass.</summary>
    private static async Task MakeOwnerAdminAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        await db.Users.Where(u => u.Id == h.Owner.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, IdentityRole.Admin));
    }

    private sealed class FakeTranscoder : IVideoTranscoder
    {
        public VideoProbeResult Probe { get; set; } = new(5, "hevc", "aac", "yuv420p", true);

        public bool ThrowOnEncode { get; set; }

        public int RemuxCalls { get; private set; }

        public int TranscodeCalls { get; private set; }

        public Task<VideoProbeResult> ProbeAsync(string inputPath, CancellationToken cancellationToken) =>
            Task.FromResult(Probe);

        public async Task<VideoTranscodeResult> RemuxAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
        {
            RemuxCalls++;
            return await WriteAsync(outputPath, cancellationToken);
        }

        public async Task<VideoTranscodeResult> TranscodeAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
        {
            TranscodeCalls++;
            if (ThrowOnEncode)
            {
                throw new InvalidOperationException("ffmpeg failed (fake).");
            }

            return await WriteAsync(outputPath, cancellationToken);
        }

        private static async Task<VideoTranscodeResult> WriteAsync(string outputPath, CancellationToken ct)
        {
            await File.WriteAllBytesAsync(outputPath, Encoded, ct);
            return new VideoTranscodeResult(Encoded.Length, "video/mp4");
        }
    }
}
