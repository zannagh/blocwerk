// <copyright file="AtticMarkerPlan.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>
/// The owner's wall ("The Attic") as a marker plan, from what capture 1 established
/// (.me/plan-2026-09-22-glyph-wall-geometry.md "Wall layout" + the solved wall-geometry.json): a 45°
/// main wall, the kickboard below it, the vertical left triangle whose hypotenuse IS the main wall's
/// left edge (right angle bottom-left, at marker 15), and the right cornered piece. Marker centres are
/// the solved plane positions shifted 20 mm into each surface's frame; every marker is 125 mm. The
/// ids follow the OLD <c>segment*6+role</c> scheme — 24–27 are seg-4 spares that physically sit on
/// the main wall — and the plan simply lists them there, no special case.
/// </summary>
public static class AtticMarkerPlan
{
    public static PhotoSetup Photo { get; } = MarkerCameraPresets.Create(MarkerCameraPresets.PhoneUltraWide, 2500);

    public static IReadOnlyList<PlanSegment> Segments { get; } =
    [
        new(0, "main wall", SegmentShape.Rectangle, 5200, 3400, TriangleCorner.BottomLeft, 45, 0, null),
        new(1, "kickboard", SegmentShape.Rectangle, 5800, 450, TriangleCorner.BottomLeft, 0, 0,
            new PlanAttachment(0, SegmentEdge.Bottom, SegmentEdge.Top, 30)),
        new(2, "left triangle", SegmentShape.Triangle, 2400, 2400, TriangleCorner.BottomLeft, 0, 90,
            new PlanAttachment(0, SegmentEdge.Left, SegmentEdge.Hypotenuse, 0)),
        new(5, "right cornered piece", SegmentShape.Rectangle, 1450, 2500, TriangleCorner.BottomLeft, 45, 0,
            new PlanAttachment(0, SegmentEdge.Right, SegmentEdge.Left, 100)),
    ];

    public static MarkerPlan Plan { get; } = new(
        MarkerPlan.CurrentSchemaVersion,
        ArucoDict4X4.DictionaryName,
        Photo,
        Segments,
        [
            Corner(0, 0, 65.1, 3138.9), Corner(1, 0, 4956.8, 3273.2), Corner(2, 0, 5020.9, 181.8), Corner(3, 0, 115.2, 62.5),
            Filler(4, 0, 2135.7, 3236.4), Filler(5, 0, 76.7, 2339.2),
            Corner(6, 1, 64.1, 210.8), Corner(7, 1, 5641.1, 306.0), Corner(8, 1, 5651.7, 161.8), Corner(9, 1, 63.3, 63.3),
            Filler(10, 1, 2549.3, 248.9),
            Corner(12, 2, 85.2, 1738.2), Corner(14, 2, 1878.8, 132.2), Corner(15, 2, 63.6, 63.6),
            Filler(24, 0, 3561.6, 3263.8), Filler(25, 0, 4975.3, 2413.9), Filler(26, 0, 2159.1, 100.3), Filler(27, 0, 3736.6, 149.8),
            Corner(31, 5, 1246.1, 2364.9), Corner(32, 5, 1285.1, 1216.0), Corner(33, 5, 63.9, 63.9),
        ]);

    /// <summary>The Attic's surfaces with no markers yet — the input for a suggested layout.</summary>
    public static MarkerPlan Blank { get; } = Plan with { Markers = [] };

    private static PlanMarker Corner(int id, int segment, double a, double b) =>
        new(id, segment, a + 20, b + 20, 125, MarkerRole.Corner);

    private static PlanMarker Filler(int id, int segment, double a, double b) =>
        new(id, segment, a + 20, b + 20, 125, MarkerRole.Filler);
}
