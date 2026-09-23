// <copyright file="PhoneCameraCatalog.Android.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// Recent Pixel and Galaxy S phones. Pixels save 12.5 MP (4080 px) from every lens by default (the 50 MP
/// mode is opt-in); Google quotes diagonal fields of view. Galaxies save 12 MP (4000 px) by default, the
/// 10 MP 3× telephotos 3648 px; Samsung quotes 35 mm equivalents and labels the ultra-wide 0.6×. None of
/// these rows has been checked against a real photo yet.
/// </summary>
public static partial class PhoneCameraCatalog
{
    private const string GoogleSpecs = "Google Store tech specs (diagonal FOV); 12.5 MP default";
    private const string SamsungSpecs = "Samsung spec sheet (35 mm eq.); 12 MP default";

    private static IEnumerable<PhoneCamera> Google() =>
    [
        Pixel("pixel-8", "Pixel 8", 125.8, withTele: false),
        Pixel("pixel-8-pro", "Pixel 8 Pro", 125.5, withTele: true),
        Pixel("pixel-9", "Pixel 9", 123, withTele: false),
        Pixel("pixel-9-pro", "Pixel 9 Pro", 123, withTele: true),
        Pixel("pixel-9-pro-xl", "Pixel 9 Pro XL", 123, withTele: true),
    ];

    private static IEnumerable<PhoneCamera> Samsung() =>
    [
        Galaxy("galaxy-s23", "Galaxy S23", 24),
        Galaxy("galaxy-s24", "Galaxy S24", 24),
        Galaxy("galaxy-s25", "Galaxy S25", 24),
        GalaxyUltra("galaxy-s23-ultra", "Galaxy S23 Ultra", PhoneLens.FromEquivalent("10x", "Telephoto", 230, 3648, $"{SamsungSpecs} (10 MP)")),
        GalaxyUltra("galaxy-s24-ultra", "Galaxy S24 Ultra", PhoneLens.FromEquivalent("5x", "Telephoto", 111, 4000, SamsungSpecs)),
        GalaxyUltra("galaxy-s25-ultra", "Galaxy S25 Ultra", PhoneLens.FromEquivalent("5x", "Telephoto", 111, 4000, SamsungSpecs)),
    ];

    private static PhoneCamera Pixel(string id, string model, double ultraWideDiagDeg, bool withTele)
    {
        List<PhoneLens> lenses =
        [
            PhoneLens.FromDiagonal("0.5x", "Ultra-wide", ultraWideDiagDeg, 4080, GoogleSpecs),
            PhoneLens.FromDiagonal("1x", "Main", 82, 4080, GoogleSpecs),
            PhoneLens.FromEquivalent("2x", "Main, 2× crop", 50, 4080, $"{GoogleSpecs} (centre crop of the 50 MP main)"),
        ];
        if (withTele)
        {
            lenses.Add(PhoneLens.FromDiagonal("5x", "Telephoto", 22, 4080, GoogleSpecs));
        }

        return new PhoneCamera(id, "Google", model, lenses);
    }

    private static PhoneCamera Galaxy(string id, string model, double mainEq) =>
        new(id, "Samsung", model,
        [
            PhoneLens.FromEquivalent("0.6x", "Ultra-wide", 13, 4000, SamsungSpecs),
            PhoneLens.FromEquivalent("1x", "Main", mainEq, 4000, SamsungSpecs),
            PhoneLens.FromEquivalent("2x", "Main, 2× crop", 2 * mainEq, 4000, $"{SamsungSpecs} (centre crop of the 50 MP main)"),
            PhoneLens.FromEquivalent("3x", "Telephoto", 67, 3648, $"{SamsungSpecs} (10 MP)"),
        ]);

    private static PhoneCamera GalaxyUltra(string id, string model, PhoneLens longTele) =>
        new(id, "Samsung", model,
        [
            PhoneLens.FromEquivalent("0.6x", "Ultra-wide", 13, 4000, SamsungSpecs),
            PhoneLens.FromEquivalent("1x", "Main", 23, 4000, $"{SamsungSpecs} (binned 200 MP main)"),
            PhoneLens.FromEquivalent("2x", "Main, 2× crop", 46, 4000, $"{SamsungSpecs} (centre crop of the 200 MP main)"),
            PhoneLens.FromEquivalent("3x", "Telephoto", 67, 3648, $"{SamsungSpecs} (10 MP)"),
            longTele,
        ]);
}
