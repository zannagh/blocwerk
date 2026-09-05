using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
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
    private static readonly byte[] Poster = [0xFF, 0xD8, 0xFF, 0xE0, 0x10, 0x20];

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
    public async Task Normalize_Transcodes_AnAlreadyWebSafeButOversizedClip()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h);
        var info = await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/mp4", null, "clip.mp4");

        // Already H.264/AAC, but a 4K frame (3840x2160): past the 720p remux cap, so the MP4 fallback
        // must be re-encoded down rather than copied through at source resolution.
        var transcoder = new FakeTranscoder { Probe = new VideoProbeResult(5, "h264", "aac", "yuv420p", true, 2160, 3840) };
        var outcome = await NewNormalizer(h, transcoder).ProcessAsync(info.Id, default);

        Assert.Equal(BetaVideoNormalizeOutcome.Ready, outcome);
        Assert.Equal(1, transcoder.TranscodeCalls);
        Assert.Equal(0, transcoder.RemuxCalls);
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
    public async Task Normalize_StoresTheServerSidePoster_IntoTheThumbnail()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h);
        var info = await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/quicktime", null, "clip.mov");

        var transcoder = new FakeTranscoder { Probe = new VideoProbeResult(5, "hevc", "aac", "yuv420p", true), Poster = Poster };
        var outcome = await NewNormalizer(h, transcoder).ProcessAsync(info.Id, default);

        Assert.Equal(BetaVideoNormalizeOutcome.Ready, outcome);
        Assert.Equal(1, transcoder.PosterCalls);

        var row = await RowAsync(h, info.Id);
        Assert.Equal(Poster, row.Thumbnail);
    }

    [Fact]
    public async Task Normalize_LeavesAnExistingThumbnailUntouched_WhenPosterGenerationReturnsNull()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h);
        // Uploaded with a browser-side poster; the server-side grab then fails (returns null).
        var info = await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/quicktime", Poster, "clip.mov");

        var transcoder = new FakeTranscoder { Probe = new VideoProbeResult(5, "hevc", "aac", "yuv420p", true), Poster = null };
        var outcome = await NewNormalizer(h, transcoder).ProcessAsync(info.Id, default);

        Assert.Equal(BetaVideoNormalizeOutcome.Ready, outcome);
        var row = await RowAsync(h, info.Id);
        Assert.Equal(Poster, row.Thumbnail);
    }

    [Fact]
    public async Task Normalize_StillReady_WhenPosterGenerationFails_AndNoThumbnailExisted()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h);
        var info = await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/quicktime", null, "clip.mov");

        var transcoder = new FakeTranscoder { Probe = new VideoProbeResult(5, "hevc", "aac", "yuv420p", true), Poster = null };
        var outcome = await NewNormalizer(h, transcoder).ProcessAsync(info.Id, default);

        // A missing poster never fails normalization; the clip is Ready and simply carries no thumbnail.
        Assert.Equal(BetaVideoNormalizeOutcome.Ready, outcome);
        var row = await RowAsync(h, info.Id);
        Assert.Equal(BetaVideoEncodingStatus.Ready, row.EncodingStatus);
        Assert.Null(row.Thumbnail);
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

    [Fact]
    public async Task Normalize_BuildsHls_AlongsideTheMp4_AndCommitsTheLadder()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h);
        var info = await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/quicktime", null, "clip.mov");

        var transcoder = new FakeTranscoder { Probe = new VideoProbeResult(5, "hevc", "aac", "yuv420p", true, 1080) };
        var outcome = await NewNormalizer(h, transcoder).ProcessAsync(info.Id, default);

        Assert.Equal(BetaVideoNormalizeOutcome.Ready, outcome);
        Assert.Equal(1, transcoder.HlsCalls);

        var row = await RowAsync(h, info.Id);
        Assert.True(row.HasHls);

        // The committed ladder is served through the SAME access gate as the byte route.
        var dir = await h.BetaVideoService.GetHlsDirectoryAsync(info.Id);
        Assert.NotNull(dir);
        Assert.True(File.Exists(Path.Combine(dir!, "master.m3u8")));
    }

    [Fact]
    public async Task Normalize_KeepsTheMp4Ready_WhenTheHlsLadderFails_WithoutSettingHasHls()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h);
        var info = await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/quicktime", null, "clip.mov");

        var transcoder = new FakeTranscoder { ThrowOnHls = true, Probe = new VideoProbeResult(5, "hevc", "aac", "yuv420p", true, 720) };
        var outcome = await NewNormalizer(h, transcoder).ProcessAsync(info.Id, default);

        // A failed ladder is no regression: the clip is still Ready on the MP4, HasHls stays false, and
        // no half-written ladder is committed — so the HLS route 404s and the player uses the MP4.
        Assert.Equal(BetaVideoNormalizeOutcome.Ready, outcome);
        var row = await RowAsync(h, info.Id);
        Assert.Equal(BetaVideoEncodingStatus.Ready, row.EncodingStatus);
        Assert.False(row.HasHls);
        Assert.Null(await h.BetaVideoService.GetHlsDirectoryAsync(info.Id));
        Assert.False(Directory.Exists(h.BetaStorage.GetHlsDirectory(info.Id)));

        // The MP4 fallback still serves.
        var served = await h.BetaVideoService.GetVideoContentAsync(info.Id);
        Assert.Equal(Encoded, served!.Data);
    }

    [Fact]
    public async Task GetHlsDirectory_IsWithheld_UntilReadyAndHasHls()
    {
        using var h = new WallTestHarness();
        var boulder = await SeedBoulderAsync(h);
        var info = await h.BetaVideoService.AddVideoAsync(boulder.Id, Clip, "video/mp4", null, "a.mp4");

        // Pending upload: no HLS even though nothing is wrong yet.
        Assert.Null(await h.BetaVideoService.GetHlsDirectoryAsync(info.Id));

        var transcoder = new FakeTranscoder { Probe = new VideoProbeResult(5, "h264", "aac", "yuv420p", true, 720) };
        await NewNormalizer(h, transcoder).ProcessAsync(info.Id, default);

        Assert.NotNull(await h.BetaVideoService.GetHlsDirectoryAsync(info.Id));
    }

    private static BetaVideoNormalizer NewNormalizer(
        WallTestHarness h, IVideoTranscoder transcoder, BlocwerkSettings? settings = null) =>
        new(h.RootContextFactory, h.BetaStorage, transcoder, settings ?? new BlocwerkSettings(),
            NullLogger<BetaVideoNormalizer>.Instance);

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

        public bool ThrowOnHls { get; set; }

        /// <summary>Poster bytes the fake returns; null models a failed (non-throwing) poster grab.</summary>
        public byte[]? Poster { get; set; } = [0x0A, 0x0B, 0x0C];

        public int RemuxCalls { get; private set; }

        public int TranscodeCalls { get; private set; }

        public int HlsCalls { get; private set; }

        public int PosterCalls { get; private set; }

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

        public async Task TranscodeHlsAsync(string inputPath, string outputDirectory, VideoProbeResult probe, CancellationToken cancellationToken)
        {
            HlsCalls++;
            if (ThrowOnHls)
            {
                throw new InvalidOperationException("ffmpeg HLS failed (fake).");
            }

            // Stand in for a real ladder: a master + one variant playlist + one segment so the commit and
            // the serving path have real files to move and resolve.
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "master.m3u8"), "#EXTM3U\nv0.m3u8\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "v0.m3u8"), "#EXTM3U\nv0_000.ts\n", cancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "v0_000.ts"), Encoded, cancellationToken);
        }

        public Task<byte[]?> ExtractPosterAsync(string normalizedMp4Path, double durationSeconds, CancellationToken cancellationToken)
        {
            PosterCalls++;
            return Task.FromResult(Poster);
        }

        private static async Task<VideoTranscodeResult> WriteAsync(string outputPath, CancellationToken ct)
        {
            await File.WriteAllBytesAsync(outputPath, Encoded, ct);
            return new VideoTranscodeResult(Encoded.Length, "video/mp4");
        }
    }
}
