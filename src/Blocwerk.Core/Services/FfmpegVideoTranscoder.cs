using System.Diagnostics;
using System.Globalization;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// ffmpeg-backed <see cref="IVideoTranscoder"/>. Probes the clip's duration, works out a video
/// bitrate that lands near the target size, and re-encodes to H.264/AAC mp4 with faststart so the
/// result streams from the first byte.
/// </summary>
public class FfmpegVideoTranscoder : IVideoTranscoder
{
    private const long AudioBitsPerSecond = 128_000;
    private const long MinVideoBitsPerSecond = 300_000;

    private readonly BlocwerkSettings settings;
    private readonly ILogger<FfmpegVideoTranscoder> logger;

    public FfmpegVideoTranscoder(BlocwerkSettings settings, ILogger<FfmpegVideoTranscoder> logger)
    {
        this.settings = settings;
        this.logger = logger;
    }

    /// <summary>
    /// Video bitrate (bits/s) so that video + audio over <paramref name="durationSeconds"/> lands
    /// near <paramref name="targetBytes"/>, with a safety margin for container overhead and rate
    /// control drift. Clamped to a sane floor so a very long clip still produces a usable file.
    /// </summary>
    public static long ComputeVideoBitsPerSecond(double durationSeconds, long targetBytes, long audioBitsPerSecond)
    {
        if (durationSeconds <= 0 || targetBytes <= 0)
        {
            return MinVideoBitsPerSecond;
        }

        const double safety = 0.92;
        var totalBits = targetBytes * 8.0 * safety;
        var videoBits = (totalBits / durationSeconds) - audioBitsPerSecond;
        return (long)Math.Max(MinVideoBitsPerSecond, videoBits);
    }

    public async Task<VideoTranscodeResult> ShrinkAsync(string inputPath, string outputPath, long targetBytes, CancellationToken cancellationToken)
    {
        var duration = await ProbeDurationSecondsAsync(inputPath, cancellationToken);
        var videoBits = ComputeVideoBitsPerSecond(duration, targetBytes, AudioBitsPerSecond);
        var kbps = Math.Max(1, videoBits / 1000);

        // Single-pass ABR with a capped max rate: fast enough for a request, close enough to target.
        var args = string.Join(' ',
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", Quote(inputPath),
            "-c:v", "libx264", "-preset", "veryfast",
            "-b:v", $"{kbps}k", "-maxrate", $"{kbps * 3 / 2}k", "-bufsize", $"{kbps * 2}k",
            "-vf", "scale='min(1280,iw)':-2",
            "-c:a", "aac", "-b:a", "128k",
            "-movflags", "+faststart",
            Quote(outputPath));

        logger.LogInformation(
            "Transcoding beta clip ({Duration:F0}s) toward {TargetMb} MB at {Kbps} kb/s video",
            duration, targetBytes / (1024 * 1024), kbps);

        await RunAsync(settings.BetaVideo.FfmpegPath, args, cancellationToken);

        if (!File.Exists(outputPath))
        {
            throw new InvalidOperationException("Transcoding produced no output file.");
        }

        return new VideoTranscodeResult(new FileInfo(outputPath).Length, "video/mp4");
    }

    private async Task<double> ProbeDurationSecondsAsync(string inputPath, CancellationToken cancellationToken)
    {
        var args = string.Join(' ',
            "-v", "error",
            "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1:nokey=1",
            Quote(inputPath));

        var output = await RunAsync(settings.BetaVideo.FfprobePath, args, cancellationToken);
        return double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : 0;
    }

    private async Task<string> RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not start '{fileName}'. Is ffmpeg installed in the runtime image?", ex);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'{fileName}' failed ({process.ExitCode}): {await stderr}");
        }

        return await stdout;
    }

    private static string Quote(string value) => $"\"{value}\"";
}
