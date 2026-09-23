// <copyright file="MarkerPlacementChecker.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using Blocwerk.Core.Geometry;

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// Compares a marker plan with a solved geometry: markers never seen or not placed, markers on another
/// surface than planned (the solver moves a marker whose normal fits another facet), markers far from
/// their planned spot, markers whose measured size differs from the planned print (<see cref="MarkerSizeCheck"/>),
/// and surfaces steeper or flatter than planned. A solved facet's frame has its
/// origin at its markers' bounding box, not at the plan's segment corner, so positions are compared
/// after a robust rigid 2D fit of planned onto measured centres, per facet.
/// </summary>
public static class MarkerPlacementChecker
{
    /// <summary>A marker further than this from its planned spot (after the fit) is reported.</summary>
    public const double OffsetToleranceMm = 100;

    /// <summary>A surface whose measured angle differs from the plan by more than this is reported.</summary>
    public const double AngleToleranceDeg = 5;

    /// <summary>Checks <paramref name="document"/> against the plan behind <paramref name="layout"/>.</summary>
    /// <param name="layout">A layout built from a plan (a legacy layout has nothing to compare).</param>
    /// <param name="document">The solved geometry.</param>
    /// <param name="detectedIds">Every id detected in any photo of the capture.</param>
    public static MarkerPlacementCheck Check(WallMarkerLayout layout, WallGeometryDocument document, IReadOnlySet<int> detectedIds)
    {
        var solved = document.Markers
            .Where(m => layout.Markers.ContainsKey(m.Id) && m.CornersPlaneMm.Count == 4)
            .GroupBy(m => m.Id)
            .ToDictionary(g => g.Key, g => g.First());
        var findings = new List<MarkerPlacementFinding>();
        foreach (var planned in layout.Markers.Values.OrderBy(m => m.Id))
        {
            if (!solved.TryGetValue(planned.Id, out var measured))
            {
                findings.Add(Missing(layout, planned, detectedIds.Contains(planned.Id)));
            }
            else if (measured.Segment != planned.Segment)
            {
                findings.Add(new MarkerPlacementFinding(
                    MarkerPlacementIssue.WrongSegment, planned.Id, planned.Segment, measured.Segment, null,
                    $"Marker {planned.Id} was planned on {Describe(layout, planned.Segment)} but was found on {Describe(layout, measured.Segment)}."));
            }
        }

        var checkedCount = CheckOffsets(layout, solved, findings);
        CheckSizes(layout, solved, findings);
        CheckAngles(layout, document, findings);
        var ordered = findings.OrderBy(f => f.Kind).ThenBy(f => f.MarkerId).ThenBy(f => f.PlannedSegment).ToList();
        return new MarkerPlacementCheck(layout.Markers.Count, solved.Count, checkedCount, ordered);
    }

    private static MarkerPlacementFinding Missing(WallMarkerLayout layout, LayoutMarker planned, bool detected) => detected
        ? new MarkerPlacementFinding(
            MarkerPlacementIssue.NotSolved, planned.Id, planned.Segment, null, null,
            $"Marker {planned.Id} ({Describe(layout, planned.Segment)}) was seen but could not be placed — make sure it shows fully in at least two photos.")
        : new MarkerPlacementFinding(
            MarkerPlacementIssue.NeverSeen, planned.Id, planned.Segment, null, null,
            $"Marker {planned.Id} ({Describe(layout, planned.Segment)}) was never seen in any photo — is it on the wall, and in the photos?");

    /// <summary>Per facet: fit the plan onto the measured centres; flag markers far off. Returns how many were compared.</summary>
    private static int CheckOffsets(
        WallMarkerLayout layout, Dictionary<int, WallGeometryMarker> solved, List<MarkerPlacementFinding> findings)
    {
        var compared = 0;
        var onPlannedSurface = solved.Values
            .Where(m => layout.Markers[m.Id] is { PlannedXMm: not null, PlannedYMm: not null } p && p.Segment == m.Segment)
            .GroupBy(m => m.Facet);
        foreach (var facet in onPlannedSurface)
        {
            var markers = facet.OrderBy(m => m.Id).ToList();
            var pairs = markers.Select(m => (From: Planned(layout.Markers[m.Id]), To: Centre(m))).ToList();
            var fit = RigidFit2D.Robust(pairs, OffsetToleranceMm);
            if (fit is null)
            {
                continue;
            }

            compared += markers.Count;
            for (var i = 0; i < markers.Count; i++)
            {
                var offset = fit.Residual(pairs[i]);
                if (offset > OffsetToleranceMm)
                {
                    var segment = markers[i].Segment;
                    findings.Add(new MarkerPlacementFinding(
                        MarkerPlacementIssue.Offset, markers[i].Id, segment, segment, Math.Round(offset),
                        $"Marker {markers[i].Id} is {Mm(offset)} away from its planned spot on {Describe(layout, segment)}."));
                }
            }
        }

        return compared;
    }

    private static void CheckSizes(
        WallMarkerLayout layout, Dictionary<int, WallGeometryMarker> solved, List<MarkerPlacementFinding> findings)
    {
        foreach (var measured in solved.Values.OrderBy(m => m.Id))
        {
            if (layout.SizeOf(measured.Id) is not { } planned || MarkerSizeCheck.Mismatch(measured, planned) is not { } side)
            {
                continue;
            }

            findings.Add(new MarkerPlacementFinding(
                MarkerPlacementIssue.SizeMismatch, measured.Id, layout.Markers[measured.Id].Segment, measured.Segment,
                Math.Round(side - planned, 1), MarkerSizeCheck.Describe(measured.Id, planned, side)));
        }
    }

    private static void CheckAngles(WallMarkerLayout layout, WallGeometryDocument document, List<MarkerPlacementFinding> findings)
    {
        foreach (var segment in layout.Segments)
        {
            var solvedSegment = document.Segments.FirstOrDefault(s => s.Index == segment.Index);
            var measured = solvedSegment?.MeasuredAngleDeg
                           ?? (solvedSegment?.Facets.Count == 1 ? solvedSegment.Facets[0].MeasuredAngleDeg : null);
            if (measured is not { } angle || Math.Abs(angle - segment.OverhangDeg) <= AngleToleranceDeg)
            {
                continue;
            }

            findings.Add(new MarkerPlacementFinding(
                MarkerPlacementIssue.AngleMismatch, null, segment.Index, segment.Index, Math.Round(angle - segment.OverhangDeg, 1),
                $"{Capitalized(Describe(layout, segment.Index))} measured {SurfaceAngle.Describe(angle)}, but the plan says {SurfaceAngle.Describe(segment.OverhangDeg)}."));
        }
    }

    private static PlanVector Planned(LayoutMarker marker) => new(marker.PlannedXMm!.Value, marker.PlannedYMm!.Value);

    private static PlanVector Centre(WallGeometryMarker marker) =>
        new(marker.CornersPlaneMm.Average(c => c[0]), marker.CornersPlaneMm.Average(c => c[1]));

    private static string Describe(WallMarkerLayout layout, int segment) =>
        layout.FindSegment(segment) is { } s ? $"segment {segment} (“{s.Name}”)" : $"segment {segment}";

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Mm(double value) => string.Create(CultureInfo.InvariantCulture, $"{value:0} mm");
}
