using System.Globalization;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Geometry;

/// <summary>
/// Derived, display-level facts about a <see cref="WallGeometryDocument"/>: the per-segment measured
/// angle/yaw copied onto <see cref="WallSegment"/> rows, and the "marker span" summary columns.
/// </summary>
public static class WallGeometrySummary
{
    /// <summary>
    /// The measured tilt and yaw for a document segment. The segment-level angle wins; a folded
    /// segment (facets "5a"/"5b") falls back to its primary facet — the one named after the segment,
    /// else the first — which is also where the yaw is read from.
    /// </summary>
    public static (double? AngleDeg, double? YawDeg) MeasuredFor(WallGeometrySegment segment)
    {
        var primary = segment.Facets.FirstOrDefault(f => f.Id == segment.Index.ToString(CultureInfo.InvariantCulture))
                      ?? segment.Facets.FirstOrDefault();
        return (segment.MeasuredAngleDeg ?? primary?.MeasuredAngleDeg, primary?.YawDeg);
    }

    /// <summary>
    /// Width and height of the LARGEST facet's marker extent (by area), in mm — the main wall, not a
    /// long thin strip such as a kickboard, which "widest" used to pick. This is the span the markers
    /// cover plus the solver's margin — not the size of the wall. Null when no facet has an extent.
    /// </summary>
    public static (double WidthMm, double HeightMm)? MarkerSpan(WallGeometryDocument document)
    {
        var largest = document.Segments
            .SelectMany(s => s.Facets)
            .Where(f => f.ExtentMm is not null)
            .Select(f => f.ExtentMm!.Value)
            .OrderByDescending(e => e.Width * e.Height)
            .Cast<PlaneRectMm?>()
            .FirstOrDefault();
        return largest is { } e ? (e.Width, e.Height) : null;
    }

    /// <summary>One row per facet, in document order, with its segment's declared angle.</summary>
    public static IReadOnlyList<WallGeometryFacetRow> FacetRows(WallGeometryDocument document)
    {
        var markersPerFacet = document.Markers
            .GroupBy(m => m.Facet, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return document.Segments
            .SelectMany(s => s.Facets.Select(f => new WallGeometryFacetRow(
                f.Id,
                s.Index,
                s.Name,
                s.DeclaredAngleDeg,
                f.MeasuredAngleDeg ?? (s.Facets.Count == 1 ? s.MeasuredAngleDeg : null),
                f.YawDeg,
                markersPerFacet.GetValueOrDefault(f.Id))))
            .ToList();
    }

    /// <summary>
    /// Copies the document's measured angle and yaw onto every segment bound to a marker segment.
    /// A bound segment the document does not describe is cleared, so no value from an older model
    /// survives an import or activation. Unbound segments are left alone.
    /// </summary>
    public static void ApplyMeasured(WallGeometryDocument document, IEnumerable<WallSegment> segments)
    {
        foreach (var segment in segments)
        {
            if (segment.MarkerSegmentIndex is not { } index)
            {
                continue;
            }

            var match = document.Segments.FirstOrDefault(s => s.Index == index);
            var (angle, yaw) = match is null ? (null, null) : MeasuredFor(match);
            segment.MeasuredAngle = angle;
            segment.MeasuredYaw = yaw;
        }
    }
}
