// <copyright file="MarkerCameraPresets.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// Camera presets for <see cref="PhotoSetup.CameraPreset"/>. The field of view is what the sizing
/// maths needs; "custom" keeps whatever the owner typed. Phone models and their lenses live in
/// <see cref="PhoneCameraCatalog"/>; the two legacy presets are its generic phone's lenses.
/// </summary>
public static class MarkerCameraPresets
{
    /// <summary>A phone's main camera (≈ 26 mm equivalent): ≈ 69° horizontal, 4032 px long edge.</summary>
    public const string Phone1X = "phone-1x";

    /// <summary>
    /// A phone's ultra-wide (0.5×, ≈ 13 mm equivalent): ≈ 104° horizontal, 4032 px. iPhones correct its
    /// barrel distortion in the saved JPEG, so it behaves like a (wide) pinhole camera.
    /// </summary>
    public const string PhoneUltraWide = "phone-0.5x";

    /// <summary>Owner-supplied field of view and resolution.</summary>
    public const string Custom = "custom";

    /// <summary>All preset names, in the order the UI should offer them.</summary>
    public static IReadOnlyList<string> All { get; } = [Phone1X, PhoneUltraWide, Custom];

    /// <summary>
    /// A photo setup for <paramref name="preset"/> at <paramref name="distanceMm"/>; unknown presets and
    /// "custom" fall back to <paramref name="customFovDeg"/> / <paramref name="customLongEdgePx"/>.
    /// </summary>
    public static PhotoSetup Create(
        string preset,
        double distanceMm,
        double customFovDeg = 69.0,
        int customLongEdgePx = 4032) => preset switch
        {
            Phone1X => new PhotoSetup(distanceMm, Phone1X, 69.0, 4032),
            PhoneUltraWide => new PhotoSetup(distanceMm, PhoneUltraWide, 104.0, 4032),
            _ => new PhotoSetup(distanceMm, Custom, customFovDeg, customLongEdgePx),
        };

    /// <summary>Fields of view at or above this count as an ultra-wide for the legacy preset name.</summary>
    public const double UltraWideFovDeg = 90;

    /// <summary>
    /// A photo setup for a phone's lens from <see cref="PhoneCameraCatalog"/>: its FOV and resolution, the
    /// model and lens ids, and the nearest legacy preset name for older readers.
    /// </summary>
    public static PhotoSetup ForPhone(PhoneCamera phone, PhoneLens lens, double distanceMm, double? nearestDistanceMm = null) =>
        new(
            distanceMm,
            lens.HorizontalFovDeg >= UltraWideFovDeg ? PhoneUltraWide : Phone1X,
            lens.HorizontalFovDeg,
            lens.ImageLongEdgePx,
            phone.Id,
            lens.Id,
            nearestDistanceMm);

    /// <summary>
    /// Owner-typed camera: field of view and megapixels (4:3 assumed, so long edge = √(MP·10⁶·4/3)).
    /// </summary>
    public static PhotoSetup CustomFromMegapixels(double distanceMm, double horizontalFovDeg, double megapixels, double? nearestDistanceMm = null) =>
        new(distanceMm, Custom, horizontalFovDeg, LongEdgeFromMegapixels(megapixels), NearestDistanceMm: nearestDistanceMm);

    /// <summary>Long edge in px of a 4:3 photo with <paramref name="megapixels"/>.</summary>
    public static int LongEdgeFromMegapixels(double megapixels) =>
        (int)Math.Round(Math.Sqrt(megapixels * 1e6 * 4 / 3));
}
