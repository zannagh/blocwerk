// <copyright file="MarkerSizing.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// How big a printed marker appears in a photo taken at the plan's distance.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scale.</b> A pinhole camera at distance <c>d</c> with horizontal field of view <c>hfov</c> sees a
/// strip <c>2·d·tan(hfov/2)</c> mm wide across the photo's long edge (we assume the long edge is
/// horizontal — landscape shots), so <c>pxPerMm = ImageLongEdgePx / (2·d·tan(hfov/2))</c>. Example: a
/// phone ultra-wide (104°, 4032 px) at 2.5 m sees 6.40 m → 0.63 px/mm → a 125 mm marker is ≈ 79 px.
/// </para>
/// <para>
/// <b>Foreshortening.</b> The owner stands in front of the ROOT segment and looks horizontally. A
/// segment tilted by its overhang θ (from vertical) is squashed by cos θ along its slope; one turned by
/// its yaw ψ relative to the root is squashed by cos ψ across. Detection is limited by the shorter
/// side, so the effective side is <c>size · pxPerMm · min(cos θ, cos ψ)</c> — conservative, since only
/// one direction shrinks. Beyond <see cref="GrazingLimitDeg"/> of obliqueness the marker is too
/// edge-on to decode from the standing position at all; the plan then tells the owner to photograph
/// that surface face-on, and sizes its markers for a face-on shot (factor 1) rather than demanding
/// absurdly large prints.
/// </para>
/// <para>
/// <b>Slabs.</b> A slab is a negative θ; cos is even, so a 15° slab foreshortens like a 15° overhang.
/// That is the right first-order answer from standing height: the squash comes from the angle
/// between the viewing ray and the surface normal, and at eye level that angle is |θ| either way.
/// What the model ignores is the viewing ray's elevation, for EVERY surface: a marker above eye level
/// on a slab is seen more obliquely (|θ| plus the elevation), one on an overhang less, one below eye
/// level the other way round — and a vertical wall's top is oblique too. A slab's top also sits
/// <c>h·sin θ</c> further away. None of that is specific to the sign, so the model stays symmetric;
/// the photo overlap (every corner in ≥3 photos from different spots) is what covers it.
/// </para>
/// <para>
/// <b>Photo footprint.</b> <c>2·d·tan(hfov/2)</c> wide and ¾ of that tall (4:3 sensor). Spacing markers
/// at most half the SHORTER footprint side apart means every photo framed on the wall sees several.
/// </para>
/// </remarks>
public static class MarkerSizing
{
    /// <summary>Obliqueness (degrees) beyond which a surface must be photographed face-on.</summary>
    public const double GrazingLimitDeg = 72.0;

    /// <summary>Photo aspect: the short side as a fraction of the long side (4:3 sensors).</summary>
    public const double ShortSideRatio = 0.75;

    /// <summary>Pixels per millimetre of a surface facing the camera at the plan's distance.</summary>
    public static double PxPerMm(PhotoSetup photo) => photo.ImageLongEdgePx / FootprintWidthMm(photo);

    /// <summary>Width of wall one photo covers at the plan's distance (long edge horizontal).</summary>
    public static double FootprintWidthMm(PhotoSetup photo) =>
        2 * photo.DistanceMm * Math.Tan(photo.HorizontalFovDeg * Math.PI / 360.0);

    /// <summary>Height of wall one photo covers (¾ of the width).</summary>
    public static double FootprintHeightMm(PhotoSetup photo) => FootprintWidthMm(photo) * ShortSideRatio;

    /// <summary>Largest gap between neighbouring markers so any photo still sees several.</summary>
    public static double MaxSpacingMm(PhotoSetup photo) => FootprintHeightMm(photo) / 2;

    /// <summary>How far the segment is turned away from the standing camera, in degrees (0 = face-on).</summary>
    public static double ObliquenessDeg(PlanSegment segment)
    {
        var cos = Math.Min(Math.Cos(ToRad(segment.OverhangDeg)), Math.Cos(ToRad(segment.YawDeg)));
        return Math.Acos(Math.Clamp(cos, -1, 1)) * 180.0 / Math.PI;
    }

    /// <summary>True when the segment is too edge-on for the standing camera (shoot it face-on).</summary>
    public static bool IsGrazing(PlanSegment segment) => ObliquenessDeg(segment) > GrazingLimitDeg;

    /// <summary>
    /// The foreshortening factor <c>min(cos θ, cos ψ)</c>, or 1 for a grazing surface (it will be
    /// photographed face-on).
    /// </summary>
    public static double Foreshortening(PlanSegment segment) =>
        IsGrazing(segment)
            ? 1.0
            : Math.Min(Math.Cos(ToRad(segment.OverhangDeg)), Math.Cos(ToRad(segment.YawDeg)));

    /// <summary>Expected on-photo side (px) of a <paramref name="sizeMm"/> marker on the segment.</summary>
    public static double EstimatedPx(double sizeMm, PlanSegment segment, PhotoSetup photo) =>
        sizeMm * PxPerMm(photo) * Foreshortening(segment);

    /// <summary>The printed side (mm) needed to reach <paramref name="targetPx"/> on the segment.</summary>
    public static double RequiredSizeMm(double targetPx, PlanSegment segment, PhotoSetup photo) =>
        targetPx / (PxPerMm(photo) * Foreshortening(segment));

    /// <summary>
    /// The smallest available size reaching <paramref name="targetPx"/>; when none does, the largest
    /// (and <paramref name="meetsTarget"/> is false).
    /// </summary>
    public static double PickSize(
        double targetPx,
        PlanSegment segment,
        PhotoSetup photo,
        IReadOnlyList<double> sizesMm,
        out bool meetsTarget)
    {
        var needed = RequiredSizeMm(targetPx, segment, photo);
        var sorted = sizesMm.Where(s => double.IsFinite(s) && s > 0).Order().ToList();
        if (sorted.Count == 0)
        {
            meetsTarget = false;
            return Math.Ceiling(needed / 5) * 5;
        }

        foreach (var size in sorted)
        {
            if (size >= needed - 1e-9)
            {
                meetsTarget = true;
                return size;
            }
        }

        meetsTarget = false;
        return sorted[^1];
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
}
