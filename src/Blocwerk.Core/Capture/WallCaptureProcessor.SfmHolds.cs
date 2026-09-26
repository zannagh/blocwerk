// <copyright file="WallCaptureProcessor.SfmHolds.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Diagnostics;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.Proposals;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// The hold detections <c>solve-sfm</c> scores the wall's candidate planes with (the "hold hits" of the wall-facet
/// score: a plane the photos' holds land on is a climbing surface; the room, rafters and stray planes carry none).
/// The app's own tiled YOLO (<see cref="InAppCaptureHoldDetector"/>, the hold proposals' CPU detector) on every
/// capture photo, as pixel centres on the stored photo's grid (the grid the photo was reconstructed on). Cached per
/// stored photo, so a resumed capture does not detect twice. Without a detector the solve scores without holds.
/// </summary>
public sealed partial class WallCaptureProcessor
{
    /// <summary>The cache key's detector name: stored-grid pixels (the proposals cache camera-grid pixels).</summary>
    private const string SfmHoldsCacheKey = "yolo-cpu@stored";

    /// <summary>The solve request's limit of holds per photo (request.py MAX_HOLDS).</summary>
    private const int MaxHoldsPerPhoto = 2000;

    /// <summary>Photo index → hold centres [x, y] px; empty when there is no detector or nothing was found.</summary>
    private async Task<IReadOnlyDictionary<int, IReadOnlyList<double[]>>> PhotoHoldsAsync(
        Guid captureId, IReadOnlyList<WallCapturePhoto> photos, CancellationToken ct)
    {
        var holds = new Dictionary<int, IReadOnlyList<double[]>>();
        if (holdDetection is null || photos.Count == 0)
        {
            return holds;
        }

        await SetStageAsync(captureId, WallCaptureStatus.Solving, 0.55, "Finding the holds in the photos", ct);
        var detector = new InAppCaptureHoldDetector(holdDetection);
        var watch = Stopwatch.StartNew();
        foreach (var photo in photos)
        {
            ct.ThrowIfCancellationRequested();
            var found = await DetectPhotoAsync(detector, photo, ct);
            if (found.Count > 0)
            {
                holds[photo.Index] = found
                    .OrderByDescending(d => d.Confidence)
                    .Take(MaxHoldsPerPhoto)
                    .Select(d => new[] { Math.Round(d.Px, 1), Math.Round(d.Py, 1) })
                    .ToList();
            }
        }

        logger.LogInformation(
            "Capture {CaptureId}: {Holds} hold detections on {Photos}/{Total} photos for the wall-facet score, {Ms} ms",
            captureId, holds.Values.Sum(h => h.Count), holds.Count, photos.Count, watch.ElapsedMilliseconds);
        return holds;
    }

    private async Task<IReadOnlyList<CaptureDetection>> DetectPhotoAsync(
        InAppCaptureHoldDetector detector, WallCapturePhoto photo, CancellationToken ct)
    {
        if (CaptureDetectionCache.Get(photo.StoredPath, SfmHoldsCacheKey) is { } cached)
        {
            return cached;
        }

        try
        {
            if (await files.ReadAsync(photo.StoredPath, ct) is not { } bytes)
            {
                return [];
            }

            var found = await detector.DetectAsync(CaptureComputeDocuments.PhotoName(photo.Index), ImageMetadataStripper.Strip(bytes), ct);
            CaptureDetectionCache.Put(photo.StoredPath, SfmHoldsCacheKey, found);
            return found;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A photo the detector cannot read only loses its hold hits; the solve still runs.
            logger.LogWarning(ex, "Hold detection failed on capture photo {Index}", photo.Index);
            return [];
        }
    }
}
