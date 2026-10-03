// <copyright file="MountingHoleSafety.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// How thin the white border around a screwed marker may get, in photo pixels, from REAL photos: the
/// owner's 125 mm test markers (cut with a 3–7 % border) in 14 iPhone 16 Pro shots, downsampled to 20–80 px,
/// read back with the app's detector and its 20 px floor (sizing study 2026-09-23, view &lt; 55°, mean side
/// ≥ 26 px so the size itself is not the limit). Acceptance by border width and by the wall tone right
/// outside the paper:
/// <list type="bullet">
/// <item>Light plywood: 87–95 % at 1–2 px, 100 % from 2 px.</item>
/// <item>Mid tones: 40–77 % at 1–2 px, 80–88 % at 2–3 px, 95 % at 3–3.5 px, 100 % from 3.5 px.</item>
/// <item>Dark holds, volumes and shadows: 80–86 % at 1–4 px, 92 % at 4–5 px. Pooled with the mid tones:
/// 74 % at 1–2 px, 83–84 % at 2–4 px, 96 % at 4–5 px.</item>
/// <item>Median refined-corner error: 0.35–0.5 px at 1–2 px of border, 0.15–0.2 px from 3 px on.</item>
/// </list>
/// So there are two levels. Under <see cref="MinBorderPx"/> markers drop out on every wall tone. Under
/// <see cref="RecommendedBorderPx"/> the light wall is still fine, but a marker on a dark hold or volume, where
/// the paper is the only contrast, loses one photo in six.
/// The simulated renders (MountingHoleDecodeTests) agree on the low end: every marker decoded with the tight
/// 1 / 1 mm default; the refined corners drifted 0.8–1.5 px at 1–2 px of border and were as good as a plain
/// print from ~3 px.
/// </summary>
public static class MountingHoleSafety
{
    /// <summary>Under this white border (black edge to cut line, photo px) markers drop out on every wall tone.</summary>
    public const double MinBorderPx = 2.0;

    /// <summary>From this white border (photo px) markers on dark holds and volumes decode like the rest.</summary>
    public const double RecommendedBorderPx = 4.0;

    /// <summary>Marker size in the photos the planner aims for, px (used when the photo scale is unknown).</summary>
    public const double PlannedMarkerPx = 60;

    /// <summary>Smallest white border as a fraction of the marker side when the photo scale is unknown.</summary>
    public const double MinBorderFraction = MinBorderPx / PlannedMarkerPx;

    /// <summary>
    /// White border in mm that spans <paramref name="borderPx"/> photo px on a <paramref name="sizeMm"/> marker
    /// photographed at <paramref name="photoPxPerMm"/> (0 or less = unknown: sized for <see cref="PlannedMarkerPx"/>).
    /// </summary>
    public static double BorderMmFor(double sizeMm, double photoPxPerMm, double borderPx) =>
        photoPxPerMm > 0 ? borderPx / photoPxPerMm : borderPx / PlannedMarkerPx * sizeMm;

    /// <summary>Smallest white border in mm (<see cref="MinBorderPx"/>).</summary>
    public static double MinBorderMm(double sizeMm, double photoPxPerMm) => BorderMmFor(sizeMm, photoPxPerMm, MinBorderPx);

    /// <summary>Recommended white border in mm (<see cref="RecommendedBorderPx"/>).</summary>
    public static double RecommendedBorderMm(double sizeMm, double photoPxPerMm) =>
        BorderMmFor(sizeMm, photoPxPerMm, RecommendedBorderPx);

    /// <summary>The holes' white border in photo px (for a <see cref="PlannedMarkerPx"/> marker when the scale is unknown).</summary>
    public static double BorderPx(double sizeMm, MountingHoles holes, double photoPxPerMm = 0) =>
        MountingHoleLayout.BorderMm(holes) * (photoPxPerMm > 0 ? photoPxPerMm : PlannedMarkerPx / sizeMm);

    /// <summary>Where the holes' border falls against the two measured levels.</summary>
    public static MountingHoleBorder Assess(double sizeMm, MountingHoles holes, double photoPxPerMm = 0)
    {
        var border = MountingHoleLayout.BorderMm(holes);
        return border < MinBorderMm(sizeMm, photoPxPerMm) - 1e-9 ? MountingHoleBorder.TooThin
            : border < RecommendedBorderMm(sizeMm, photoPxPerMm) - 1e-9 ? MountingHoleBorder.Thin
            : MountingHoleBorder.Clear;
    }

    /// <summary>True when the holes keep at least <see cref="MinBorderPx"/> for a <paramref name="sizeMm"/> marker.</summary>
    public static bool IsSafe(double sizeMm, MountingHoles holes, double photoPxPerMm = 0) =>
        Assess(sizeMm, holes, photoPxPerMm) != MountingHoleBorder.TooThin;

    /// <summary>
    /// The smallest gaps (0.5 mm steps, never below the current ones) that give <paramref name="borderPx"/>
    /// (default <see cref="RecommendedBorderPx"/>) for a <paramref name="sizeMm"/> marker, or null when the
    /// allowed gap range cannot reach it. The cut edge moves out first; the holes only when the edge gap alone
    /// cannot make the border.
    /// </summary>
    public static MountingHoles? SafeGaps(
        double sizeMm,
        MountingHoles holes,
        double photoPxPerMm = 0,
        double borderPx = RecommendedBorderPx)
    {
        var r = holes.ScrewHeadDiameterMm / 2;
        var border = BorderMmFor(sizeMm, photoPxPerMm, borderPx);
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
