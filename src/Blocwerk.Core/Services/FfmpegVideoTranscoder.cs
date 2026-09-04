using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// ffmpeg/ffprobe-backed <see cref="IVideoTranscoder"/>. Probes a clip, remuxes it when it is already
/// web-safe, and otherwise re-encodes to the maximally compatible H.264/AAC MP4 profile (yuv420p,
/// AAC-LC, faststart, ~720p cap) that plays on the kiosk Chromium and every mainstream browser/OS.
/// </summary>
public class FfmpegVideoTranscoder : IVideoTranscoder
{
    /// <summary>Video codecs that browsers decode natively, so only a container remux is needed.</summary>
    private static readonly HashSet<string> WebSafeVideoCodecs = new(StringComparer.OrdinalIgnoreCase) { "h264" };

    /// <summary>Audio codecs safe to keep on the copy path (or no audio at all).</summary>
    private static readonly HashSet<string> WebSafeAudioCodecs = new(StringComparer.OrdinalIgnoreCase) { "aac", "mp3" };

    /// <summary>8-bit 4:2:0 pixel formats. A 10-bit H.264 clip still needs a re-encode to yuv420p.</summary>
    private static readonly HashSet<string> WebSafePixelFormats = new(StringComparer.OrdinalIgnoreCase) { "yuv420p", "yuvj420p" };

    private readonly BlocwerkSettings settings;
    private readonly ILogger<FfmpegVideoTranscoder> logger;

    public FfmpegVideoTranscoder(BlocwerkSettings settings, ILogger<FfmpegVideoTranscoder> logger)
    {
        this.settings = settings;
        this.logger = logger;
    }

    /// <summary>
    /// Whether a probed clip can be served with a plain remux: an H.264 video stream in an 8-bit
    /// 4:2:0 pixel format, with either AAC/MP3 audio or no audio at all. Pure and static so the
    /// remux-vs-transcode decision is testable without ffmpeg on the box.
    /// </summary>
    public static bool IsWebSafe(VideoProbeResult probe)
    {
        if (probe.VideoCodec is null || !WebSafeVideoCodecs.Contains(probe.VideoCodec))
        {
            return false;
        }

        if (probe.PixelFormat is not null && !WebSafePixelFormats.Contains(probe.PixelFormat))
        {
            return false;
        }

        return !probe.HasAudio || (probe.AudioCodec is not null && WebSafeAudioCodecs.Contains(probe.AudioCodec));
    }

    public async Task<VideoProbeResult> ProbeAsync(string inputPath, CancellationToken cancellationToken)
    {
        var args = string.Join(' ',
            "-v", "error",
            "-print_format", "json",
            "-show_format", "-show_streams",
            Quote(inputPath));

        var output = await RunAsync(settings.BetaVideo.FfprobePath, args, cancellationToken);
        return ParseProbe(output);
    }

    /// <summary>Parses ffprobe's JSON into a <see cref="VideoProbeResult"/>. Static for direct testing.</summary>
    public static VideoProbeResult ParseProbe(string ffprobeJson)
    {
        using var doc = JsonDocument.Parse(ffprobeJson);
        var root = doc.RootElement;

        string? videoCodec = null;
        string? audioCodec = null;
        string? pixelFormat = null;

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                if (type == "video" && videoCodec is null)
                {
                    videoCodec = Lower(stream, "codec_name");
                    pixelFormat = Lower(stream, "pix_fmt");
                }
                else if (type == "audio" && audioCodec is null)
                {
                    audioCodec = Lower(stream, "codec_name");
                }
            }
        }

        var duration = 0.0;
        if (root.TryGetProperty("format", out var format)
            && format.TryGetProperty("duration", out var d)
            && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            duration = seconds;
        }

        return new VideoProbeResult(duration, videoCodec, audioCodec, pixelFormat, audioCodec is not null);
    }

    public async Task<VideoTranscodeResult> RemuxAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        var args = string.Join(' ',
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", Quote(inputPath),
            "-c", "copy", "-movflags", "+faststart",
            Quote(outputPath));

        logger.LogInformation("Remuxing already-web-safe beta clip into faststart MP4");
        await RunAsync(settings.BetaVideo.FfmpegPath, args, cancellationToken);
        return Result(outputPath);
    }

    public async Task<VideoTranscodeResult> TranscodeAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        var kbps = Math.Max(1, settings.BetaVideo.TargetVideoBitsPerSecond / 1000);

        // Single-pass ABR with a capped max rate; H.264 High/4.1 in yuv420p + AAC-LC stereo, capped at
        // 720p (long edge 1280). ffmpeg auto-rotates by the display matrix, so portrait phone clips
        // come out upright without any rotation metadata the browser would have to honour.
        var args = string.Join(' ',
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", Quote(inputPath),
            "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "high", "-level", "4.1",
            "-pix_fmt", "yuv420p",
            "-b:v", $"{kbps}k", "-maxrate", $"{kbps * 3 / 2}k", "-bufsize", $"{kbps * 2}k",
            "-vf", "scale=w=1280:h=1280:force_original_aspect_ratio=decrease:force_divisible_by=2",
            "-c:a", "aac", "-b:a", "128k", "-ac", "2",
            "-movflags", "+faststart",
            Quote(outputPath));

        logger.LogInformation("Transcoding beta clip to web-safe H.264/AAC at {Kbps} kb/s video", kbps);
        await RunAsync(settings.BetaVideo.FfmpegPath, args, cancellationToken);
        return Result(outputPath);
    }

    private static VideoTranscodeResult Result(string outputPath)
    {
        if (!File.Exists(outputPath))
        {
            throw new InvalidOperationException("ffmpeg produced no output file.");
        }

        return new VideoTranscodeResult(new FileInfo(outputPath).Length, "video/mp4");
    }

    private static string? Lower(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.GetString()?.ToLowerInvariant() : null;

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

        // A bounded per-invocation timeout linked to the caller's token: a clip that makes ffmpeg hang
        // must not block the single normalizer worker forever. On expiry the linked token cancels,
        // WaitForExitAsync throws, and the process is killed below so ffmpeg actually dies.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = settings.BetaVideo.EncodeTimeout;
        if (timeout > TimeSpan.Zero)
        {
            timeoutCts.CancelAfter(timeout);
        }

        var token = timeoutCts.Token;

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

        // Best-effort: run the encode below the app's own threads so a backfill cannot starve request
        // handling. Not supported on every host/container, so a failure here is ignored.
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not lower ffmpeg process priority; continuing at normal priority.");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            // Kill the process tree so ffmpeg dies rather than lingering after we stop awaiting it.
            TryKill(process, fileName);

            // A real shutdown (the caller's token) propagates so the clip is left Processing and
            // re-picked on boot. A timeout is surfaced as a FAILURE so the clip goes to the failure
            // path (marked Failed, original kept) instead of hanging the worker.
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new InvalidOperationException(
                $"'{fileName}' exceeded the {timeout.TotalSeconds:0}s encode timeout and was killed.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'{fileName}' failed ({process.ExitCode}): {await stderr}");
        }

        return await stdout;
    }

    private void TryKill(Process process, string fileName)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not kill '{FileName}' after a timeout/cancel.", fileName);
        }
    }

    private static string Quote(string value) => $"\"{value}\"";
}
