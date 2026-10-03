// <copyright file="MarkerGenerator.Fillers.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>Filler placement: along the edges between the corner markers, then inside big surfaces.</summary>
public static partial class MarkerGenerator
{
    /// <summary>
    /// Fillers along each edge, evenly between the two corner markers at its ends, so no gap along
    /// the edge exceeds <see cref="MarkerSizing.MaxSpacingMm"/>. A filler that would leave the
    /// surface or crowd another marker is nudged along the edge, else skipped.
    /// </summary>
    private static void PlaceEdgeFillers(SegmentFill fill, Dictionary<int, PlanMarker> corners, double size)
    {
        var spacing = MarkerSizing.MaxSpacingMm(fill.Photo);
        var edges = SegmentOutline.Edges(fill.Segment);
        for (var i = 0; i < edges.Count; i++)
        {
            var edge = edges[i];
            var along = edge.Direction;
            var inward = edge.Inward;
            var start = corners.TryGetValue(i, out var a) ? Along(edge, a) : 0;
            var end = corners.TryGetValue((i + 1) % edges.Count, out var b) ? Along(edge, b) : edge.Length;
            var gap = end - start;
            var count = Math.Max(0, (int)Math.Ceiling(gap / spacing) - 1);

            // An axis-aligned square reaches (|n.x| + |n.y|)·half toward a slanted edge.
            var depth = fill.Options.EdgeInsetMm + ((Math.Abs(inward.X) + Math.Abs(inward.Y)) * size / 2);
            for (var k = 1; k <= count; k++)
            {
                var t = start + (gap * k / (count + 1));
                TryAddNudged(fill, edge.From + (along * t) + (inward * depth), along, size, spacing / 4);
            }
        }
    }

    /// <summary>
    /// Fillers inside surfaces bigger than a photo: every grid point (half-photo spacing) that is
    /// further than that spacing from all markers gets one, so a photo framed mid-wall still sees one.
    /// </summary>
    private static void PlaceInteriorFillers(SegmentFill fill, double size)
    {
        var spacing = MarkerSizing.MaxSpacingMm(fill.Photo);
        var w = fill.Segment.WidthMm;
        var h = fill.Segment.HeightMm;
        var nx = Math.Max(1, (int)Math.Ceiling(w / spacing));
        var ny = Math.Max(1, (int)Math.Ceiling(h / spacing));
        for (var j = 0; j < ny; j++)
        {
            for (var i = 0; i < nx; i++)
            {
                var p = new PlanVector(w * (i + 0.5) / nx, h * (j + 0.5) / ny);
                if (fill.Markers.All(m => (new PlanVector(m.XMm, m.YMm) - p).Length > spacing))
                {
                    TryAddNudged(fill, p, new PlanVector(1, 0), size, spacing / 4);
                }
            }
        }
    }

    private static void TryAddNudged(SegmentFill fill, PlanVector centre, PlanVector along, double size, double maxNudge)
    {
        var step = Math.Max(size / 2, 10);
        for (var n = 0.0; n <= maxNudge; n += step)
        {
            if (fill.TryAdd(centre + (along * n), size, MarkerRole.Filler) is not null
                || (n > 0 && fill.TryAdd(centre - (along * n), size, MarkerRole.Filler) is not null))
            {
                return;
            }
        }
    }

    private static double Along(SegmentShapeEdge edge, PlanMarker marker) =>
        (new PlanVector(marker.XMm, marker.YMm) - edge.From).Dot(edge.Direction);
}
