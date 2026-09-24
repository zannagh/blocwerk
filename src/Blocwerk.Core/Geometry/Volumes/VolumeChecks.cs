// <copyright file="VolumeChecks.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.Volumes;

/// <summary>A known hold on a facet, as an axis-aligned ellipse around its plane centre (mm).</summary>
/// <param name="A">Centre along u.</param>
/// <param name="B">Centre along v.</param>
/// <param name="HalfWidth">Half its width.</param>
/// <param name="HalfHeight">Half its height.</param>
public readonly record struct KnownHoldEllipse(double A, double B, double HalfWidth, double HalfHeight);

/// <summary>
/// The tests that tell a volume from other raised things (see <see cref="VolumeDetectionOptions"/>): a lone big
/// hold or a hold cluster (known hold outlines), the wall's edge or what is beyond it, and something that does
/// not sit on this facet at all (no bare wall around it).
/// </summary>
public static class VolumeChecks
{
    private const double SampleMm = 10;
    private const double RingInnerMm = 20;

    /// <summary>(share covered by any hold, largest share covered by one hold) of a footprint.</summary>
    /// <param name="footprint">Convex outline.</param>
    /// <param name="holds">Known holds on the facet.</param>
    /// <returns>The two shares.</returns>
    public static (double Any, double Single) HoldCover(IReadOnlyList<(double A, double B)> footprint, IReadOnlyList<KnownHoldEllipse> holds)
    {
        double a0 = footprint.Min(p => p.A), a1 = footprint.Max(p => p.A), b0 = footprint.Min(p => p.B), b1 = footprint.Max(p => p.B);
        var near = holds.Where(h => h.A + h.HalfWidth >= a0 && h.A - h.HalfWidth <= a1 && h.B + h.HalfHeight >= b0 && h.B - h.HalfHeight <= b1).ToList();
        var perHold = new int[near.Count];
        int total = 0, covered = 0;
        for (var a = a0 + (SampleMm / 2); a < a1; a += SampleMm)
        {
            for (var b = b0 + (SampleMm / 2); b < b1; b += SampleMm)
            {
                if (!PlanePolygon.Contains(footprint, (a, b)))
                {
                    continue;
                }

                total++;
                var any = false;
                for (var k = 0; k < near.Count; k++)
                {
                    var h = near[k];
                    double da = (a - h.A) / h.HalfWidth, db = (b - h.B) / h.HalfHeight;
                    if ((da * da) + (db * db) <= 1)
                    {
                        perHold[k]++;
                        any = true;
                    }
                }

                covered += any ? 1 : 0;
            }
        }

        return total == 0 ? (0, 0) : ((double)covered / total, (double)(perHold.Length == 0 ? 0 : perHold.Max()) / total);
    }

    /// <summary>Share of the cloud's points in the ring around a footprint that lie on the bare wall.</summary>
    /// <param name="cloud">The facet's cloud.</param>
    /// <param name="footprint">Convex outline.</param>
    /// <param name="options">Tuning.</param>
    /// <returns>The share; 0 with too few ring points.</returns>
    public static double WallSupport(FacetCloud cloud, IReadOnlyList<(double A, double B)> footprint, VolumeDetectionOptions options)
    {
        var ring = options.RingMm;
        double a0 = footprint.Min(p => p.A) - ring, a1 = footprint.Max(p => p.A) + ring;
        double b0 = footprint.Min(p => p.B) - ring, b1 = footprint.Max(p => p.B) + ring;
        int count = 0, flat = 0;
        for (var k = 0; k < cloud.Count; k++)
        {
            double a = cloud.A[k], b = cloud.B[k];
            if (a < a0 || a > a1 || b < b0 || b > b1 || PlanePolygon.Contains(footprint, (a, b)))
            {
                continue;
            }

            var d = DistanceToRing(footprint, a, b);
            if (d > RingInnerMm && d < ring)
            {
                count++;
                flat += Math.Abs(cloud.H[k]) < options.WallToleranceMm ? 1 : 0;
            }
        }

        return count < 30 ? 0 : (double)flat / count;
    }

    /// <summary>The verdict: <see cref="DetectedVolume.Accepted"/> or "rejected:&lt;reason&gt;".</summary>
    /// <param name="c">The measured candidate (status and surface not yet set).</param>
    /// <param name="extent">The facet's extent.</param>
    /// <param name="options">Tuning.</param>
    /// <returns>The status.</returns>
    public static string Judge(DetectedVolume c, PlaneRectMm extent, VolumeDetectionOptions options)
    {
        var band = options.EdgeBandMm;
        var f = c.Footprint;
        return c switch
        {
            _ when c.HeightMm > options.MaxHeightMm => "rejected:too-tall",
            _ when c.AreaM2 < options.MinAreaM2 => "rejected:small",
            _ when f.Min(p => p.A) < extent.AMin + band || f.Max(p => p.A) > extent.AMax - band
                || f.Min(p => p.B) < extent.BMin + band || f.Max(p => p.B) > extent.BMax - band => "rejected:edge",
            _ when c.WallSupport < options.MinWallSupport => "rejected:no-wall-around",
            _ when c.SingleHoldCover > options.MaxSingleHoldCover => "rejected:single-hold",
            _ when c.HoldCover > options.MaxHoldCover && c.HeightMm < options.ClusterMaxHeightMm => "rejected:hold-cluster",
            _ when c.HoldCover < 0.05 && c.AreaM2 < options.BareMaxAreaM2 => "rejected:bare-step",
            _ => DetectedVolume.Accepted,
        };
    }

    private static double DistanceToRing(IReadOnlyList<(double A, double B)> ring, double a, double b)
    {
        var best = double.MaxValue;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            double ax = ring[j].A, ay = ring[j].B, bx = ring[i].A, by = ring[i].B;
            double dx = bx - ax, dy = by - ay;
            var len2 = (dx * dx) + (dy * dy);
            var t = len2 <= 0 ? 0 : Math.Clamp((((a - ax) * dx) + ((b - ay) * dy)) / len2, 0, 1);
            double px = ax + (t * dx) - a, py = ay + (t * dy) - b;
            best = Math.Min(best, Math.Sqrt((px * px) + (py * py)));
        }

        return best;
    }
}
