// <copyright file="MarkerDetectability.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// How many photo pixels a marker needs, measured on the owner's real wall photos (sizing study,
/// 2026-09-23): 84 real marker observations from 14 iPhone photos, each shrunk to 12–80 px with a
/// phone-like chain (area downsample, lens blur, unsharp mask, noise, JPEG q85), given a one-module white
/// quiet zone like the planner's print, and run through the app's detector and corner refiner.
/// </summary>
/// <remarks>
/// <para>
/// <b>Decoding.</b> OpenCV decodes such markers from ~15 px, but the app's validator rejects anything
/// under <c>MarkerDetectionOptions.MinSidePx</c> = 20 px mean side (real false positives were ~12 px).
/// With the quiet zone intact, a 21 px short side decoded and was accepted in 100% of the trials up to a
/// 55° view angle and in ≥ 97% up to 65°; between 65° and 72° acceptance wobbled (83–97%) until ~29 px.
/// </para>
/// <para>
/// <b>Pose.</b> Accepted markers' refined corners were 0.2 px (median) / 0.3 px (p90) off from 21 px
/// up, 0.3–0.7 px at 55–72°. Re-solving the owner's wall (ultra-wide, 1–4 m) with every marker shrunk to
/// 30–100 mm and that measured noise added: facet angles stayed within 0.5° down to 50 mm, but marker
/// positions drifted like σ·Z/s — 4.8 mm RMS at 80 mm, 5.5 at 60, 7.8 at 50, 10.5 at 40 (vs 1.9 for the
/// 125 mm control). So beyond the decode floor a marker needs px in proportion to the photo distance.
/// </para>
/// <para>
/// <b>Quiet zone.</b> The owner's 125 mm test markers had a 3–7% white border; on a dark wall that border
/// fell under ~4 photo px long before the marker itself was too small, and those markers failed at any
/// size. The planner's print keeps a full module (side/6); with mounting holes the border is checked by
/// <see cref="MountingHoleSafety"/>.
/// </para>
/// </remarks>
public static class MarkerDetectability
{
    /// <summary>Short-side px a filler needs (≥ 95% decoded, face-on to 55°): the 20 px floor plus margin.</summary>
    public const double FillerMinPx = 22;

    /// <summary>
    /// Short-side px a corner needs. Corners anchor a surface and must decode in every photo that shows
    /// that corner, including the frame edges of a wide shot, so they keep a 25% margin over the fillers.
    /// </summary>
    public const double CornerMinPx = 28;

    /// <summary>
    /// Pose accuracy for corners: short-side px per metre of photo distance. A marker's position error in
    /// the re-solves grew like σ·Z/s (corner noise σ px, distance Z, side s px); 30 px/m keeps it ≈ 5 mm,
    /// about what the 125 mm solve achieves today (marker sides ± 6.4 mm, facet angle ± 0.3°).
    /// </summary>
    public const double CornerPosePxPerMetre = 30;

    /// <summary>
    /// Pose accuracy for fillers: 20 px/m (≈ 7–8 mm per marker, facet angles still within 0.5° in the
    /// re-solves); fillers link photos and are redundant, the corners carry the surface's frame.
    /// </summary>
    public const double FillerPosePxPerMetre = 20;

    /// <summary>Obliqueness up to which no extra margin is needed.</summary>
    public const double SteepFromDeg = 40;

    /// <summary>Obliqueness at which the full steep-view margin applies.</summary>
    public const double SteepFullDeg = 60;

    /// <summary>
    /// Extra px factor for a steeply seen surface: a segment turned θ from the camera is seen at θ plus
    /// the lens's off-axis angle toward the frame edges, and acceptance only settled from ~29 px at 65–72°.
    /// </summary>
    public const double SteepMarginFactor = 1.3;

    /// <summary>
    /// The margin factor for a surface <paramref name="obliquenessDeg"/> from face-on: 1 up to
    /// <see cref="SteepFromDeg"/>, rising linearly to <see cref="SteepMarginFactor"/> at <see cref="SteepFullDeg"/>.
    /// </summary>
    public static double SteepViewFactor(double obliquenessDeg)
    {
        if (!double.IsFinite(obliquenessDeg) || obliquenessDeg <= SteepFromDeg)
        {
            return 1.0;
        }

        var t = Math.Min(1.0, (obliquenessDeg - SteepFromDeg) / (SteepFullDeg - SteepFromDeg));
        return 1.0 + (t * (SteepMarginFactor - 1.0));
    }
}
