// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The capture limits in one place: <see cref="WallCapturePipelineOptions.MaxPhotos"/> and
/// <see cref="WallCapturePipelineOptions.MaxVideoFrames"/> (<c>CAPTURE__MAXPHOTOS</c>, <c>CAPTURE__MAXVIDEOFRAMES</c>). The
/// compute workers read the same two values with the same ranges and derive their per-job caps and timeouts the same way
/// (<c>docker/compute-jobs-py/computejobs/capture_limits.py</c>; every number here is in its <c>capture_limits.json</c>, which
/// a test holds against this class): wall-geometry gets the photos (never the video frames), the splat worker the photos,
/// the frames and the anchor photos (at least <see cref="SplatFloorImages"/>), and every timeout sized for
/// <see cref="BaselineImages"/> images grows in proportion. A capture that will not fit (one imported from an instance with
/// higher limits, a worker configured below them) is refused before anything is sent.
/// </summary>
public static class CaptureLimits
{
    /// <summary>The fewest photos <c>CAPTURE__MAXPHOTOS</c> may be (a value outside the range keeps the default).</summary>
    public const int MinPhotos = 2;

    /// <summary>The most photos <c>CAPTURE__MAXPHOTOS</c> may be.</summary>
    public const int MaxPhotosCeiling = 1000;

    /// <summary>The fewest frames <c>CAPTURE__MAXVIDEOFRAMES</c> may be.</summary>
    public const int MinVideoFrames = 3;

    /// <summary>The most frames <c>CAPTURE__MAXVIDEOFRAMES</c> may be.</summary>
    public const int MaxVideoFramesCeiling = 400;

    /// <summary>The anchor photos a markerless capture's splat job carries on top of its own (<see cref="AnchorPhotoSelector"/>).</summary>
    public const int AnchorImages = AnchorPhotoSelector.DefaultCount;

    /// <summary>The image count the base timeouts are sized for.</summary>
    public const int BaselineImages = 200;

    /// <summary>The geometry worker's request size for <see cref="BaselineImages"/> photos, MB.</summary>
    public const int GeometryRequestBaseMb = 4096;

    /// <summary>The splat worker never takes fewer images per job than this (what it always took).</summary>
    public const int SplatFloorImages = 600;

    /// <summary>The geometry worker's images per job for the largest capture these options allow: the photos.</summary>
    public static int GeometryImages(WallCapturePipelineOptions options) => options.MaxPhotos;

    /// <summary>
    /// The geometry worker's request size for the largest capture these options allow (4 GB per 200 photos, as
    /// capture_limits.py sizes <c>MAX_REQUEST_MB</c>).
    /// </summary>
    public static long GeometryRequestBytes(WallCapturePipelineOptions options) =>
        (long)Math.Ceiling(GeometryRequestBaseMb * Math.Max(1.0, (double)GeometryImages(options) / BaselineImages)) * 1024 * 1024;

    /// <summary>The splat worker's images per job for the largest capture these options allow.</summary>
    public static int SplatImages(WallCapturePipelineOptions options) =>
        Math.Max(SplatFloorImages, options.MaxPhotos + options.MaxVideoFrames + AnchorImages);

    /// <summary>A timeout sized for <see cref="BaselineImages"/> images, grown in proportion for more (never below it).</summary>
    public static TimeSpan Scaled(TimeSpan timeout, int images) =>
        images <= BaselineImages ? timeout : TimeSpan.FromTicks((long)Math.Ceiling(timeout.Ticks * (double)images / BaselineImages));

    /// <summary>The worker's timeout growth: geometry by its photos, the splat worker against its 600-image floor.</summary>
    public static TimeSpan ScaledFor(Compute.ComputeServiceKind service, TimeSpan timeout, WallCapturePipelineOptions options) =>
        service == Compute.ComputeServiceKind.Splat
            ? Scaled(timeout, SplatImages(options) * BaselineImages / SplatFloorImages)
            : Scaled(timeout, GeometryImages(options));

    /// <summary>
    /// The frames a video yields once extracted: about its duration at the sampling rate, at most the frame limit (what
    /// <see cref="CaptureVideoFrameExtractor"/> takes). Unknown duration: none counted (the extraction caps them anyway).
    /// </summary>
    public static int ExpectedFrames(double? durationSeconds, WallCapturePipelineOptions options) =>
        durationSeconds is > 0 and { } seconds
            ? (int)Math.Min(options.MaxVideoFrames, Math.Ceiling(seconds * options.VideoFramesPerSecond))
            : 0;

    /// <summary>
    /// Why a capture of <paramref name="photos"/> photos and <paramref name="frames"/> video frames cannot go where
    /// <paramref name="fit"/> sends it (to workers that take <paramref name="geometryCap"/> / <paramref name="splatCap"/>
    /// images per job when known), or null when it fits. <paramref name="photoBytes"/> (the photos' stored size) is held against
    /// the 3D model service's request cap <paramref name="geometryRequestCap"/> (a textures request carries every photo).
    /// </summary>
    public static string? Refusal(
        int photos, int frames, WallCapturePipelineOptions options, CaptureFit fit, int? geometryCap = null, int? splatCap = null,
        long photoBytes = 0, long? geometryRequestCap = null)
    {
        if (photos > options.MaxPhotos || (fit.HasFlag(CaptureFit.Splat) && frames > options.MaxVideoFrames))
        {
            var (maxPhotos, maxFrames) = (options.MaxPhotos, options.MaxVideoFrames);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"This capture has {photos} photos and {frames} video frames; this server takes at most {maxPhotos} photos and {maxFrames} frames (CAPTURE__MAXPHOTOS, CAPTURE__MAXVIDEOFRAMES). Nothing was sent.");
        }

        if (fit.HasFlag(CaptureFit.Geometry) && geometryCap is { } geometry && photos > geometry)
        {
            return WorkerRefusal(photos, "3D model service", geometry);
        }

        if (fit.HasFlag(CaptureFit.Geometry) && geometryRequestCap is { } requestCap && photoBytes > requestCap)
        {
            var (mb, capMb) = (photoBytes >> 20, requestCap >> 20);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"This capture's photos are {mb} MB; the 3D model service takes at most {capMb} MB per request (its MAX_REQUEST_MB is set below the capture limits). Nothing was sent.");
        }

        var splatImages = photos + frames + AnchorImages;
        return fit.HasFlag(CaptureFit.Splat) && splatCap is { } splat && splatImages > splat
            ? WorkerRefusal(splatImages, "photo-real worker", splat)
            : null;
    }

    private static string WorkerRefusal(int images, string worker, int cap) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"This capture sends {images} images; the {worker} takes at most {cap} per job (its MAX_PHOTOS is set below the capture limits). Nothing was sent.");
}
