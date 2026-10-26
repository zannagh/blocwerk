// <copyright file="NewHoldWallRegion.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// The climbed wall on a staged photo, as the carried holds outline it: the convex hull of their spots, grown by a margin
/// of <see cref="MarginRadii"/> typical hold radii. A photo taken wider than the old one (an uncropped panel photo) also
/// shows the mats, the floor, beams and the electrics box beside the wall; a detection well beyond every carried hold is
/// one of those far more often than a new hold. The old photo's frame (<see cref="NewHoldDiscardReason.OutsideOldPhoto"/>)
/// does not catch them when the old photo, cropped or not, showed some of that too.
/// </summary>
public sealed class NewHoldWallRegion
{
    /// <summary>
    /// How far beyond the outermost carried holds the wall is taken to reach, in median carried-hold radii. On The Attic's
    /// gen 6 → 7 update the real new holds above the old top row lay up to 1.8 radii beyond the hull; 7 of the 10 mat, beam,
    /// electrics and marker proposals beyond it lay more than 2.5 radii out.
    /// </summary>
    public const double MarginRadii = 2.5;

    /// <summary>Fewer carried spots than this outline no wall: the rule does not run.</summary>
    public const int MinimumSpots = 12;

    private readonly IReadOnlyList<(double X, double Y)> hull;
    private readonly double marginPx;

    private NewHoldWallRegion(IReadOnlyList<(double X, double Y)> hullPoints, double margin)
    {
        hull = hullPoints;
        marginPx = margin;
    }

    /// <summary>The region the spots outline, or null with too few spots or no known hold size.</summary>
    /// <param name="spots">The carried holds' centres on the staged photo, RAW pixels.</param>
    /// <param name="radiiPx">The carried holds' radii, RAW pixels (unknown sizes as 0).</param>
    /// <returns>The region, or null when the rule cannot run.</returns>
    public static NewHoldWallRegion? Of(IReadOnlyList<(double X, double Y)> spots, IEnumerable<double> radiiPx)
    {
        var radii = radiiPx.Where(r => r > 0).Order().ToList();
        if (spots.Count < MinimumSpots || radii.Count == 0)
        {
            return null;
        }

        var hull = ConvexHull(spots);
        return hull.Count < 3 ? null : new NewHoldWallRegion(hull, MarginRadii * radii[radii.Count / 2]);
    }

    /// <summary>True when the point lies more than the margin outside the carried holds' hull.</summary>
    /// <param name="x">RAW x.</param>
    /// <param name="y">RAW y.</param>
    /// <returns>Whether it is off the wall.</returns>
    public bool IsOutside(double x, double y) => !Contains(x, y) && EdgeDistance(x, y) > marginPx;

    /// <summary>Andrew's monotone chain; counter-clockwise in a y-up frame, without collinear points.</summary>
    private static List<(double X, double Y)> ConvexHull(IReadOnlyList<(double X, double Y)> spots)
    {
        var points = spots.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (points.Count < 3)
        {
            return points;
        }

        var hull = new List<(double X, double Y)>();
        foreach (var pass in new[] { points, Enumerable.Reverse(points).ToList() })
        {
            var start = hull.Count;
            foreach (var p in pass)
            {
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], p) <= 0)
                {
                    hull.RemoveAt(hull.Count - 1);
                }

                hull.Add(p);
            }

            hull.RemoveAt(hull.Count - 1);
        }

        return hull;
    }

    private static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) =>
        ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));

    private bool Contains(double x, double y)
    {
        for (var i = 0; i < hull.Count; i++)
        {
            if (Cross(hull[i], hull[(i + 1) % hull.Count], (x, y)) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private double EdgeDistance(double x, double y)
    {
        var best = double.MaxValue;
        for (var i = 0; i < hull.Count; i++)
        {
            var (a, b) = (hull[i], hull[(i + 1) % hull.Count]);
            var (dx, dy) = (b.X - a.X, b.Y - a.Y);
            var t = Math.Clamp((((x - a.X) * dx) + ((y - a.Y) * dy)) / ((dx * dx) + (dy * dy)), 0, 1);
            best = Math.Min(best, Math.Sqrt(Math.Pow(x - (a.X + (t * dx)), 2) + Math.Pow(y - (a.Y + (t * dy)), 2)));
        }

        return best;
    }
}
