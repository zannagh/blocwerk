// <copyright file="MarkerQuietZoneCheck.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Markers;

/// <summary>
/// "Is this a dark square on light paper?" A printed marker's black border is surrounded by its white
/// quiet zone; a dark climbing hold that happened to decode as an id (a real false id 17 on The Attic)
/// is surrounded by more of itself, its shadow or the wood. Measured on the refined quad, sampled in the
/// marker's own frame (unit square, the black border is its outer 1/6):
/// <c>contrast = (P25(ring just outside) − mean(border)) / (P90(interior) − mean(border))</c>.
/// On 319 real detections of 67 wall photos the lowest contrast was −0.09 (a marker at the frame edge),
/// the false id 17 scored −1.19; <see cref="MinContrast"/> sits between.
/// </summary>
internal static class MarkerQuietZoneCheck
{
    /// <summary>Candidates with a lower contrast are rejected.</summary>
    public const double MinContrast = -0.5;

    /// <summary>Sampling step in the unit-square marker frame (60 samples per side).</summary>
    private const double Step = 1.0 / 60;

    /// <summary>Splits <paramref name="markers"/> into kept ones and ones without a quiet zone.</summary>
    public static (IReadOnlyList<DetectedMarker> Kept, IReadOnlyList<RejectedMarkerCandidate> Rejected) Filter(
        Mat gray, IReadOnlyList<DetectedMarker> markers)
    {
        var kept = new List<DetectedMarker>(markers.Count);
        var rejected = new List<RejectedMarkerCandidate>();
        foreach (var marker in markers)
        {
            var contrast = marker.Synthetic ? null : Contrast(gray, marker.CornersPx);
            if (contrast is { } c && c < MinContrast)
            {
                rejected.Add(new RejectedMarkerCandidate(
                    marker.Id, marker.CornersPx, marker.SidePx, marker.EdgeRatio, MarkerRejectionReason.NoQuietZone,
                    $"id {marker.Id}: surroundings as dark as its border (contrast {c:F2} < {MinContrast:F2}); not a printed marker"));
            }
            else
            {
                kept.Add(marker);
            }
        }

        return (kept, rejected);
    }

    /// <summary>The quiet-zone contrast of a quad, or null when too little of its surroundings is in the image.</summary>
    public static double? Contrast(Mat gray, IReadOnlyList<MarkerPoint> corners)
    {
        var h = Homography(corners);
        var border = new List<double>();
        var interior = new List<double>();
        var ring = new List<double>();
        var ringTotal = 0;
        for (var u = -0.2; u <= 1.2; u += Step)
        {
            for (var v = -0.2; v <= 1.2; v += Step)
            {
                var inside = Math.Min(Math.Min(u, v), Math.Min(1 - u, 1 - v)); // > 0 inside the square
                var list = inside switch
                {
                    >= 0.03 and <= 0.14 => border,
                    >= 0.2 => interior,
                    <= -0.05 and >= -0.17 => ring,
                    _ => null,
                };
                if (list is null)
                {
                    continue;
                }

                ringTotal += ReferenceEquals(list, ring) ? 1 : 0;
                if (Sample(gray, h, u, v) is { } value)
                {
                    list.Add(value);
                }
            }
        }

        if (border.Count < 20 || interior.Count < 20 || ring.Count < ringTotal / 2)
        {
            return null;
        }

        var black = border.Average();
        var white = Percentile(interior, 0.9);
        return (Percentile(ring, 0.25) - black) / Math.Max(1.0, white - black);
    }

    private static double[] Homography(IReadOnlyList<MarkerPoint> corners)
    {
        Point2f[] unit = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        var quad = corners.Select(c => new Point2f((float)c.X, (float)c.Y)).ToArray();
        using var m = Cv2.GetPerspectiveTransform(unit, quad);
        var h = new double[9];
        for (var i = 0; i < 9; i++)
        {
            h[i] = m.At<double>(i / 3, i % 3);
        }

        return h;
    }

    private static double? Sample(Mat gray, double[] h, double u, double v)
    {
        var w = (h[6] * u) + (h[7] * v) + h[8];
        var x = (int)Math.Round(((h[0] * u) + (h[1] * v) + h[2]) / w);
        var y = (int)Math.Round(((h[3] * u) + (h[4] * v) + h[5]) / w);
        if (w <= 0 || x < 0 || y < 0 || x >= gray.Width || y >= gray.Height)
        {
            return null;
        }

        return gray.At<byte>(y, x);
    }

    private static double Percentile(List<double> values, double q)
    {
        values.Sort();
        return values[(int)Math.Clamp(Math.Round(q * (values.Count - 1)), 0, values.Count - 1)];
    }
}
