// <copyright file="MarkerCameraPresets.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// Camera presets for <see cref="PhotoSetup.CameraPreset"/>. The field of view is what the sizing
/// maths needs; "custom" keeps whatever the owner typed.
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
}
