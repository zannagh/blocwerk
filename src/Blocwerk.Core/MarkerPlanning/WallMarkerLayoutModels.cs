// <copyright file="WallMarkerLayoutModels.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>One marker as the effective layout knows it.</summary>
/// <param name="Id">ArUco id.</param>
/// <param name="Segment">The segment it sits on (plan index, or <c>id / 6</c> in the legacy scheme).</param>
/// <param name="SizeMm">Printed black-square side; null when the legacy wall never stated its size.</param>
/// <param name="Role">"corner"/"filler" from a plan, "TL".."V" in the legacy scheme.</param>
/// <param name="PlannedXMm">Planned centre in the segment frame (x right); null without a plan.</param>
/// <param name="PlannedYMm">Planned centre in the segment frame (y up the surface); null without a plan.</param>
public sealed record LayoutMarker(int Id, int Segment, double? SizeMm, string Role, double? PlannedXMm, double? PlannedYMm);

/// <summary>One segment (surface) of the effective layout — only a plan knows these.</summary>
/// <param name="Index">Plan segment index.</param>
/// <param name="Name">Owner's name.</param>
/// <param name="OverhangDeg">Planned tilt from vertical (+ overhang, − slab).</param>
/// <param name="YawDeg">Planned turn about the vertical axis relative to the root segment.</param>
/// <param name="VerticalReference">True when the plan says the surface is plumb (|overhang| below the tolerance).</param>
/// <param name="OutlineMm">Outline in the segment frame, counter-clockwise, as [x, y] pairs.</param>
public sealed record LayoutSegment(
    int Index,
    string Name,
    double OverhangDeg,
    double YawDeg,
    bool VerticalReference,
    IReadOnlyList<double[]> OutlineMm);

/// <summary>Two segments that share an edge in the unfolded net (from the plan's attachments).</summary>
/// <param name="Segment">The child segment.</param>
/// <param name="Edge">The child's edge on the seam.</param>
/// <param name="ParentSegment">The segment it is attached to.</param>
/// <param name="ParentEdge">The parent's edge on the seam.</param>
/// <param name="OffsetMm">Slide along the parent edge (see <see cref="PlanAttachment"/>).</param>
public sealed record LayoutSharedEdge(int Segment, SegmentEdge Edge, int ParentSegment, SegmentEdge ParentEdge, double OffsetMm);
