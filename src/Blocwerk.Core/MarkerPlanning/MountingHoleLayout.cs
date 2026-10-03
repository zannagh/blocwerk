// <copyright file="MountingHoleLayout.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// Where the mounting holes go — tight to the marker so the cut-out stays small. Each hole sits on the
/// diagonal outward from one corner of the black square, at a per-axis offset <c>d = (r + gap) / √2</c>
/// (r = head radius, gap = <see cref="MountingHoles.GapToMarkerMm"/>): the head's closest point is then
/// the square's corner, exactly <c>gap</c> away (the corner is at distance <c>√2 · d = r + gap</c> from the
/// centre). The head reaches <c>d + r</c> out along each axis, so the white border (black edge to cut line)
/// is <c>d + r + <see cref="MountingHoles.GapToEdgeMm"/></c>, independent of the marker size.
/// </summary>
public static class MountingHoleLayout
{
    /// <summary>Per-axis offset of a hole centre from its corner of the black square (outward on both axes).</summary>
    public static double CentreOffsetMm(MountingHoles holes) =>
        ((holes.ScrewHeadDiameterMm / 2) + holes.GapToMarkerMm) / Math.Sqrt(2);

    /// <summary>Distance along the diagonal from the square's corner to the hole centre.</summary>
    public static double DiagonalOffsetMm(MountingHoles holes) => Math.Sqrt(2) * CentreOffsetMm(holes);

    /// <summary>How far the head reaches past the black square's edge on each axis.</summary>
    public static double HeadExtentMm(MountingHoles holes) => CentreOffsetMm(holes) + (holes.ScrewHeadDiameterMm / 2);

    /// <summary>
    /// White border (black square edge to cut line): the head's extent plus the gap to the cut edge, never
    /// less than <paramref name="floorMm"/> (0 = the tight layout the owner asked for).
    /// </summary>
    public static double BorderMm(MountingHoles holes, double floorMm = 0) =>
        Math.Max(HeadExtentMm(holes) + holes.GapToEdgeMm, floorMm);

    /// <summary>
    /// The four hole centres (TL, TR, BR, BL) for a black square with top-left (<paramref name="leftMm"/>,
    /// <paramref name="topMm"/>) in page mm, y down.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> HoleCentres(double leftMm, double topMm, double sizeMm, MountingHoles holes)
    {
        var d = CentreOffsetMm(holes);
        var right = leftMm + sizeMm;
        var bottom = topMm + sizeMm;
        return [(leftMm - d, topMm - d), (right + d, topMm - d), (right + d, bottom + d), (leftMm - d, bottom + d)];
    }
}
