using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Blocwerk.Core.Capture;

/// <summary>Timing and limits of the capture pipeline (tests shrink the delays).</summary>
public sealed class WallCapturePipelineOptions
{
    /// <summary>Default of <see cref="MaxPhotos"/>.</summary>
    public const int DefaultMaxPhotos = 200;

    /// <summary>
    /// Photos per capture; every step uses all of them (more views, better model, textures and hold shapes). Must not
    /// exceed the compute services' own per-job caps (wall-geometry's and the splat worker's <c>MAX_PHOTOS</c>,
    /// 200 / 600 by default), and wall-geometry's <c>MAX_REQUEST_MB</c> must fit them all in one textures request.
    /// A big wall shot with the main lens (24 mm) needs ~50 photos for the coverage 14 ultra-wide ones give, a careful
    /// walk along a large wall well over 100. Setting <c>Blocwerk:Capture:MaxPhotos</c> / <c>CAPTURE__MAXPHOTOS</c>
    /// (2–1000); default 200.
    /// </summary>
    public int MaxPhotos { get; init; } = DefaultMaxPhotos;

    /// <summary>
    /// Largest photo a capture takes, checked on upload and again after a HEIC → JPEG conversion (a 48 MP
    /// JPEG is 12–22 MB). Matches the compute services' <c>MAX_PHOTO_MB</c> (40). Setting
    /// <c>Blocwerk:Capture:MaxPhotoMb</c> / <c>CAPTURE__MAXPHOTOMB</c> (1–200); default 40 MB.
    /// </summary>
    public long MaxPhotoBytes { get; init; } = 40L * 1024 * 1024;

    /// <summary>
    /// JPEG quality of a HEIC upload's conversion. Setting <c>Blocwerk:Capture:HeicJpegQuality</c> /
    /// <c>CAPTURE__HEICJPEGQUALITY</c> (50–100); default 92.
    /// </summary>
    public int HeicJpegQuality { get; init; } = HeifCapturePhotoConverter.Quality;

    /// <summary>
    /// Video candidates per kept frame: the sharpest of each run of this many wins. Setting
    /// <c>Blocwerk:Capture:FrameSharpnessWindow</c> / <c>CAPTURE__FRAMESHARPNESSWINDOW</c> (1–10); default 3.
    /// </summary>
    public int FrameSharpnessWindow { get; init; } = CaptureVideoFrameExtractor.Window;

    /// <summary>
    /// ffmpeg's <c>-q:v</c> for the extracted frames (2 best … 31 worst). Setting
    /// <c>Blocwerk:Capture:FrameJpegQ</c> / <c>CAPTURE__FRAMEJPEGQ</c> (2–31); default 3.
    /// </summary>
    public int FrameJpegQ { get; init; } = CaptureVideoFrameExtractor.JpegQ;

    /// <summary>
    /// Long edge a frame's sharpness is scored at. Setting <c>Blocwerk:Capture:SharpnessEdge</c> /
    /// <c>CAPTURE__SHARPNESSEDGE</c> (120–1920); default 480.
    /// </summary>
    public int SharpnessEdge { get; init; } = CaptureFrameSharpness.ScoreEdge;

    /// <summary>
    /// Long edge a capture photo's sharpness is scored at on upload (larger than a frame's: a photo is judged on its
    /// own detail, not against its neighbours). Setting <c>Blocwerk:Capture:PhotoSharpnessEdge</c> /
    /// <c>CAPTURE__PHOTOSHARPNESSEDGE</c> (240–4096); default 1024.
    /// </summary>
    public int PhotoSharpnessEdge { get; init; } = 1024;

    /// <summary>
    /// A photo without any decoded marker is left out as blurry when its sharpness is below this share of the
    /// capture's sharp photos (<see cref="CaptureBlurFilter"/>). Setting <c>Blocwerk:Capture:BlurExcludeRatio</c> /
    /// <c>CAPTURE__BLUREXCLUDERATIO</c> (0–1, 0 = never); default 0.2.
    /// </summary>
    public double BlurExcludeRatio { get; init; } = CaptureBlurFilter.DefaultRatio;

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

    /// <summary>
    /// Retired models per wall that keep their wall textures and photo-real view, newest first, so the admin can activate
    /// the one before again with its 3D view (the active model's family and models of captures awaiting an admin's
    /// decision never count). Older ones keep their geometry only. Null keeps every model's files. Setting
    /// <c>Blocwerk:Capture:KeepSupersededModels</c> / <c>CAPTURE__KEEPSUPERSEDEDMODELS</c> (0–100, -1 = keep all); default 1.
    /// </summary>
    public int? KeepSupersededModels { get; init; } = 1;

    /// <summary>
    /// A retired model keeps its files at least this long after it was replaced, whatever <see cref="KeepSupersededModels"/>
    /// says. Setting <c>Blocwerk:Capture:SupersededModelGraceDays</c> / <c>CAPTURE__SUPERSEDEDMODELGRACEDAYS</c> (0–3650);
    /// default 14.
    /// </summary>
    public TimeSpan SupersededModelGrace { get; init; } = TimeSpan.FromDays(14);

    /// <summary>
    /// A 3D runner's trained result and prepared state (kept to finish the view again without training, or to export the
    /// capture for a replay) are deleted this long after the view was installed; the installed view stays. Null keeps
    /// them. Setting <c>Blocwerk:Capture:RunnerLeftoverRetentionDays</c> / <c>CAPTURE__RUNNERLEFTOVERRETENTIONDAYS</c>
    /// (0 = keep); default 30.
    /// </summary>
    public TimeSpan? RunnerLeftoverRetention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// A capture import (<c>captures/imports/{id}/</c>) nobody touched for this long is abandoned and deleted. Setting
    /// <c>Blocwerk:Capture:ImportStagingDays</c> / <c>CAPTURE__IMPORTSTAGINGDAYS</c> (1–365); default 3.
    /// </summary>
    public TimeSpan ImportStagingLifetime { get; init; } = TimeSpan.FromDays(3);

    /// <summary>
    /// The retention of retired models, runner leftovers and abandoned imports only logs what it would free. Setting
    /// <c>Blocwerk:Capture:RetentionDryRun</c> / <c>CAPTURE__RETENTIONDRYRUN</c> (true/false); default false.
    /// </summary>
    public bool RetentionDryRun { get; init; }

    /// <summary>How often <see cref="WallCaptureSweeper"/> runs.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>
    /// After this long idle, the re-solve and re-render workers queue again every capture still marked for them that no
    /// run is working on (a mark a failed run could not clear), so the wall is not left busy until a restart.
    /// </summary>
    public TimeSpan RedoRescanInterval { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>A stored file no row references is only deleted once it is at least this old (an upload in flight).</summary>
    public TimeSpan OrphanGrace { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Largest walk-along video a capture takes (streamed to disk, never held in memory). Setting
    /// <c>Blocwerk:Capture:MaxVideoMb</c> / <c>CAPTURE__MAXVIDEOMB</c> (1–16384); default 2048 MB.
    /// </summary>
    public long MaxVideoBytes { get; init; } = 2048L * 1024 * 1024;

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

    /// <summary>
    /// libheif's converter for HEIC uploads. Setting <c>Blocwerk:Capture:HeifConvertPath</c> /
    /// <c>CAPTURE__HEIFCONVERTPATH</c>; default <c>heif-convert</c> (on the PATH).
    /// </summary>
    public string HeifConvertPath { get; init; } = "heif-convert";

    /// <summary>The frame request of a capture video, with the extraction settings above.</summary>
    public CaptureVideoFrameRequest VideoFrameRequest() =>
        new(VideoFramesPerSecond, MaxVideoFrames, VideoExtractTimeout)
        {
            Window = FrameSharpnessWindow,
            JpegQ = FrameJpegQ,
            ScoreEdge = SharpnessEdge,
        };

    /// <summary>The defaults, with the retention, photo and video settings read from configuration when set.</summary>
    public static WallCapturePipelineOptions Bind(IConfiguration? configuration)
    {
        var defaults = new WallCapturePipelineOptions();
        var days = ReadInt(configuration, "PhotoRetentionDays", 0, int.MaxValue);
        var videoMb = ReadInt(configuration, "MaxVideoMb", 1, 16 * 1024);
        var photoMb = ReadInt(configuration, "MaxPhotoMb", 1, 200);
        var frames = ReadInt(configuration, "MaxVideoFrames", 3, 400);
        var fps = ReadDouble(configuration, "VideoFramesPerSecond", 0.1, 10) ?? defaults.VideoFramesPerSecond;
        return new WallCapturePipelineOptions
        {
            MaxPhotos = ReadInt(configuration, "MaxPhotos", 2, 1000) ?? defaults.MaxPhotos,
            PhotoRetention = days is { } d ? (d == 0 ? null : TimeSpan.FromDays(d)) : defaults.PhotoRetention,
            MaxVideoBytes = videoMb is { } mb ? mb * 1024L * 1024 : defaults.MaxVideoBytes,
            MaxPhotoBytes = photoMb is { } pmb ? pmb * 1024L * 1024 : defaults.MaxPhotoBytes,
            HeicJpegQuality = ReadInt(configuration, "HeicJpegQuality", 50, 100) ?? defaults.HeicJpegQuality,
            FrameSharpnessWindow = ReadInt(configuration, "FrameSharpnessWindow", 1, 10) ?? defaults.FrameSharpnessWindow,
            FrameJpegQ = ReadInt(configuration, "FrameJpegQ", 2, 31) ?? defaults.FrameJpegQ,
            SharpnessEdge = ReadInt(configuration, "SharpnessEdge", 120, 1920) ?? defaults.SharpnessEdge,
            PhotoSharpnessEdge = ReadInt(configuration, "PhotoSharpnessEdge", 240, 4096) ?? defaults.PhotoSharpnessEdge,
            BlurExcludeRatio = ReadDouble(configuration, "BlurExcludeRatio", 0, 1) ?? defaults.BlurExcludeRatio,
            MaxVideoFrames = frames ?? defaults.MaxVideoFrames,
            VideoFramesPerSecond = fps,
            HeifConvertPath = Read(configuration, "HeifConvertPath") is { Length: > 0 } heif ? heif : defaults.HeifConvertPath,
            KeepSupersededModels = ReadInt(configuration, "KeepSupersededModels", -1, 100) is { } keep
                ? (keep < 0 ? null : keep)
                : defaults.KeepSupersededModels,
            SupersededModelGrace = ReadDays(configuration, "SupersededModelGraceDays", 0, 3650) ?? defaults.SupersededModelGrace,
            RunnerLeftoverRetention = ReadInt(configuration, "RunnerLeftoverRetentionDays", 0, int.MaxValue) is { } leftover
                ? (leftover == 0 ? null : TimeSpan.FromDays(leftover))
                : defaults.RunnerLeftoverRetention,
            ImportStagingLifetime = ReadDays(configuration, "ImportStagingDays", 1, 365) ?? defaults.ImportStagingLifetime,
            RetentionDryRun = bool.TryParse(Read(configuration, "RetentionDryRun"), out var dryRun) && dryRun,
        };
    }

    private static TimeSpan? ReadDays(IConfiguration? configuration, string key, int min, int max) =>
        ReadInt(configuration, key, min, max) is { } days ? TimeSpan.FromDays(days) : null;

    private static string? Read(IConfiguration? configuration, string key) =>
        configuration?[$"Blocwerk:Capture:{key}"] ?? Environment.GetEnvironmentVariable($"CAPTURE__{key.ToUpperInvariant()}");

    private static double? ReadDouble(IConfiguration? configuration, string key, double min, double max) =>
        double.TryParse(Read(configuration, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && value >= min && value <= max
            ? value
            : null;

    private static int? ReadInt(IConfiguration? configuration, string key, int min, int max) =>
        int.TryParse(Read(configuration, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        && value >= min && value <= max
            ? value
            : null;
}
