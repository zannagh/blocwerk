using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>
/// Makes the auto-traced hold shapes of ONE panel photo non-overlapping, so every hold can be selected on its
/// own. Pure geometry (no OpenCV, no database).
/// </summary>
/// <remarks>
/// <para><b>Order of preference</b> for each unlocked hold: keep its outline; else clip it (cut it with a straight line
/// per neighbour, then re-smooth) or, failing that, shrink it uniformly toward the centre;
/// else the plain circle; else the circle at the LARGEST radius that clears every neighbour, never below
/// <see cref="MinRadiusFraction"/> of the original nor <see cref="MinRadiusFloor"/>. If even that floor overlaps,
/// the hold keeps its original radius and is reported as <see cref="HoldShapeFit.Unresolved"/>: a visible
/// overlap is better than a hold too small to tap.</para>
/// <para><b>Locked holds always win</b> and are never changed. Unlocked holds are processed in ascending id
/// order against the locked ones plus the already-decided unlocked ones, so the result depends only on the
/// input set, never on its order.</para>
/// <para><b>Tolerance</b>: two footprints overlap when closer than <see cref="Tolerance"/> (0.0005 of the image,
/// about 2 px on a 4000 px photo, or a 1.5 px hairline gap on screen). It is far above the 1e-5 rounding of stored
/// points, so a resolved result never flips back to "touching" through rounding, and it leaves a visible gap
/// between neighbours so a tap lands on exactly one of them. Circles are tested as circumscribed 48-gons.</para>
/// </remarks>
public static class HoldShapeOverlapResolver
{
    /// <summary>Footprints closer than this (normalized image units) count as overlapping.</summary>
    public const double Tolerance = 0.0005;

    /// <summary>A shrunk circle keeps at least this share of its original radius.</summary>
    public const double MinRadiusFraction = 0.5;

    /// <summary>Absolute smallest circle radius (the editor's own lower bound).</summary>
    public const double MinRadiusFloor = 0.003;

    /// <summary>The uniform shrink fallback never goes below this share of the outline's size.</summary>
    public const double MinShrinkFactor = 0.5;

    /// <summary>A clipped or shrunk outline must keep at least this share of its original area, else it becomes the circle.</summary>
    public const double MinAreaRetained = 0.5;

    private const double ShrinkStep = 0.92;
    private const int RadiusBisections = 20;

    /// <summary>Resolves the panel's holds.</summary>
    /// <param name="holds">Every hold on the panel photo (locked ones are obstacles only).</param>
    /// <param name="allowRadiusShrink">False when a hold's radius must stay as it is (then the circle step is the last one).</param>
    /// <param name="aspect">Photo width / height, used to re-smooth clipped outlines (1 when unknown).</param>
    /// <returns>One decision per unlocked hold, in ascending id order.</returns>
    public static List<HoldShapeResolution> Resolve(IReadOnlyList<HoldShapeInput> holds, bool allowRadiusShrink = true, double aspect = 1)
    {
        ArgumentNullException.ThrowIfNull(holds);
        var obstacles = new ShapeObstacles(Tolerance);
        foreach (var locked in holds.Where(h => h.Locked))
        {
            obstacles.Add(Footprint(locked, locked.Shape, locked.Radius));
        }

        var results = new List<HoldShapeResolution>();
        foreach (var hold in holds.Where(h => !h.Locked).OrderBy(h => h.Id))
        {
            var resolution = FitOne(hold, obstacles, allowRadiusShrink, aspect);
            obstacles.Add(Footprint(hold, resolution.Shape, resolution.Radius));
            results.Add(resolution);
        }

        return results;
    }

    private static HoldShapeResolution FitOne(HoldShapeInput hold, ShapeObstacles obstacles, bool allowRadiusShrink, double aspect)
    {
        if (hold.Shape is { Count: >= 3 } shape)
        {
            if (!obstacles.Overlaps(Footprint(hold, shape, hold.Radius)))
            {
                return new HoldShapeResolution(hold.Id, HoldShapeFit.Unchanged, shape, hold.Radius);
            }

            if (TryPullIn(hold, shape, obstacles, aspect) is { } pulled)
            {
                return new HoldShapeResolution(hold.Id, HoldShapeFit.Shrunk, pulled, hold.Radius);
            }
        }

        return FitCircle(hold, obstacles, allowRadiusShrink, hadShape: hold.Shape is { Count: >= 3 });
    }

    private static HoldShapeResolution FitCircle(HoldShapeInput hold, ShapeObstacles obstacles, bool allowRadiusShrink, bool hadShape)
    {
        var centre = new P2(hold.X, hold.Y);
        if (!obstacles.Overlaps(ShapeObstacles.Circle(centre, hold.Radius)))
        {
            return new HoldShapeResolution(hold.Id, hadShape ? HoldShapeFit.Circle : HoldShapeFit.Unchanged, null, hold.Radius);
        }

        double floor = Math.Min(hold.Radius, Math.Max(MinRadiusFloor, hold.Radius * MinRadiusFraction));
        if (!allowRadiusShrink || obstacles.Overlaps(ShapeObstacles.Circle(centre, floor)))
        {
            // Nothing sane clears it: keep the original size and say so, rather than a needlessly tiny circle.
            return new HoldShapeResolution(hold.Id, HoldShapeFit.Unresolved, null, hold.Radius);
        }

        // Overlap only grows with the radius, so bisect for the largest radius that still clears.
        double lo = floor;
        double hi = hold.Radius;
        for (int i = 0; i < RadiusBisections; i++)
        {
            double mid = (lo + hi) / 2;
            if (obstacles.Overlaps(ShapeObstacles.Circle(centre, mid)))
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        return new HoldShapeResolution(hold.Id, HoldShapeFit.ShrunkCircle, null, Math.Floor(lo * 1e6) / 1e6);
    }

    /// <summary>Clips (one straight cut per neighbour) then, if needed, uniformly shrinks the outline; null when it will not fit.</summary>
    private static List<ShapePoint>? TryPullIn(HoldShapeInput hold, IReadOnlyList<ShapePoint> shape, ShapeObstacles obstacles, double aspect)
    {
        var centre = new P2(hold.X, hold.Y);
        var local = shape.Select(s => new P2(s.Dx, s.Dy)).ToList();
        double originalArea = ShapeGeometry.Area(local);

        var clipped = ShapeClip.ClipToClear(local, centre, obstacles) is { } cut
            ? HoldShapeSmoother.Smooth(ToPoints(cut), aspect)
            : null;
        if (clipped is not null && Accept(clipped, centre, originalArea, obstacles))
        {
            return clipped;
        }

        for (double s = ShrinkStep; s >= MinShrinkFactor; s *= ShrinkStep)
        {
            var scaled = ToPoints(local.Select(p => p * s));
            if (HoldShapeSmoother.IsSmooth(scaled, aspect) && Accept(scaled, centre, originalArea, obstacles))
            {
                return scaled;
            }
        }

        return null;
    }

    private static bool Accept(List<ShapePoint> candidate, P2 centre, double originalArea, ShapeObstacles obstacles)
    {
        var local = candidate.Select(s => new P2(s.Dx, s.Dy)).ToList();
        return ShapeGeometry.Area(local) >= MinAreaRetained * originalArea
               && !obstacles.Overlaps(local.Select(p => p + centre).ToList());
    }

    private static List<P2> Footprint(HoldShapeInput hold, IReadOnlyList<ShapePoint>? shape, double radius)
    {
        var centre = new P2(hold.X, hold.Y);
        return shape is { Count: >= 3 }
            ? shape.Select(s => new P2(hold.X + s.Dx, hold.Y + s.Dy)).ToList()
            : [.. ShapeObstacles.Circle(centre, radius)];
    }

    private static List<ShapePoint> ToPoints(IEnumerable<P2> pts) =>
        pts.Select(p => new ShapePoint { Dx = Math.Round(p.X, 5), Dy = Math.Round(p.Y, 5) }).ToList();
}
