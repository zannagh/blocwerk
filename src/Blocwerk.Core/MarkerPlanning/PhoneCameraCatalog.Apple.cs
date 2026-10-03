// <copyright file="PhoneCameraCatalog.Apple.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// iPhones 12–17. Equivalent focal lengths from Apple's tech specs; the saved photo's size is the Camera
/// app default (HEIF/JPEG, not ProRAW): 12 MP everywhere up to the 14 Pro, then 24 MP on the 48 MP main
/// sensor (15 and later), while the ultra-wide, 2× and the telephotos stay at 12 MP.
/// </summary>
public static partial class PhoneCameraCatalog
{
    private const string AppleSpecs = "Apple tech specs; Camera app default resolution";

    // Apple markets the ultra-wide as 13 mm; the iPhone 16 Pro writes 14 mm into EXIF and the wall solve of
    // 13 of its photos measured f = 1605 px on 4032 → 103.0°. Used for every iPhone ultra-wide.
    private static PhoneLens IPhoneUltraWide => new(
        "0.5x", "Ultra-wide", 13, 103.0, 4032, "Measured: iPhone 16 Pro, 13 photos, solved f = 1605 px (EXIF 14 mm eq.)");

    private static IEnumerable<PhoneCamera> Apple() =>
    [
        IPhone("iphone-12", "iPhone 12", Main12(26), Tele(52, "2x")),
        IPhone("iphone-12-mini", "iPhone 12 mini", Main12(26)),
        IPhone("iphone-12-pro", "iPhone 12 Pro", Main12(26), Tele(52, "2x")),
        IPhone("iphone-12-pro-max", "iPhone 12 Pro Max", Main12(26), Tele(65, "2.5x")),
        IPhone("iphone-13", "iPhone 13", Main12(26)),
        IPhone("iphone-13-mini", "iPhone 13 mini", Main12(26)),
        IPhone("iphone-13-pro", "iPhone 13 Pro", Main12(26), Tele(77, "3x")),
        IPhone("iphone-13-pro-max", "iPhone 13 Pro Max", Main12(26), Tele(77, "3x")),
        IPhone("iphone-14", "iPhone 14", Main12(26)),
        IPhone("iphone-14-plus", "iPhone 14 Plus", Main12(26)),
        IPhone("iphone-14-pro", "iPhone 14 Pro", Main12(24), Crop2X(48), Tele(77, "3x")),
        IPhone("iphone-14-pro-max", "iPhone 14 Pro Max", Main12(24), Crop2X(48), Tele(77, "3x")),
        IPhone("iphone-15", "iPhone 15", Main24(26), Crop2X(52)),
        IPhone("iphone-15-plus", "iPhone 15 Plus", Main24(26), Crop2X(52)),
        IPhone("iphone-15-pro", "iPhone 15 Pro", [.. ProMain(), Tele(77, "3x")]),
        IPhone("iphone-15-pro-max", "iPhone 15 Pro Max", [.. ProMain(), Tele(120, "5x")]),
        IPhone("iphone-16", "iPhone 16", Main24(26), Crop2X(52)),
        IPhone("iphone-16-plus", "iPhone 16 Plus", Main24(26), Crop2X(52)),
        new PhoneCamera("iphone-16e", "Apple", "iPhone 16e", [Main24(26), Crop2X(52)]),
        IPhone("iphone-16-pro", "iPhone 16 Pro", [.. ProMain(), Tele(120, "5x")]),
        IPhone("iphone-16-pro-max", "iPhone 16 Pro Max", [.. ProMain(), Tele(120, "5x")]),
        IPhone("iphone-17", "iPhone 17", Main24(26), Crop2X(52)),
        new PhoneCamera("iphone-air", "Apple", "iPhone Air", [Main24(26), Crop2X(52)]),
        IPhone("iphone-17-pro", "iPhone 17 Pro", [.. ProMain(), Tele(100, "4x"), Tele(200, "8x")]),
        IPhone("iphone-17-pro-max", "iPhone 17 Pro Max", [.. ProMain(), Tele(100, "4x"), Tele(200, "8x")]),
    ];

    private static PhoneCamera IPhone(string id, string model, params PhoneLens[] lenses) =>
        new(id, "Apple", model, [IPhoneUltraWide, .. lenses]);

    private static PhoneLens Main12(double eq) => PhoneLens.FromEquivalent("1x", "Main", eq, 4032, AppleSpecs);

    private static PhoneLens Main24(double eq) =>
        PhoneLens.FromEquivalent("1x", "Main", eq, 5712, $"{AppleSpecs} (24 MP default on the 48 MP main)");

    private static PhoneLens Crop2X(double eq) =>
        PhoneLens.FromEquivalent("2x", "Main, 2× crop", eq, 4032, $"{AppleSpecs} (centre crop of the 48 MP main)");

    private static PhoneLens Tele(double eq, string id) => PhoneLens.FromEquivalent(id, "Telephoto", eq, 4032, AppleSpecs);

    /// <summary>
    /// The Pro main camera from the 15 Pro on: 24 mm at 24 MP plus the 28 mm (1.2×) and 35 mm (1.5×)
    /// presets, which are crops of the same sensor still saved at 24 MP, and the 2× (48 mm) crop at 12 MP.
    /// </summary>
    private static PhoneLens[] ProMain() =>
    [
        PhoneLens.FromEquivalent("1x", "Main", 24, 5712, $"{AppleSpecs} (24 MP default)"),
        PhoneLens.FromEquivalent(
            "1.2x",
            "Main, 28 mm crop",
            28,
            5712,
            "Apple tech specs; confirmed on the iPhone 16 Pro: EXIF 28 mm, 5712 × 4284 px (solve: 60.2°, one photo only)"),
        PhoneLens.FromEquivalent("1.5x", "Main, 35 mm crop", 35, 5712, $"{AppleSpecs} (35 mm preset, saved at 24 MP)"),
        Crop2X(48),
    ];
}
