// <copyright file="PlanFixtures.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>Small plan builders for the planner tests.</summary>
internal static class PlanFixtures
{
    public static PhotoSetup Photo { get; } = MarkerCameraPresets.Create(MarkerCameraPresets.Phone1X, 2000);

    public static PlanSegment Rect(int index, double w, double h, PlanAttachment? attached = null, double overhang = 0, double yaw = 0) =>
        new(index, $"seg {index}", SegmentShape.Rectangle, w, h, TriangleCorner.BottomLeft, overhang, yaw, attached);

    public static PlanSegment Tri(int index, double w, double h, TriangleCorner corner, PlanAttachment? attached = null) =>
        new(index, $"tri {index}", SegmentShape.Triangle, w, h, corner, 0, 0, attached);

    public static MarkerPlan Plan(IReadOnlyList<PlanSegment> segments, IReadOnlyList<PlanMarker>? markers = null, PhotoSetup? photo = null) =>
        new(MarkerPlan.CurrentSchemaVersion, ArucoDict4X4.DictionaryName, photo ?? Photo, segments, markers ?? []);

    /// <summary>Signed area of a polygon (positive = counter-clockwise).</summary>
    public static double SignedArea(IReadOnlyList<double[]> polygon)
    {
        var sum = 0.0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var p = polygon[i];
            var q = polygon[(i + 1) % polygon.Count];
            sum += (p[0] * q[1]) - (q[0] * p[1]);
        }

        return sum / 2;
    }
}
