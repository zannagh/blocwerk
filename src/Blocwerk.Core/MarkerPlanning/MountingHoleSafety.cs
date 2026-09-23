// <copyright file="MountingHoleSafety.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// How thin the white border around a screwed marker may get before detection suffers, measured by
/// rendering the PDF and reading it back with the app's detector (MountingHoleDecodeTests) at 30–295 px
/// per marker, 6 and 15 mm screw heads, on white paper and on light, mid-grey and dark textured walls.
/// <list type="bullet">
/// <item>Since the detector keeps the black square instead of the paper's outline (ArUco's too-close
/// filter off, nested copies collapsed), every marker decoded with any border and any head gap down to
/// 0.5 mm, including the tight 1 mm / 1 mm default on 50 and 125 mm markers. Before, a border under
/// ~0.12–0.14 × the side lost markers on darker walls and heads closer than side/25 moved corners by
/// up to 10 px.</item>
/// <item>What is left is corner accuracy: a white strip under ~3 photo px is narrower than the blur,
/// and the refined edge drifts toward the wall (0.8–1.5 px at 1–2 px of border, ≤ 0.5 px from ~3 px).
/// The head gap itself made no measurable difference.</item>
/// </list>
/// With the border limit met, the refined corners stayed within 0.5 px of the truth in every run.
/// </summary>
public static class MountingHoleSafety
{
    /// <summary>Smallest white border (black edge to cut line) in photo pixels.</summary>
    public const double MinBorderPx = 3.0;

    /// <summary>Marker size in the photos the planner aims for, px (used when the photo scale is unknown).</summary>
    public const double PlannedMarkerPx = 60;

    /// <summary>Smallest white border as a fraction of the marker side when the photo scale is unknown.</summary>
    public const double MinBorderFraction = MinBorderPx / PlannedMarkerPx;

    /// <summary>
    /// Smallest white border for a <paramref name="sizeMm"/> marker photographed at
    /// <paramref name="photoPxPerMm"/> (0 or less = unknown: sized for <see cref="PlannedMarkerPx"/>).
    /// </summary>
    public static double MinBorderMm(double sizeMm, double photoPxPerMm) =>
        photoPxPerMm > 0 ? MinBorderPx / photoPxPerMm : MinBorderFraction * sizeMm;

    /// <summary>True when the holes keep the measured limit for a <paramref name="sizeMm"/> marker.</summary>
    public static bool IsSafe(double sizeMm, MountingHoles holes, double photoPxPerMm = 0) =>
        MountingHoleLayout.BorderMm(holes) >= MinBorderMm(sizeMm, photoPxPerMm) - 1e-9;

    /// <summary>
    /// The smallest gaps (0.5 mm steps, never below the current ones) that meet the limit for a
    /// <paramref name="sizeMm"/> marker, or null when the allowed gap range cannot reach it. The cut edge
    /// moves out first; the holes only when the edge gap alone cannot make the border.
    /// </summary>
    public static MountingHoles? SafeGaps(double sizeMm, MountingHoles holes, double photoPxPerMm = 0)
    {
        var r = holes.ScrewHeadDiameterMm / 2;
        var border = MinBorderMm(sizeMm, photoPxPerMm);
        var toMarker = holes.GapToMarkerMm;
        var toEdge = Math.Max(holes.GapToEdgeMm, RoundUpHalf(border - ExtentMm(r, toMarker)));
        if (toEdge > MountingHoles.MaxGapMm)
        {
            toEdge = MountingHoles.MaxGapMm;
            toMarker = Math.Max(toMarker, RoundUpHalf(((border - r - toEdge) * Math.Sqrt(2)) - r));
        }

        return toMarker <= MountingHoles.MaxGapMm
            ? holes with { GapToMarkerMm = toMarker, GapToEdgeMm = toEdge }
            : null;
    }

    private static double ExtentMm(double r, double gapToMarker) => ((r + gapToMarker) / Math.Sqrt(2)) + r;

    private static double RoundUpHalf(double mm) => Math.Max(MountingHoles.MinGapMm, Math.Ceiling((mm * 2) - 1e-9) / 2);
}
