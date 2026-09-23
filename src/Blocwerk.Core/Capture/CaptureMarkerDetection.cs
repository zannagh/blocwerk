using Blocwerk.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Marker detection for capture photos: validated (the wall's layout ids — 0..35 without a plan —
/// duplicates rejected by the detector) and edge-refined corners, in PIXELS of the RAW grid (EXIF orientation ignored).
/// </summary>
internal static class CaptureMarkerDetection
{
    /// <summary>Detects markers, or throws when detection is unavailable on this server.</summary>
    public static async Task<List<CaptureMarker>> DetectAsync(
        IMarkerDetectionService? detector, byte[] bytes, MarkerDetectionOptions options, CancellationToken ct)
    {
        if (detector is null)
        {
            throw new CaptureFailedException("Marker detection is not available on this server.");
        }

        // The OpenCV detector is synchronous behind its Task (decode + ArUco + refinement of a photo up to
        // 20 MB); run it on the pool so an upload never blocks the caller's thread (a Blazor circuit).
        var result = await Task.Run(() => detector.DetectAsync(bytes, options, ct), ct);
        return result.Markers
            .Select(m => new CaptureMarker(
                m.Id,
                m.CornersPx.Select(c => new[] { Math.Round(c.X, 4), Math.Round(c.Y, 4) }).ToArray(),
                m.Synthetic,
                Math.Round(m.SidePx, 2)))
            .ToList();
    }

    /// <summary>
    /// Detection at upload time: null (retry in the pipeline) when no detector is registered or it
    /// failed on this image, so an upload is never lost to a detector hiccup.
    /// </summary>
    public static async Task<List<CaptureMarker>?> DetectOrNullAsync(
        IMarkerDetectionService? detector, byte[] bytes, MarkerDetectionOptions options, ILogger logger, CancellationToken ct)
    {
        if (detector is null)
        {
            logger.LogWarning("No marker detector is registered; capture photos are detected later in the pipeline");
            return null;
        }

        try
        {
            return await DetectAsync(detector, bytes, options, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Marker detection failed on an uploaded capture photo; the pipeline will retry");
            return null;
        }
    }
}
