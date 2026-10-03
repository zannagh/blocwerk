// <copyright file="PhoneCameraCatalogTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>Sanity of the phone camera table, and that it agrees with the owner's real iPhone 16 Pro photos.</summary>
public class PhoneCameraCatalogTests
{
    [Fact]
    public void EveryLens_HasAPlausibleFov_Resolution_AndSource()
    {
        Assert.All(PhoneCameraCatalog.All.SelectMany(p => p.Lenses.Select(l => (p, l))), t =>
        {
            Assert.InRange(t.l.HorizontalFovDeg, 5, 125);
            Assert.InRange(t.l.ImageLongEdgePx, 3000, 8200);
            Assert.InRange(t.l.Equivalent35mm, 10, 250);
            Assert.False(string.IsNullOrWhiteSpace(t.l.Source));
            Assert.Matches("^[0-9]{1,2}(\\.[0-9]{1,2})?x$", t.l.Id);
        });
    }

    [Fact]
    public void Ids_AreUnique_EveryPhoneHasA1xLens_AndLensesGoWideToNarrow()
    {
        Assert.Equal(PhoneCameraCatalog.All.Count, PhoneCameraCatalog.All.Select(p => p.Id).Distinct().Count());
        Assert.All(PhoneCameraCatalog.All, p =>
        {
            Assert.NotNull(p.Lens("1x"));
            Assert.Equal(p.Lenses.Count, p.Lenses.Select(l => l.Id).Distinct().Count());
            Assert.Equal(p.Lenses.OrderByDescending(l => l.HorizontalFovDeg).Select(l => l.Id), p.Lenses.Select(l => l.Id));
        });
        Assert.Contains("Apple", PhoneCameraCatalog.Brands);
        Assert.Contains("Google", PhoneCameraCatalog.Brands);
        Assert.Contains("Samsung", PhoneCameraCatalog.Brands);
    }

    [Theory]
    [InlineData("iphone-12", "0.5x")]
    [InlineData("iphone-15-pro", "3x")]
    [InlineData("iphone-15-pro-max", "5x")]
    [InlineData("iphone-16-pro", "1.5x")]
    [InlineData("iphone-17-pro", "4x")]
    [InlineData("pixel-9-pro", "5x")]
    [InlineData("galaxy-s24-ultra", "0.6x")]
    public void TheMarketedLenses_AreThere(string phone, string lens) => Assert.NotNull(PhoneCameraCatalog.FindLens(phone, lens));

    [Fact]
    public void Iphone16Pro_MatchesTheOwnersPhotos()
    {
        var uw = PhoneCameraCatalog.FindLens("iphone-16-pro", "0.5x")!;
        var crop = PhoneCameraCatalog.FindLens("iphone-16-pro", "1.2x")!;

        // 13 ultra-wide photos, 4032 × 3024, solved focal 1605 px.
        Assert.Equal(4032, uw.ImageLongEdgePx);
        Assert.InRange(uw.FocalPx, 1605 * 0.99, 1605 * 1.01);

        // The 28 mm shot is saved at 24 MP (5712 × 4284); EXIF says 28 mm.
        Assert.Equal(5712, crop.ImageLongEdgePx);
        Assert.Equal(28, crop.Equivalent35mm);
        Assert.InRange(crop.HorizontalFovDeg, 60, 65);
    }

    [Fact]
    public void Main24MpSensor_DefaultsTo24MP_ButTheUltraWideAnd2xStayAt12MP()
    {
        var phone = PhoneCameraCatalog.Find("iphone-16-pro")!;

        Assert.Equal(5712, phone.Lens("1x")!.ImageLongEdgePx);
        Assert.Equal(4032, phone.Lens("0.5x")!.ImageLongEdgePx);
        Assert.Equal(4032, phone.Lens("2x")!.ImageLongEdgePx);
        Assert.Equal(4032, PhoneCameraCatalog.FindLens("iphone-14-pro", "1x")!.ImageLongEdgePx);
    }

    [Theory]
    [InlineData(24, 71.6)]
    [InlineData(26, 67.3)]
    [InlineData(77, 25.3)]
    public void FovFromEquivalent_UsesThe4To3LongSideOfTheFilmDiagonal(double eq, double fov) =>
        Assert.Equal(fov, PhoneLens.FovFromEquivalent(eq), 1);

    [Fact]
    public void LegacyPresets_MapToTheGenericPhone_Unchanged()
    {
        var oneX = MarkerCameraPresets.Create(MarkerCameraPresets.Phone1X, 2000);
        var uw = MarkerCameraPresets.Create(MarkerCameraPresets.PhoneUltraWide, 2000);

        var r1 = PhoneCameraCatalog.Resolve(oneX)!.Value;
        var r2 = PhoneCameraCatalog.Resolve(uw)!.Value;

        Assert.Equal(PhoneCameraCatalog.GenericPhoneId, r1.Phone.Id);
        Assert.Equal((oneX.HorizontalFovDeg, oneX.ImageLongEdgePx), (r1.Lens.HorizontalFovDeg, r1.Lens.ImageLongEdgePx));
        Assert.Equal((uw.HorizontalFovDeg, uw.ImageLongEdgePx), (r2.Lens.HorizontalFovDeg, r2.Lens.ImageLongEdgePx));
        Assert.Null(PhoneCameraCatalog.Resolve(MarkerCameraPresets.Create(MarkerCameraPresets.Custom, 2000)));
    }

    [Fact]
    public void ForPhone_CopiesTheLens_AndKeepsTheNearestLegacyPresetName()
    {
        var phone = PhoneCameraCatalog.Find("pixel-9-pro")!;

        var uw = MarkerCameraPresets.ForPhone(phone, phone.Lens("0.5x")!, 3000, 1200);
        var tele = MarkerCameraPresets.ForPhone(phone, phone.Lens("5x")!, 3000);

        Assert.Equal((MarkerCameraPresets.PhoneUltraWide, "pixel-9-pro", "0.5x", 1200.0), (uw.CameraPreset, uw.PhoneModel, uw.Lens, uw.NearestDistanceMm!.Value));
        Assert.Equal(MarkerCameraPresets.Phone1X, tele.CameraPreset);
        Assert.Equal(phone.Lens("5x")!.HorizontalFovDeg, tele.HorizontalFovDeg);
    }

    [Fact]
    public void CustomFromMegapixels_Assumes4To3() =>
        Assert.InRange(MarkerCameraPresets.CustomFromMegapixels(2000, 70, 12.19).ImageLongEdgePx, 4027, 4037);
}
