// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The capture limits in one place: <see cref="WallCapturePipelineOptions.MaxPhotos"/> and
/// <see cref="WallCapturePipelineOptions.MaxVideoFrames"/> (<c>CAPTURE__MAXPHOTOS</c>, <c>CAPTURE__MAXVIDEOFRAMES</c>). The
/// compute workers read the same two values and derive their per-job caps and timeouts the same way
/// (<c>docker/compute-jobs-py/computejobs/capture_limits.py</c>, defaults in <c>capture_limits.json</c>, which a test holds
/// against these numbers): wall-geometry takes photos + frames per job, the splat worker that plus the anchor crops, and
/// every timeout sized for <see cref="BaselineImages"/> images grows in proportion. A capture that will not fit (one
/// imported from an instance with higher limits, a worker configured below them) is refused before anything is sent.
/// </summary>
public static class CaptureLimits
{
    /// <summary>The marker anchor crops a splat job carries on top of the photos and frames (at most).</summary>
    public const int AnchorImages = 32;

    /// <summary>The image count the base timeouts are sized for.</summary>
    public const int BaselineImages = 200;

    /// <summary>The geometry worker's images per job for the largest capture these options allow.</summary>
    public static int GeometryImages(WallCapturePipelineOptions options) => options.MaxPhotos + options.MaxVideoFrames;

    /// <summary>The splat worker's images per job for the largest capture these options allow.</summary>
    public static int SplatImages(WallCapturePipelineOptions options) => GeometryImages(options) + AnchorImages;

    /// <summary>A timeout sized for <see cref="BaselineImages"/> images, grown in proportion for more (never below it).</summary>
    public static TimeSpan Scaled(TimeSpan timeout, int images) =>
        images <= BaselineImages ? timeout : TimeSpan.FromTicks((long)Math.Ceiling(timeout.Ticks * (double)images / BaselineImages));

    /// <summary>
    /// Why a capture of <paramref name="photos"/> photos and <paramref name="frames"/> video frames cannot be sent (to a worker
    /// that takes <paramref name="geometryCap"/> / <paramref name="splatCap"/> images per job when known), or null when it fits.
    /// </summary>
    public static string? Refusal(int photos, int frames, WallCapturePipelineOptions options, int? geometryCap = null, int? splatCap = null)
    {
        if (photos > options.MaxPhotos || frames > options.MaxVideoFrames)
        {
            var (maxPhotos, maxFrames) = (options.MaxPhotos, options.MaxVideoFrames);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"This capture has {photos} photos and {frames} video frames; this server takes at most {maxPhotos} photos and {maxFrames} frames (CAPTURE__MAXPHOTOS, CAPTURE__MAXVIDEOFRAMES). Nothing was sent.");
        }

        var cap = new[] { (geometryCap, photos + frames, "3D model service"), (splatCap, photos + frames + AnchorImages, "photo-real worker") }
            .FirstOrDefault(c => c.Item1 is { } max && c.Item2 > max);
        if (cap.Item1 is not { } limit)
        {
            return null;
        }

        var (images, worker) = (photos + frames, cap.Item3);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"This capture has {images} images; the {worker} takes at most {limit} per job (its MAX_PHOTOS is set below the capture limits). Nothing was sent.");
    }
}
