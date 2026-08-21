using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The bitrate maths that aims a re-encode at a target file size. Kept pure and tested here so the
/// "scale a huge clip down toward ~500 MB" promise does not rest on ffmpeg being installed.
/// </summary>
public class FfmpegVideoTranscoderTests
{
    private const long AudioBits = 128_000;

    [Theory]
    [InlineData(60)]
    [InlineData(600)]
    [InlineData(1800)]
    public void ComputeVideoBitsPerSecond_LandsUnderTarget(double durationSeconds)
    {
        const long target = 500L * 1024 * 1024;
        var videoBits = FfmpegVideoTranscoder.ComputeVideoBitsPerSecond(durationSeconds, target, AudioBits);

        // Encoding at video + audio for the whole clip must not exceed the target (safety margin).
        var predictedBytes = (long)((videoBits + AudioBits) * durationSeconds / 8.0);
        Assert.True(predictedBytes <= target, $"predicted {predictedBytes} > target {target}");
        Assert.True(predictedBytes > target * 0.75, "should still use most of the budget");
    }

    [Fact]
    public void ComputeVideoBitsPerSecond_ClampsToAFloorForVeryLongClips()
    {
        // A multi-hour clip would compute an absurdly low bitrate; the floor keeps it usable.
        var videoBits = FfmpegVideoTranscoder.ComputeVideoBitsPerSecond(100_000, 500L * 1024 * 1024, AudioBits);
        Assert.Equal(300_000, videoBits);
    }

    [Fact]
    public void ComputeVideoBitsPerSecond_HandlesUnknownDuration()
    {
        Assert.Equal(300_000, FfmpegVideoTranscoder.ComputeVideoBitsPerSecond(0, 500L * 1024 * 1024, AudioBits));
    }
}
