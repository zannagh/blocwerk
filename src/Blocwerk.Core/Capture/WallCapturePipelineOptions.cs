using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Blocwerk.Core.Capture;

/// <summary>Timing and limits of the capture pipeline (tests shrink the delays).</summary>
public sealed class WallCapturePipelineOptions
{
    public const int MaxPhotos = 40;

    public const long MaxPhotoBytes = 20L * 1024 * 1024;

    /// <summary>First wait between two job-status polls; grows by half each time.</summary>
    public TimeSpan PollInitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan PollMaxDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Consecutive "unreachable / busy" answers tolerated while polling before giving up.</summary>
    public int MaxTransientErrors { get; init; } = 12;

    /// <summary>How often a capture may be (re)started, restarts included, before it is failed.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Drafts nobody submitted are removed after this.</summary>
    public TimeSpan DraftLifetime { get; init; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Photos of a finished or failed capture are deleted this long after it ended — except those of
    /// the capture that produced the wall's ACTIVE model. Null keeps them forever. Setting
    /// <c>Blocwerk:Capture:PhotoRetentionDays</c> / <c>CAPTURE__PHOTORETENTIONDAYS</c> (0 = keep).
    /// </summary>
    public TimeSpan? PhotoRetention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>How often <see cref="WallCaptureSweeper"/> runs.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>A stored file no row references is only deleted once it is at least this old (an upload in flight).</summary>
    public TimeSpan OrphanGrace { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Largest walk-along video a capture takes (streamed to disk, never held in memory). Setting
    /// <c>Blocwerk:Capture:MaxVideoMb</c> / <c>CAPTURE__MAXVIDEOMB</c>; default 1024 MB.
    /// </summary>
    public long MaxVideoBytes { get; init; } = 1024L * 1024 * 1024;

    /// <summary>
    /// Most frames taken from the video for the photo-real view (they never count against
    /// <see cref="MaxPhotos"/>). Setting <c>Blocwerk:Capture:MaxVideoFrames</c> / <c>CAPTURE__MAXVIDEOFRAMES</c>; default 120.
    /// </summary>
    public int MaxVideoFrames { get; init; } = 120;

    /// <summary>
    /// Target frames per second of video (the sharpest frame of each window wins); lowered when the
    /// video is long enough to hit <see cref="MaxVideoFrames"/>. Setting
    /// <c>Blocwerk:Capture:VideoFramesPerSecond</c> / <c>CAPTURE__VIDEOFRAMESPERSECOND</c>; default 2.5.
    /// </summary>
    public double VideoFramesPerSecond { get; init; } = 2.5;

    /// <summary>Ceiling for one ffmpeg run of the frame extraction; the process tree is killed beyond it.</summary>
    public TimeSpan VideoExtractTimeout { get; init; } = TimeSpan.FromMinutes(20);

    /// <summary>The defaults, with the retention and video settings read from configuration when set.</summary>
    public static WallCapturePipelineOptions Bind(IConfiguration? configuration)
    {
        var defaults = new WallCapturePipelineOptions();
        var days = ReadInt(configuration, "PhotoRetentionDays", 0, int.MaxValue);
        var videoMb = ReadInt(configuration, "MaxVideoMb", 1, 16 * 1024);
        var frames = ReadInt(configuration, "MaxVideoFrames", 3, 400);
        var fps = Read(configuration, "VideoFramesPerSecond") is { } rawFps
                  && double.TryParse(rawFps, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f is >= 0.1 and <= 10
            ? f
            : defaults.VideoFramesPerSecond;
        return new WallCapturePipelineOptions
        {
            PhotoRetention = days is { } d ? (d == 0 ? null : TimeSpan.FromDays(d)) : defaults.PhotoRetention,
            MaxVideoBytes = videoMb is { } mb ? mb * 1024L * 1024 : defaults.MaxVideoBytes,
            MaxVideoFrames = frames ?? defaults.MaxVideoFrames,
            VideoFramesPerSecond = fps,
        };
    }

    private static string? Read(IConfiguration? configuration, string key) =>
        configuration?[$"Blocwerk:Capture:{key}"] ?? Environment.GetEnvironmentVariable($"CAPTURE__{key.ToUpperInvariant()}");

    private static int? ReadInt(IConfiguration? configuration, string key, int min, int max) =>
        int.TryParse(Read(configuration, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        && value >= min && value <= max
            ? value
            : null;
}
