using Blocwerk.Core.Abstractions;
using Blocwerk.Core.MarkerPlanning;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Marker detection for capture photos: validated (every dictionary id with a plan, unplanned ones included — see
/// <see cref="CaptureUnplannedMarkers"/>; 0..35 without a plan —
/// duplicates rejected by the detector) and edge-refined corners, in PIXELS of the RAW grid (EXIF orientation ignored).
/// With a plan, every photo's markers are then checked against the planned layout (<see cref="CapturePlanLayoutCheck"/>).
/// Detections that are not printed markers (no quiet zone, or not fitting the plan) are stored flagged
/// (<see cref="CaptureMarker.Ignored"/>), never dropped silently, and never given to the solver.
/// </summary>
internal static class CaptureMarkerDetection
{
    /// <summary>The stored reason of a detection without a white border (a hold or shadow that decoded as an id).</summary>
    public const string NoQuietZoneReason = Geometry.WallGeometryRejectedObservation.NoQuietZone;

    /// <summary>Detects markers, or throws when detection is unavailable on this server.</summary>
    /// <param name="detector">The detector, when registered.</param>
    /// <param name="bytes">The stored photo.</param>
    /// <param name="layout">The capture's marker layout (plan or legacy).</param>
    /// <param name="focalPx">The photo's focal length in px, when known.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<List<CaptureMarker>> DetectAsync(
        IMarkerDetectionService? detector, byte[] bytes, WallMarkerLayout layout, double? focalPx, CancellationToken ct)
    {
        if (detector is null)
        {
            throw new CaptureFailedException("Marker detection is not available on this server.");
        }

        // The OpenCV detector is synchronous behind its Task (decode + ArUco + refinement of a photo up to
        // 20 MB); run it on the pool so an upload never blocks the caller's thread (a Blazor circuit).
        var result = await Task.Run(() => detector.DetectAsync(bytes, CaptureUnplannedMarkers.DetectionOptions(layout), ct), ct);
        var markers = result.Markers
            .Select(m => new CaptureMarker(m.Id, Corners(m.CornersPx), m.Synthetic, Math.Round(m.SidePx, 2)))
            .ToList();
        var verdict = CapturePlanLayoutCheck.Apply(layout, markers, focalPx);
        var noQuietZone = result.Rejected
            .Where(r => r.Reason == MarkerRejectionReason.NoQuietZone)
            .Select(r => new CaptureMarker(r.Id, Corners(r.CornersPx), false, Math.Round(r.SidePx, 2))
            {
                Ignored = NoQuietZoneReason,
                IgnoredDetail = r.Detail,
            });
        var planMisfits = verdict.Ignored.Select(i => i.Marker with
        {
            Ignored = CapturePlanLayoutCheck.Reason,
            IgnoredDetail = i.Detail,
        });
        return [.. verdict.Kept, .. planMisfits, .. noQuietZone];
    }

    /// <summary>
    /// Detection at upload time: null (retry in the pipeline) when no detector is registered or it
    /// failed on this image, so an upload is never lost to a detector hiccup.
    /// </summary>
    public static async Task<List<CaptureMarker>?> DetectOrNullAsync(
        IMarkerDetectionService? detector, byte[] bytes, WallMarkerLayout layout, double? focalPx, ILogger logger, CancellationToken ct)
    {
        if (detector is null)
        {
            logger.LogWarning("No marker detector is registered; capture photos are detected later in the pipeline");
            return null;
        }

        try
        {
            return await DetectAsync(detector, bytes, layout, focalPx, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Marker detection failed on an uploaded capture photo; the pipeline will retry");
            return null;
        }
    }

    private static double[][] Corners(IReadOnlyList<MarkerPoint> corners) =>
        corners.Select(c => new[] { Math.Round(c.X, 4), Math.Round(c.Y, 4) }).ToArray();
}
