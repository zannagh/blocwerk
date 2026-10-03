// <copyright file="PlanLayoutView.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Capture;

/// <summary>A detection with its planned position and size in its segment's frame (mm, y up the surface).</summary>
internal sealed record PlanLayoutView(CaptureMarker Marker, int Segment, double PlanX, double PlanY, double SizeMm)
{
    public double X => Marker.Corners.Average(c => c[0]);

    /// <summary>Image y flipped to point up, so plan and image share a handedness.</summary>
    public double Up => -Marker.Corners.Average(c => c[1]);

    /// <summary>The longest edge in px: the least foreshortened measure of the marker's scale.</summary>
    public double MaxEdgePx => Enumerable.Range(0, 4).Max(i =>
        Math.Sqrt(Math.Pow(Marker.Corners[(i + 1) % 4][0] - Marker.Corners[i][0], 2)
                  + Math.Pow(Marker.Corners[(i + 1) % 4][1] - Marker.Corners[i][1], 2)));

    public static PlanLayoutView? Of(WallMarkerLayout layout, CaptureMarker marker) =>
        marker.Corners.Length == 4
        && layout.Markers.TryGetValue(marker.Id, out var planned)
        && planned is { PlannedXMm: { } x, PlannedYMm: { } y }
        && layout.SizeOf(marker.Id) is { } size
        && size > 0
            ? new PlanLayoutView(marker, planned.Segment, x, y, size)
            : null;

    public double PlanDistance(PlanLayoutView other) => Math.Sqrt(Math.Pow(PlanX - other.PlanX, 2) + Math.Pow(PlanY - other.PlanY, 2));
}
