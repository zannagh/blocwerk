// <copyright file="CaptureBlurFilter.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Which capture photos are clearly too blurry to use (a phone on the wrong settings, a shaky hand). Conservative on
/// purpose: a photo is left out only when its sharpness (<see cref="WallCapturePhoto.Sharpness"/>) is below
/// <c>ratio</c> × the sharpness of the capture's sharp photos (its <see cref="ReferencePercentile"/> percentile) AND
/// no usable marker was decoded in it — a photo whose markers still decode is still measured by the marker solve.
/// The sharpness is relative to the capture, so the scene and the lens cancel out; a capture with fewer than
/// <see cref="MinScoredPhotos"/> scored photos is not judged.
/// </summary>
public static class CaptureBlurFilter
{
    /// <summary>Scored photos a capture needs before any is judged blurry.</summary>
    public const int MinScoredPhotos = 5;

    /// <summary>The percentile of the capture's sharpness that stands for "a sharp photo of this capture".</summary>
    public const double ReferencePercentile = 0.75;

    /// <summary>Default of <see cref="WallCapturePipelineOptions.BlurExcludeRatio"/>.</summary>
    public const double DefaultRatio = 0.2;

    /// <summary>The indexes of the photos to leave out; empty when <paramref name="ratio"/> is 0 (off).</summary>
    /// <param name="photos">The capture's photos, detected and scored.</param>
    /// <param name="ratio">Share of the reference sharpness below which a photo without markers is left out.</param>
    public static IReadOnlySet<int> Blurry(IReadOnlyCollection<WallCapturePhoto> photos, double ratio)
    {
        var scores = photos.Where(p => p.Sharpness is > 0).Select(p => p.Sharpness!.Value).Order().ToList();
        if (ratio <= 0 || scores.Count < MinScoredPhotos)
        {
            return new HashSet<int>();
        }

        var threshold = ratio * scores[(int)Math.Floor(ReferencePercentile * (scores.Count - 1))];
        return photos
            .Where(p => p.Sharpness is { } s && s < threshold && p.MarkersJson is not null && !HasUsableMarker(p))
            .Select(p => p.Index)
            .ToHashSet();
    }

    private static bool HasUsableMarker(WallCapturePhoto photo) =>
        CaptureComputeDocuments.ParseMarkers(photo.MarkersJson).Any(m => m.Ignored is null);
}
