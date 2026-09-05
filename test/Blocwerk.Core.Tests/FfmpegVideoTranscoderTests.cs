using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The remux-vs-transcode decision and ffprobe parsing. Kept pure and tested here so the "already
/// web-safe clips are only remuxed, everything else is re-encoded" promise does not rest on ffmpeg
/// being installed on the box.
/// </summary>
public class FfmpegVideoTranscoderTests
{
    [Fact]
    public void IsWebSafe_H264Aac8Bit_IsRemuxable()
    {
        var probe = new VideoProbeResult(12.5, "h264", "aac", "yuv420p", HasAudio: true);
        Assert.True(FfmpegVideoTranscoder.IsWebSafe(probe));
    }

    [Fact]
    public void IsWebSafe_H264NoAudio_IsRemuxable()
    {
        var probe = new VideoProbeResult(4, "h264", null, "yuv420p", HasAudio: false);
        Assert.True(FfmpegVideoTranscoder.IsWebSafe(probe));
    }

    [Fact]
    public void IsWebSafe_Hevc_MustTranscode()
    {
        // The main real-world case: an HEVC/H.265 phone clip that many browsers cannot decode.
        var probe = new VideoProbeResult(8, "hevc", "aac", "yuv420p", HasAudio: true);
        Assert.False(FfmpegVideoTranscoder.IsWebSafe(probe));
    }

    [Fact]
    public void IsWebSafe_H264But10Bit_MustTranscode()
    {
        // H.264 in a 10-bit pixel format still needs a re-encode down to yuv420p to play everywhere.
        var probe = new VideoProbeResult(8, "h264", "aac", "yuv420p10le", HasAudio: true);
        Assert.False(FfmpegVideoTranscoder.IsWebSafe(probe));
    }

    [Fact]
    public void IsWebSafe_H264WithNonAacAudio_MustTranscode()
    {
        var probe = new VideoProbeResult(8, "h264", "pcm_s16le", "yuv420p", HasAudio: true);
        Assert.False(FfmpegVideoTranscoder.IsWebSafe(probe));
    }

    // The remux fast-path (-c copy) is gated on size, not just codec, so a 4K/high-bitrate clip that
    // merely happens to be H.264 is still transcoded down to the 720p MP4 fallback. Default target is
    // 3 Mbps, so the 1.25x margin threshold is 3.75 Mbps.
    private const long Target = 3_000_000;

    [Fact]
    public void CanRemux_AlreadyH264But4KHighBitrate_MustTranscode()
    {
        // 3840x2160, ~25 Mbps (88 MB / ~28 s). Web-safe codec, but far past the 720p cap → transcode.
        var probe = new VideoProbeResult(28, "h264", "aac", "yuv420p", HasAudio: true, Height: 2160, Width: 3840);
        Assert.False(FfmpegVideoTranscoder.CanRemux(probe, sourceFileBytes: 88L * 1024 * 1024, Target));
    }

    [Fact]
    public void CanRemux_H264720pWithinTargetBitrate_IsRemuxable()
    {
        // 1280x720, ~2 Mbps (2.5 MB / 10 s): web-safe, within the dimension cap and under 1.25x target.
        var probe = new VideoProbeResult(10, "h264", "aac", "yuv420p", HasAudio: true, Height: 720, Width: 1280);
        Assert.True(FfmpegVideoTranscoder.CanRemux(probe, sourceFileBytes: 2_500_000, Target));
    }

    [Fact]
    public void CanRemux_H264720pOverBitrate_MustTranscode()
    {
        // 1280x720 but ~4.8 Mbps (6 MB / 10 s), over the 3.75 Mbps margin → transcode to shrink it.
        var probe = new VideoProbeResult(10, "h264", "aac", "yuv420p", HasAudio: true, Height: 720, Width: 1280);
        Assert.False(FfmpegVideoTranscoder.CanRemux(probe, sourceFileBytes: 6_000_000, Target));
    }

    [Fact]
    public void CanRemux_MissingDurationOrSize_MustTranscode()
    {
        // The bitrate cannot be estimated without both, so the safe default is a (size-capping) transcode.
        var probe = new VideoProbeResult(0, "h264", "aac", "yuv420p", HasAudio: true, Height: 720, Width: 1280);
        Assert.False(FfmpegVideoTranscoder.CanRemux(probe, sourceFileBytes: 2_500_000, Target));

        var sized = new VideoProbeResult(10, "h264", "aac", "yuv420p", HasAudio: true, Height: 720, Width: 1280);
        Assert.False(FfmpegVideoTranscoder.CanRemux(sized, sourceFileBytes: 0, Target));
    }

    [Fact]
    public void CanRemux_RotatedPortraitH264_DisplayedMaxDimOver1280_MustTranscode()
    {
        // Coded 1920x1080 rotated 90°: displayed 1080x1920, so the displayed long edge is 1920 > 1280.
        // Web-safe and low bitrate, yet the displayed size alone forces a transcode.
        var probe = new VideoProbeResult(10, "h264", "aac", "yuv420p", HasAudio: true, Height: 1080, Width: 1920, RotationDegrees: 90);
        Assert.False(FfmpegVideoTranscoder.CanRemux(probe, sourceFileBytes: 2_500_000, Target));
    }

    [Fact]
    public void ParseProbe_ReadsCodecsPixelFormatAndDuration()
    {
        const string json = """
        {
            "streams": [
                { "codec_type": "video", "codec_name": "hevc", "pix_fmt": "yuv420p" },
                { "codec_type": "audio", "codec_name": "aac" }
            ],
            "format": { "duration": "9.531000" }
        }
        """;

        var probe = FfmpegVideoTranscoder.ParseProbe(json);

        Assert.Equal("hevc", probe.VideoCodec);
        Assert.Equal("aac", probe.AudioCodec);
        Assert.Equal("yuv420p", probe.PixelFormat);
        Assert.True(probe.HasAudio);
        Assert.Equal(9.531, probe.DurationSeconds, precision: 3);
    }

    [Fact]
    public void ParseProbe_ReadsWidthAndNoRotation_WhenNoneIsPresent()
    {
        const string json = """
        {
            "streams": [ { "codec_type": "video", "codec_name": "h264", "pix_fmt": "yuv420p", "width": 1920, "height": 1080 } ],
            "format": { "duration": "3.0" }
        }
        """;

        var probe = FfmpegVideoTranscoder.ParseProbe(json);

        Assert.Equal(1920, probe.Width);
        Assert.Equal(1080, probe.Height);
        Assert.Equal(0, probe.RotationDegrees);
    }

    [Fact]
    public void ParseProbe_NegatesTheDisplayMatrixRotation_ToTheUprightConvention()
    {
        // A typical portrait phone clip: coded 1920x1080, display-matrix rotation -90 (== rotate tag 90).
        // The upright angle we store is +90 (negated), which maps to transpose=1 on the ladder.
        const string json = """
        {
            "streams": [ {
                "codec_type": "video", "codec_name": "hevc", "pix_fmt": "yuv420p", "width": 1920, "height": 1080,
                "side_data_list": [ { "side_data_type": "Display Matrix", "rotation": -90 } ]
            } ],
            "format": { "duration": "3.0" }
        }
        """;

        var probe = FfmpegVideoTranscoder.ParseProbe(json);

        Assert.Equal(90, probe.RotationDegrees);
        Assert.True(HlsLadderPlanner.IsSupportedRotation(probe.RotationDegrees));
    }

    [Fact]
    public void ParseProbe_FallsBackToTheRotateTag_WhenNoSideData()
    {
        const string json = """
        {
            "streams": [ {
                "codec_type": "video", "codec_name": "h264", "pix_fmt": "yuv420p", "width": 1080, "height": 1920,
                "tags": { "rotate": "270" }
            } ],
            "format": { "duration": "3.0" }
        }
        """;

        var probe = FfmpegVideoTranscoder.ParseProbe(json);

        Assert.Equal(270, probe.RotationDegrees);
    }

    [Fact]
    public void ParseProbe_MarksAnOddRotationUnhandled_SoTheLadderIsSkipped()
    {
        const string json = """
        {
            "streams": [ {
                "codec_type": "video", "codec_name": "h264", "pix_fmt": "yuv420p", "width": 1920, "height": 1080,
                "side_data_list": [ { "side_data_type": "Display Matrix", "rotation": 45 } ]
            } ],
            "format": { "duration": "3.0" }
        }
        """;

        var probe = FfmpegVideoTranscoder.ParseProbe(json);

        Assert.Equal(HlsLadderPlanner.UnhandledRotation, probe.RotationDegrees);
        Assert.False(HlsLadderPlanner.IsSupportedRotation(probe.RotationDegrees));
    }

    [Fact]
    public void ParseProbe_VideoOnly_ReportsNoAudio()
    {
        const string json = """
        {
            "streams": [ { "codec_type": "video", "codec_name": "h264", "pix_fmt": "yuv420p" } ],
            "format": { "duration": "3.0" }
        }
        """;

        var probe = FfmpegVideoTranscoder.ParseProbe(json);

        Assert.False(probe.HasAudio);
        Assert.Null(probe.AudioCodec);
        Assert.True(FfmpegVideoTranscoder.IsWebSafe(probe));
    }
}
