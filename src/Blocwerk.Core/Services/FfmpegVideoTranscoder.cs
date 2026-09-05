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

    /// <summary>
    /// The largest DISPLAYED dimension (px) a clip may have and still qualify for the cheap <c>-c copy</c>
    /// remux. Anything bigger is transcoded down to the 720p fallback profile even when already H.264, so a
    /// 4K clip never rides through at source resolution. Matches the 720p (long-edge 1280) transcode cap.
    /// </summary>
    public const int MaxRemuxDisplayedDimension = 1280;

    /// <summary>
    /// Head-room over <see cref="Configuration.BetaVideoSettings.TargetVideoBitsPerSecond"/> the remux
    /// fast-path tolerates: a source estimated within 1.25× target is close enough to leave untouched;
    /// anything fatter is transcoded so the flaky-network MP4 fallback stays small.
    /// </summary>
    public const double RemuxBitrateMargin = 1.25;

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

    /// <summary>
    /// Whether a probed clip qualifies for the cheap <c>-c copy</c> remux instead of a full transcode.
    /// True only when it is <see cref="IsWebSafe">web-safe</see> AND already small enough to serve as the
    /// flaky-network MP4 fallback: its max DISPLAYED dimension (rotation-aware, so a rotated portrait
    /// clip's displayed height is its coded width) is ≤ <see cref="MaxRemuxDisplayedDimension"/>, AND its
    /// estimated total bitrate (<paramref name="sourceFileBytes"/> × 8 ÷ duration) is within
    /// <see cref="RemuxBitrateMargin"/>× <paramref name="targetVideoBitsPerSecond"/>. A missing/zero
    /// duration or unknown size makes the bitrate untrustworthy, so it is treated as OUT of bounds →
    /// transcode, the safe default that guarantees a capped fallback. Pure and static for ffmpeg-free
    /// testing, mirroring <see cref="IsWebSafe"/>.
    /// </summary>
    public static bool CanRemux(VideoProbeResult probe, long sourceFileBytes, long targetVideoBitsPerSecond)
    {
        if (!IsWebSafe(probe))
        {
            return false;
        }

        var displayedWidth = HlsLadderPlanner.DisplayedWidth(probe.Width, probe.Height, probe.RotationDegrees);
        var displayedHeight = HlsLadderPlanner.DisplayedHeight(probe.Width, probe.Height, probe.RotationDegrees);
        if (Math.Max(displayedWidth, displayedHeight) > MaxRemuxDisplayedDimension)
        {
            return false;
        }

        if (sourceFileBytes <= 0 || probe.DurationSeconds <= 0)
        {
            return false;
        }

        var estimatedBitsPerSecond = sourceFileBytes * 8.0 / probe.DurationSeconds;
        return estimatedBitsPerSecond <= targetVideoBitsPerSecond * RemuxBitrateMargin;
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
        var height = 0;
        var width = 0;
        var rotation = 0;

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                if (type == "video" && videoCodec is null)
                {
                    videoCodec = Lower(stream, "codec_name");
                    pixelFormat = Lower(stream, "pix_fmt");
                    height = ReadInt(stream, "height");
                    width = ReadInt(stream, "width");
                    rotation = ReadRotation(stream);
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

        return new VideoProbeResult(
            duration, videoCodec, audioCodec, pixelFormat, audioCodec is not null, height, width, rotation);
    }

    /// <summary>
    /// The clip's upright display rotation, normalized to 0/90/180/270 (in the legacy <c>rotate</c>-tag
    /// convention: the degrees to rotate the coded frame to display it right-side up), or
    /// <see cref="HlsLadderPlanner.UnhandledRotation"/> when a rotation is present but not a clean
    /// multiple of 90 (or unparseable). Prefers the display-matrix <c>side_data_list[].rotation</c>,
    /// whose sign is the OPPOSITE of the rotate tag (it reports <c>av_display_rotation_get</c>, and
    /// autorotate rotates by the negative of that), so it is negated; falls back to <c>tags.rotate</c>,
    /// which is already in the upright convention.
    /// </summary>
    private static int ReadRotation(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var sideData in list.EnumerateArray())
            {
                if (!sideData.TryGetProperty("rotation", out var r))
                {
                    continue;
                }

                return r.ValueKind == JsonValueKind.Number && r.TryGetDouble(out var deg)
                    ? NormalizeRotation(-deg)
                    : HlsLadderPlanner.UnhandledRotation;
            }
        }

        if (stream.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object
            && tags.TryGetProperty("rotate", out var rotate))
        {
            var raw = rotate.ValueKind == JsonValueKind.String ? rotate.GetString() : rotate.GetRawText();
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var deg)
                ? NormalizeRotation(deg)
                : HlsLadderPlanner.UnhandledRotation;
        }

        return 0;
    }

    /// <summary>
    /// Reduces a display-rotation angle (degrees) to 0/90/180/270, or
    /// <see cref="HlsLadderPlanner.UnhandledRotation"/> when it is not a clean multiple of 90.
    /// </summary>
    private static int NormalizeRotation(double theta)
    {
        var rounded = Math.Round(theta);
        if (Math.Abs(theta - rounded) > 0.5)
        {
            return HlsLadderPlanner.UnhandledRotation;
        }

        var normalized = ((int)rounded % 360 + 360) % 360;
        return normalized is 0 or 90 or 180 or 270 ? normalized : HlsLadderPlanner.UnhandledRotation;
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

    public async Task TranscodeHlsAsync(string inputPath, string outputDirectory, VideoProbeResult probe, CancellationToken cancellationToken)
    {
        // Safety net: a rotation we cannot confidently make upright must not become a sideways ladder.
        // Skipping HLS (throwing so the normalizer keeps HasHls false) leaves the clip on the correctly
        // auto-rotated MP4 fallback.
        if (!HlsLadderPlanner.IsSupportedRotation(probe.RotationDegrees))
        {
            logger.LogInformation(
                "Skipping the HLS ladder for a beta clip with an unhandled display rotation ({Rotation}); the auto-rotated MP4 is served instead.",
                probe.RotationDegrees);
            throw new InvalidOperationException(
                $"Unhandled display rotation ({probe.RotationDegrees}); HLS skipped in favour of the auto-rotated MP4.");
        }

        // The ladder caps to the DISPLAYED height (a 90/270 clip swaps the coded axes) and the pre-split
        // rotation puts the frame in that displayed orientation before scale=-2:H runs.
        var displayedHeight = HlsLadderPlanner.DisplayedHeight(probe.Width, probe.Height, probe.RotationDegrees);
        var rungs = HlsLadderPlanner.SelectRungs(settings.BetaVideo.HlsLadder, displayedHeight);
        if (rungs.Count == 0)
        {
            throw new InvalidOperationException("No HLS ladder rungs are configured.");
        }

        Directory.CreateDirectory(outputDirectory);
        var args = HlsLadderPlanner.BuildArguments(
            inputPath, outputDirectory, rungs, settings.BetaVideo.HlsSegmentSeconds, probe.HasAudio, probe.RotationDegrees);

        logger.LogInformation(
            "Building HLS ladder ({Rungs} rung(s), displayed {Height}p, rotation {Rotation}) for a beta clip",
            rungs.Count, displayedHeight, probe.RotationDegrees);
        await RunAsync(settings.BetaVideo.FfmpegPath, args, cancellationToken);

        var master = Path.Combine(outputDirectory, HlsLadderPlanner.MasterPlaylistName);
        if (!File.Exists(master))
        {
            throw new InvalidOperationException("ffmpeg produced no HLS master playlist.");
        }
    }

    public async Task<byte[]?> ExtractPosterAsync(string normalizedMp4Path, double durationSeconds, CancellationToken cancellationToken)
    {
        // The poster is best-effort: any failure (missing ffmpeg, a killed/timed-out grab, an unreadable
        // frame) returns null so the normalizer keeps the clip Ready with whatever thumbnail it already had.
        var posterPath = normalizedMp4Path + ".poster.jpg";
        try
        {
            var seek = PosterSeekSeconds(durationSeconds).ToString("0.###", CultureInfo.InvariantCulture);

            // Seek BEFORE -i (fast keyframe seek), one frame, scale to a 640px long edge preserving aspect
            // with even dims, JPEG at q:v 3. The source is the already-upright MP4, so no rotation handling.
            var args = string.Join(' ',
                "-y", "-hide_banner", "-loglevel", "error",
                "-ss", seek, "-i", Quote(normalizedMp4Path),
                "-frames:v", "1",
                "-vf", "scale=w=640:h=640:force_original_aspect_ratio=decrease:force_divisible_by=2",
                "-q:v", "3", "-f", "image2",
                Quote(posterPath));

            await RunAsync(settings.BetaVideo.FfmpegPath, args, cancellationToken);
            if (!File.Exists(posterPath))
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(posterPath, cancellationToken);
            return bytes.Length > 0 ? bytes : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not extract a poster frame for a beta clip; leaving the existing thumbnail.");
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(posterPath))
                {
                    File.Delete(posterPath);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not delete the temporary poster file {Path}.", posterPath);
            }
        }
    }

    /// <summary>
    /// The seek time for the poster grab: ~10% of duration, clamped to ≥1s and strictly &lt; duration, or 1s
    /// when the duration is unknown/zero. Pure so the clamp is testable without ffmpeg on the box.
    /// </summary>
    public static double PosterSeekSeconds(double durationSeconds)
    {
        if (durationSeconds <= 0)
        {
            return 1.0;
        }

        var upper = Math.Max(1.0, durationSeconds - 0.1);
        return Math.Clamp(durationSeconds * 0.10, 1.0, upper);
    }

    private static int ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;

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
