namespace Blocwerk.Core.Geometry;

/// <summary>An axis-aligned rectangle in a facet's plane frame (mm; <c>a</c> across, <c>b</c> up).</summary>
public readonly record struct PlaneRectMm(double AMin, double AMax, double BMin, double BMax)
{
    public double Width => Math.Max(0, AMax - AMin);

    public double Height => Math.Max(0, BMax - BMin);

    public double Area => Width * Height;

    /// <summary>The overlap of two rectangles, or null when they do not overlap.</summary>
    public PlaneRectMm? Intersect(PlaneRectMm other)
    {
        var r = new PlaneRectMm(
            Math.Max(AMin, other.AMin),
            Math.Min(AMax, other.AMax),
            Math.Max(BMin, other.BMin),
            Math.Min(BMax, other.BMax));
        return r.AMax > r.AMin && r.BMax > r.BMin ? r : null;
    }

    /// <summary>The bounding box of a point set; null when it is empty.</summary>
    public static PlaneRectMm? Bounds(IEnumerable<(double A, double B)> points)
    {
        var list = points.ToList();
        if (list.Count == 0)
        {
            return null;
        }

        return new PlaneRectMm(list.Min(p => p.A), list.Max(p => p.A), list.Min(p => p.B), list.Max(p => p.B));
    }
}

public sealed record WallGeometryWorld
{
    public string? Origin { get; init; }

    public double[]? Up { get; init; }
}

public sealed record WallGeometrySegment
{
    /// <summary>Segment index: the marker plan's segment, or <c>markerId / 6</c> for legacy ids.</summary>
    public int Index { get; init; }

    public string? Name { get; init; }

    public double? DeclaredAngleDeg { get; init; }

    public double? MeasuredAngleDeg { get; init; }

    public IReadOnlyList<WallGeometryFacet> Facets { get; init; } = [];
}

/// <summary>A planar piece of a segment. Point (a, b) sits at <c>Origin + a·U + b·V</c> in world mm.</summary>
public sealed record WallGeometryFacet
{
    /// <summary>"0", or "5a"/"5b" when a segment folds.</summary>
    public string Id { get; init; } = string.Empty;

    public double[]? Origin { get; init; }

    public double[]? U { get; init; }

    public double[]? V { get; init; }

    public double[]? Normal { get; init; }

    public double? MeasuredAngleDeg { get; init; }

    /// <summary>
    /// The solver's <c>frame.yaw_rel</c> relative to the reference facet, counter-clockwise seen from
    /// above: positive = the facet faces toward the viewer's right, i.e. it is on the viewer's left (The
    /// Attic's left triangle: +89°). Same sense as <see cref="Entities.WallSegment.Yaw"/>.
    /// </summary>
    public double? YawDeg { get; init; }

    /// <summary>Bounding box of the facet's markers plus a margin. NOT an outline of the facet.</summary>
    public PlaneRectMm? ExtentMm { get; init; }
}

public sealed record WallGeometryMarker
{
    public int Id { get; init; }

    public int Segment { get; init; }

    /// <summary>TL, TR, BR, BL, H or V for legacy ids; null for a plan's ids.</summary>
    public string? Role { get; init; }

    /// <summary>This marker's printed size when the solver reports it (per-marker sizes); else the document's.</summary>
    public double? SizeMm { get; init; }

    public string Facet { get; init; } = string.Empty;

    /// <summary>Corners TL, TR, BR, BL (printed orientation) as [a, b] in the facet frame, mm.</summary>
    public IReadOnlyList<double[]> CornersPlaneMm { get; init; } = [];

    /// <summary>Corners TL, TR, BR, BL as [x, y, z] in the world frame, mm.</summary>
    public IReadOnlyList<double[]>? CornersWorldMm { get; init; }

    public int Observations { get; init; }

    public double? ReprojRmsPx { get; init; }

    /// <summary>
    /// The marker's side length as the photos show it, mm: its corners triangulated without a size prior and
    /// the four sides averaged. Unlike <see cref="CornersPlaneMm"/> (always exactly <see cref="SizeMm"/>) this
    /// reveals a misprinted or misdeclared sheet. Null when seen in a single photo, or from an older solver.
    /// </summary>
    public double? MeasuredSideMm { get; init; }

    /// <summary>How many photos <see cref="MeasuredSideMm"/> is based on; null with it.</summary>
    public int? MeasuredSidePhotos { get; init; }

    /// <summary>True if any corner was reconstructed (e.g. id 1, cut off by the frame); down-weight it.</summary>
    public bool Synthetic { get; init; }
}

public sealed record WallGeometryQuality
{
    public double? ReprojRmsPx { get; init; }

    public string? Gravity { get; init; }

    /// <summary>
    /// Markers the solver down-weighted because they fit the photos far worse than the rest (marker id →
    /// details): typically a wrong printed size in the plan, or a sheet that does not lie flat.
    /// </summary>
    public IReadOnlyDictionary<string, WallGeometryDownweightedMarker>? DownweightedMarkers { get; init; }
}

/// <summary>Why the solver down-weighted a marker (<c>quality.downweightedMarkers</c>).</summary>
public sealed record WallGeometryDownweightedMarker
{
    /// <summary>The marker's reprojection RMS in the free (unconstrained) solve, px.</summary>
    public double? FreeRmsPx { get; init; }

    /// <summary>The median marker's reprojection RMS in that solve, px.</summary>
    public double? MedianRmsPx { get; init; }
}
