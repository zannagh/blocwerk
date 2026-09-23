// <copyright file="SegmentFill.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>The markers placed so far on one segment while generating, with the fit/crowding checks.</summary>
internal sealed class SegmentFill(PlanSegment segment, PhotoSetup photo, MarkerGenerationOptions options)
{
    private readonly List<PlanMarker> markers = [];

    public PlanSegment Segment => segment;

    public PhotoSetup Photo => photo;

    public MarkerGenerationOptions Options => options;

    public IReadOnlyList<PlanMarker> Markers => markers;

    /// <summary>
    /// Adds a marker (id assigned later) when it lies inside the segment with the edge gap and keeps
    /// at least that gap to every marker already placed; otherwise returns null.
    /// </summary>
    public PlanMarker? TryAdd(PlanVector centre, double size, MarkerRole role)
    {
        var half = size / 2;
        if (!SegmentOutline.ContainsSquare(segment, centre, half, options.EdgeInsetMm))
        {
            return null;
        }

        foreach (var m in markers)
        {
            var reach = half + (m.SizeMm / 2) + options.EdgeInsetMm;
            if (Math.Abs(m.XMm - centre.X) < reach && Math.Abs(m.YMm - centre.Y) < reach)
            {
                return null;
            }
        }

        var marker = new PlanMarker(-1, segment.Index, Math.Round(centre.X, 1), Math.Round(centre.Y, 1), size, role);
        markers.Add(marker);
        return marker;
    }

    /// <summary>The preferred size, then each smaller available size (for surfaces too small for it).</summary>
    public IEnumerable<double> SizesDownFrom(double preferred)
    {
        yield return preferred;
        foreach (var s in options.AvailableSizesMm.Where(s => s < preferred && s > 0).OrderDescending())
        {
            yield return s;
        }
    }
}
