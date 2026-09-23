// <copyright file="MarkerPlanModels.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// A wall's marker plan: its surfaces drawn as an unfolded net, how the owner photographs it, and every
/// printed ArUco marker with the segment it sits on, its position and its printed size. The plan is what
/// makes a photo dump self-describing — the solver no longer has to guess which marker is on which surface
/// or how big it is, so marker ids need not encode segment or role.
/// </summary>
/// <param name="SchemaVersion">Plan JSON schema version (1).</param>
/// <param name="Dictionary">ArUco dictionary, e.g. <c>DICT_4X4_50</c>.</param>
/// <param name="Photo">How the owner photographs the wall — drives marker sizing and spacing.</param>
/// <param name="Segments">The wall's surfaces. Exactly one has no <see cref="PlanSegment.AttachedTo"/> (the root).</param>
/// <param name="Markers">Every marker to print and place.</param>
/// <param name="Print">How the PDF prints the markers (mounting holes); null = plain markers. Added
/// without a schema bump: older plans simply lack it, older readers ignore it.</param>
public sealed record MarkerPlan(
    int SchemaVersion,
    string Dictionary,
    PhotoSetup Photo,
    IReadOnlyList<PlanSegment> Segments,
    IReadOnlyList<PlanMarker> Markers,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    PrintOptions? Print = null)
{
    /// <summary>The current schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// How the wall is photographed. <see cref="HorizontalFovDeg"/> and <see cref="ImageLongEdgePx"/> are what
/// the sizing maths reads; the phone model and lens (added without a schema bump, both optional) say where
/// they came from so the planner can show and re-pick them. Old plans without them still read: their
/// <see cref="CameraPreset"/> maps to the generic phone of <see cref="PhoneCameraCatalog"/>.
/// </summary>
/// <param name="DistanceMm">Usual distance from the camera to the wall surface — the FARTHEST the owner
/// usually shoots from; markers are sized so they still work from there.</param>
/// <param name="CameraPreset">"phone-1x", "phone-0.5x" or "custom". With a phone model set it holds the
/// nearest legacy preset (0.5× for ultra-wides) so older readers show something sensible.</param>
/// <param name="HorizontalFovDeg">Horizontal field of view of the camera, in degrees.</param>
/// <param name="ImageLongEdgePx">Pixels along the photo's long edge (e.g. 4032).</param>
/// <param name="PhoneModel">A <see cref="PhoneCamera.Id"/> from <see cref="PhoneCameraCatalog"/>, or null.</param>
/// <param name="Lens">The lens / zoom preset of that phone (a <see cref="PhoneLens.Id"/> such as "0.5x"), or null.</param>
/// <param name="NearestDistanceMm">The closest the owner usually shoots from, or null. Only used for hints
/// (close-ups see fewer markers); sizing always uses <see cref="DistanceMm"/>.</param>
public sealed record PhotoSetup(
    double DistanceMm,
    string CameraPreset,
    double HorizontalFovDeg,
    int ImageLongEdgePx,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? PhoneModel = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Lens = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    double? NearestDistanceMm = null);

/// <summary>
/// One flat surface of the wall. Its own frame: origin at the bounding box's bottom-left, x to the right,
/// y UP THE SURFACE (up the slope for an overhang) — the same frame as a solved facet's (a, b), in mm.
/// </summary>
/// <param name="Index">Stable id within the plan (0, 1, 2, …).</param>
/// <param name="Name">Owner's name for it ("main wall", "kickboard", …).</param>
/// <param name="Shape">Rectangle, or a right triangle.</param>
/// <param name="WidthMm">Width (rectangle) or horizontal leg (triangle).</param>
/// <param name="HeightMm">Height along the surface (rectangle) or vertical leg (triangle).</param>
/// <param name="RightAngle">For triangles: which bounding-box corner holds the right angle. Ignored for rectangles.</param>
/// <param name="OverhangDeg">Tilt from vertical: 0 = vertical, positive = overhanging, negative = slab.</param>
/// <param name="YawDeg">Rotation about the vertical axis relative to the root segment (e.g. a side wall ≈ ±90).</param>
/// <param name="AttachedTo">How this segment joins its parent in the net; null for the root.</param>
public sealed record PlanSegment(
    int Index,
    string Name,
    SegmentShape Shape,
    double WidthMm,
    double HeightMm,
    TriangleCorner RightAngle,
    double OverhangDeg,
    double YawDeg,
    PlanAttachment? AttachedTo);

/// <summary>Segment outline kinds.</summary>
public enum SegmentShape
{
    /// <summary>A rectangle of <c>WidthMm × HeightMm</c>.</summary>
    Rectangle,

    /// <summary>A right triangle with legs <c>WidthMm</c> and <c>HeightMm</c>.</summary>
    Triangle,
}

/// <summary>Corners of a segment's bounding box (used for a triangle's right angle).</summary>
public enum TriangleCorner
{
    /// <summary>Bottom-left.</summary>
    BottomLeft,

    /// <summary>Bottom-right.</summary>
    BottomRight,

    /// <summary>Top-left.</summary>
    TopLeft,

    /// <summary>Top-right.</summary>
    TopRight,
}

/// <summary>Edges of a segment. A triangle has its two legs plus the hypotenuse.</summary>
public enum SegmentEdge
{
    /// <summary>Top edge.</summary>
    Top,

    /// <summary>Right edge.</summary>
    Right,

    /// <summary>Bottom edge.</summary>
    Bottom,

    /// <summary>Left edge.</summary>
    Left,

    /// <summary>A triangle's hypotenuse.</summary>
    Hypotenuse,
}

/// <summary>
/// Joins a segment to its parent along a shared edge, as in the unfolded net. The two edges are laid
/// against each other; <paramref name="OffsetMm"/> slides this segment's edge along the parent's edge,
/// measured from the parent edge's start (its lower/left end).
/// </summary>
/// <param name="ParentIndex">The parent segment's <see cref="PlanSegment.Index"/>.</param>
/// <param name="ParentEdge">Which edge of the parent.</param>
/// <param name="OwnEdge">Which edge of this segment touches it.</param>
/// <param name="OffsetMm">Slide along the parent edge.</param>
public sealed record PlanAttachment(int ParentIndex, SegmentEdge ParentEdge, SegmentEdge OwnEdge, double OffsetMm);

/// <summary>One printed marker.</summary>
/// <param name="Id">ArUco id in <see cref="MarkerPlan.Dictionary"/>. Unique within the plan.</param>
/// <param name="Segment">The <see cref="PlanSegment.Index"/> it sits on.</param>
/// <param name="XMm">Marker centre in the segment's frame (x right).</param>
/// <param name="YMm">Marker centre in the segment's frame (y up the surface).</param>
/// <param name="SizeMm">Printed black-square side length.</param>
/// <param name="Role">Corner anchor or filler (corners get the bigger size by default).</param>
public sealed record PlanMarker(int Id, int Segment, double XMm, double YMm, double SizeMm, MarkerRole Role);

/// <summary>What a marker is for.</summary>
public enum MarkerRole
{
    /// <summary>Anchors a segment corner: must be decodable from every photo that shows that corner.</summary>
    Corner,

    /// <summary>Links photos along an edge; redundant by design, so it may be smaller.</summary>
    Filler,
}

/// <summary>Options for generating a suggested marker layout.</summary>
/// <param name="CornerTargetPx">Minimum on-photo short side for corner markers before the steep-view margin
/// (<see cref="MarkerDetectability.CornerMinPx"/>).</param>
/// <param name="FillerTargetPx">Minimum on-photo short side for fillers before the steep-view margin
/// (<see cref="MarkerDetectability.FillerMinPx"/>).</param>
/// <param name="AvailableSizesMm">Printable sizes to choose from, ascending (e.g. 30, 40, 50, 60, 80, …).</param>
/// <param name="EdgeInsetMm">Gap between a segment edge and a marker's edge.</param>
/// <param name="CornerPxPerMetre">Pose-accuracy target for corners: short-side px per metre of photo
/// distance (<see cref="MarkerDetectability.CornerPosePxPerMetre"/>); 0 = decode target only.</param>
/// <param name="FillerPxPerMetre">The same for fillers (<see cref="MarkerDetectability.FillerPosePxPerMetre"/>).</param>
public sealed record MarkerGenerationOptions(
    double CornerTargetPx,
    double FillerTargetPx,
    IReadOnlyList<double> AvailableSizesMm,
    double EdgeInsetMm,
    double CornerPxPerMetre = 0,
    double FillerPxPerMetre = 0)
{
    /// <summary>
    /// Defaults backed by the 2026-09 sizing study on the owner's real photos (see
    /// <see cref="MarkerDetectability"/>): decode floors of 28 px (corners) / 22 px (fillers) short side, and
    /// for pose accuracy 30 / 20 px per metre of photo distance; sizes down to 30 mm, which works from about
    /// 1 m on an ultra-wide or 2 m on a 24 MP main camera.
    /// </summary>
    public static MarkerGenerationOptions Default { get; } = new(
        MarkerDetectability.CornerMinPx,
        MarkerDetectability.FillerMinPx,
        [30, 40, 50, 60, 80, 100, 125, 150, 200],
        30,
        MarkerDetectability.CornerPosePxPerMetre,
        MarkerDetectability.FillerPosePxPerMetre);
}

/// <summary>A problem the planner shows next to the plan. Warnings don't block saving; errors do.</summary>
/// <param name="Severity">Error or warning.</param>
/// <param name="Code">Stable machine code, e.g. "marker-too-small".</param>
/// <param name="Message">Plain-language explanation with a suggested fix.</param>
/// <param name="Segment">The segment it concerns, if any.</param>
/// <param name="MarkerId">The marker it concerns, if any.</param>
public sealed record PlanIssue(PlanIssueSeverity Severity, string Code, string Message, int? Segment, int? MarkerId);

/// <summary>Issue severity.</summary>
public enum PlanIssueSeverity
{
    /// <summary>The plan can't be used as is.</summary>
    Error,

    /// <summary>Usable, but likely to cause trouble.</summary>
    Warning,
}

/// <summary>The unfolded net in one 2D drawing frame (mm, y up), for the editor and the PDF map.</summary>
/// <param name="Segments">Each segment's outline in net space.</param>
/// <param name="Markers">Each marker's square in net space.</param>
/// <param name="MinX">Left edge of the whole net's bounding box.</param>
/// <param name="MinY">Bottom edge of the whole net's bounding box.</param>
/// <param name="MaxX">Right edge of the whole net's bounding box.</param>
/// <param name="MaxY">Top edge of the whole net's bounding box.</param>
public sealed record NetGeometry(
    IReadOnlyList<NetSegment> Segments,
    IReadOnlyList<NetMarker> Markers,
    double MinX,
    double MinY,
    double MaxX,
    double MaxY);

/// <summary>A segment's outline in net space, plus the transform from its own frame into net space.</summary>
/// <param name="Index">The segment index.</param>
/// <param name="PolygonMm">Outline vertices in net space (counter-clockwise).</param>
/// <param name="OriginX">Net-space x of the segment frame's origin.</param>
/// <param name="OriginY">Net-space y of the segment frame's origin.</param>
/// <param name="RotationDeg">Rotation of the segment frame in net space (counter-clockwise).</param>
public sealed record NetSegment(int Index, IReadOnlyList<double[]> PolygonMm, double OriginX, double OriginY, double RotationDeg);

/// <summary>A marker's square in net space.</summary>
/// <param name="Id">Marker id.</param>
/// <param name="CornersMm">Four corners in net space.</param>
/// <param name="EstimatedPx">Expected on-photo side length at the planned distance, after foreshortening.</param>
public sealed record NetMarker(int Id, IReadOnlyList<double[]> CornersMm, double EstimatedPx);
