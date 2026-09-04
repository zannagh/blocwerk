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
