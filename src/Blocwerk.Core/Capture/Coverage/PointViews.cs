// <copyright file="PointViews.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>
/// How the capture's cameras see one surface point: how many see it (in frame, from its front, unoccluded), from how
/// many distinct directions, the widest angle between two of their view rays, the most face-on view and the finest
/// resolution.
/// </summary>
/// <param name="Views">Cameras that see it.</param>
/// <param name="Directions">Distinct view directions (rays at least <see cref="DirectionSeparationDeg"/> apart).</param>
/// <param name="SpreadDeg">The widest angle between two view rays, degrees.</param>
/// <param name="BestAngleDeg">The most face-on view's angle off the surface normal, degrees (90 without views).</param>
/// <param name="BestMmPerPx">The finest resolution, mm per pixel (infinity without views).</param>
public readonly record struct PointViews(int Views, int Directions, double SpreadDeg, double BestAngleDeg, double BestMmPerPx)
{
    /// <summary>Fewer distinct directions than this: "seen from &lt; 3 directions".</summary>
    public const int MinDirections = 3;

    /// <summary>Views steeper than this are grazing, degrees off face-on.</summary>
    public const double GrazingDeg = 70;

    /// <summary>Coarser than this is low resolution, mm per pixel.</summary>
    public const double MaxMmPerPx = 2;

    /// <summary>Two rays closer than this are the same direction, degrees.</summary>
    public const double DirectionSeparationDeg = 15;

    /// <summary>The rating: never seen, then only grazing, then too few directions, then only far away.</summary>
    public CoverageCellStatus Status =>
        Views == 0 ? CoverageCellStatus.Never
        : BestAngleDeg > GrazingDeg ? CoverageCellStatus.Grazing
        : Directions < MinDirections ? CoverageCellStatus.FewDirections
        : BestMmPerPx > MaxMmPerPx ? CoverageCellStatus.LowResolution
        : CoverageCellStatus.Good;

    /// <summary>Rates a surface point against the cameras.</summary>
    /// <param name="point">The point, world mm.</param>
    /// <param name="normal">Its outward unit normal, world.</param>
    /// <param name="facetId">Its facet (for the occlusion test).</param>
    /// <param name="cameras">The posed cameras.</param>
    /// <param name="scene">The wall geometry.</param>
    /// <returns>The views.</returns>
    public static PointViews Evaluate(
        double[] point, double[] normal, string facetId, IReadOnlyList<CoverageCamera> cameras, CoverageScene scene)
    {
        var minDot = Math.Cos(DirectionSeparationDeg * Math.PI / 180);
        var directions = new List<double[]>();
        int views = 0;
        double bestCos = 0, bestMm = double.PositiveInfinity;
        foreach (var cam in cameras)
        {
            double[] v = [cam.Centre[0] - point[0], cam.Centre[1] - point[1], cam.Centre[2] - point[2]];
            var dist = Math.Sqrt(Dot(v, v));
            var cos = dist <= 0 ? 0 : Dot(v, normal) / dist;
            if (cos <= 0.035 || !cam.InFrame(point) || scene.Occluded(cam.Centre, point, facetId))
            {
                continue;
            }

            views++;
            bestCos = Math.Max(bestCos, cos);
            bestMm = Math.Min(bestMm, dist / cam.FocalPx);
            double[] ray = [v[0] / dist, v[1] / dist, v[2] / dist];
            if (directions.All(d => Dot(d, ray) < minDot))
            {
                directions.Add(ray);
            }
        }

        return new PointViews(views, directions.Count, Spread(directions), Math.Acos(Math.Clamp(bestCos, 0, 1)) * 180 / Math.PI, bestMm);
    }

    private static double Spread(List<double[]> rays)
    {
        var minDot = 1.0;
        for (var i = 0; i < rays.Count; i++)
        {
            for (var j = i + 1; j < rays.Count; j++)
            {
                minDot = Math.Min(minDot, Dot(rays[i], rays[j]));
            }
        }

        return Math.Acos(Math.Clamp(minDot, -1, 1)) * 180 / Math.PI;
    }

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);
}
