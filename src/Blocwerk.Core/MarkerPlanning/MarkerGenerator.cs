// <copyright file="MarkerGenerator.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// Suggests a marker layout: a corner marker in every corner of every segment, fillers along every
/// edge (and inside big surfaces) no further apart than half a photo, sizes picked as the SMALLEST
/// available print size that reaches the role's measured on-photo pixel target on that surface
/// (<see cref="MarkerSizing.RequiredPx"/>, steep-view margin included) — markers cost wall space. Deterministic: same plan and options, same
/// markers, ids 0, 1, 2, … in segment order, corners before fillers.
/// </summary>
/// <remarks>
/// Markers on both sides of a shared edge are wanted, not avoided: they are what ties two segments
/// together in the solve. Every edge — shared or not — gets its corner markers plus fillers, so each
/// shared edge has markers within one photo footprint on both sides.
/// </remarks>
public static partial class MarkerGenerator
{
    /// <summary>Returns <paramref name="plan"/> with its markers replaced by the suggested layout.</summary>
    public static MarkerPlan Generate(MarkerPlan plan, MarkerGenerationOptions options)
    {
        var placed = new List<PlanMarker>();
        foreach (var segment in plan.Segments)
        {
            if (!(double.IsFinite(segment.WidthMm) && double.IsFinite(segment.HeightMm)
                  && segment.WidthMm > 0 && segment.HeightMm > 0))
            {
                continue;
            }

            var cornerSize = MarkerSizing.PickSize(MarkerRole.Corner, segment, plan.Photo, options, out _);
            var fillerSize = MarkerSizing.PickSize(MarkerRole.Filler, segment, plan.Photo, options, out _);
            var context = new SegmentFill(segment, plan.Photo, options);
            var corners = PlaceCorners(context, cornerSize);
            PlaceEdgeFillers(context, corners, fillerSize);
            PlaceInteriorFillers(context, fillerSize);
            placed.AddRange(context.Markers);
        }

        var numbered = placed.Select((m, i) => m with { Id = i }).ToList();
        return plan with { Dictionary = ArucoDict4X4.DictionaryName, Markers = numbered };
    }

    /// <summary>One marker per outline vertex, pulled inside; keyed by vertex index.</summary>
    private static Dictionary<int, PlanMarker> PlaceCorners(SegmentFill fill, double size)
    {
        var vertices = SegmentOutline.Vertices(fill.Segment);
        var incentre = SegmentOutline.Incentre(fill.Segment);
        var byVertex = new Dictionary<int, PlanMarker>();
        for (var i = 0; i < vertices.Count; i++)
        {
            foreach (var candidateSize in fill.SizesDownFrom(size))
            {
                var centre = CornerCentre(fill, vertices[i], incentre, candidateSize);
                if (centre is { } c && fill.TryAdd(c, candidateSize, MarkerRole.Corner) is { } marker)
                {
                    byVertex[i] = marker;
                    break;
                }
            }
        }

        return byVertex;
    }

    /// <summary>
    /// Where a corner marker goes: a rectangle's corner inset by the edge gap on both axes; a
    /// triangle's vertex walked toward the incentre until the square fits with the gap.
    /// </summary>
    private static PlanVector? CornerCentre(SegmentFill fill, PlanVector vertex, PlanVector incentre, double size)
    {
        var half = size / 2;
        var inset = fill.Options.EdgeInsetMm;
        if (fill.Segment.Shape == SegmentShape.Rectangle)
        {
            var sx = vertex.X < fill.Segment.WidthMm / 2 ? 1 : -1;
            var sy = vertex.Y < fill.Segment.HeightMm / 2 ? 1 : -1;
            return vertex + new PlanVector(sx * (inset + half), sy * (inset + half));
        }

        var toward = incentre - vertex;
        var direction = toward.Normalized();
        for (var d = 0.0; d <= toward.Length; d += 5)
        {
            var centre = vertex + (direction * d);
            if (SegmentOutline.ContainsSquare(fill.Segment, centre, half, inset))
            {
                return centre;
            }
        }

        return null;
    }
}
