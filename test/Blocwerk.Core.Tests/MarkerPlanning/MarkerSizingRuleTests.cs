// <copyright file="MarkerSizingRuleTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.MarkerPlanning;
using static Blocwerk.Core.Tests.MarkerPlanning.PlanFixtures;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>The measured sizing rule: decode floors, pose px per metre, steep-view margin, smallest size that works.</summary>
public class MarkerSizingRuleTests
{
    private static readonly MarkerGenerationOptions Options = MarkerGenerationOptions.Default;

    [Fact]
    public void Defaults_AreTheMeasuredThresholds_AndOfferSmallSizes()
    {
        Assert.Equal(MarkerDetectability.CornerMinPx, Options.CornerTargetPx);
        Assert.Equal(MarkerDetectability.FillerMinPx, Options.FillerTargetPx);
        Assert.Equal(30, Options.CornerPxPerMetre);
        Assert.Equal(20, Options.FillerPxPerMetre);
        Assert.Equal(30, Options.AvailableSizesMm[0]);
        Assert.Contains(40, Options.AvailableSizesMm);
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(40, 1.0)]
    [InlineData(50, 1.15)]
    [InlineData(60, 1.3)]
    [InlineData(70, 1.3)]
    public void SteepViewFactor_RampsFrom40To60Degrees(double obliqueness, double factor) =>
        Assert.Equal(factor, MarkerDetectability.SteepViewFactor(obliqueness), 6);

    [Fact]
    public void Close_DecodeFloorBinds_Far_PoseBinds()
    {
        var wall = Rect(0, 3000, 3000);

        Assert.Equal(28, MarkerSizing.RequiredPx(MarkerRole.Corner, wall, UltraWide(500), Options), 6);
        Assert.Equal(22, MarkerSizing.RequiredPx(MarkerRole.Filler, wall, UltraWide(500), Options), 6);
        Assert.Equal(90, MarkerSizing.RequiredPx(MarkerRole.Corner, wall, UltraWide(3000), Options), 6);
        Assert.Equal(60, MarkerSizing.RequiredPx(MarkerRole.Filler, wall, UltraWide(3000), Options), 6);
        Assert.True(MarkerSizing.PoseBinds(MarkerRole.Corner, wall, UltraWide(3000), Options));
        Assert.False(MarkerSizing.PoseBinds(MarkerRole.Corner, wall, UltraWide(500), Options));
    }

    [Fact]
    public void SteepSurface_GetsTheMarginOnTheDecodeFloor()
    {
        var steep = Rect(0, 3000, 3000, overhang: 60);

        Assert.Equal(28 * 1.3, MarkerSizing.DecodePx(MarkerRole.Corner, steep, Options), 6);
        Assert.Equal(22 * 1.3, MarkerSizing.DecodePx(MarkerRole.Filler, steep, Options), 6);
    }

    [Fact]
    public void Forty_mm_FailsFrom3m_OnTheUltraWide_ButIsAFillerFrom1_5m()
    {
        var wall = Rect(0, 3000, 3000);

        Assert.Equal(21.4, MarkerSizing.EstimatedPx(40, wall, UltraWide(3000)), 1);
        Assert.False(MarkerSizing.Works(40, MarkerRole.Filler, wall, UltraWide(3000), Options));
        Assert.True(MarkerSizing.Works(40, MarkerRole.Filler, wall, UltraWide(1500), Options));
        Assert.False(MarkerSizing.Works(40, MarkerRole.Corner, wall, UltraWide(1500), Options));
        Assert.True(MarkerSizing.Works(40, MarkerRole.Corner, wall, Iphone16Pro("1x", 1500), Options));
    }

    [Theory]
    [InlineData("0.5x", 1500, 50, 30)]
    [InlineData("0.5x", 3000, 200, 125)]
    [InlineData("1x", 3000, 80, 50)]
    [InlineData("1.2x", 3000, 60, 40)]
    [InlineData("1.5x", 3000, 50, 40)]
    public void PickSize_IsTheSmallestSizeThatWorks(string lens, double distanceMm, double corner, double filler)
    {
        var wall = Rect(0, 3000, 3000);
        var photo = Iphone16Pro(lens, distanceMm);

        Assert.Equal(corner, MarkerSizing.PickSize(MarkerRole.Corner, wall, photo, Options, out var cornerOk));
        Assert.Equal(filler, MarkerSizing.PickSize(MarkerRole.Filler, wall, photo, Options, out var fillerOk));
        Assert.True(cornerOk && fillerOk);
        var smaller = Options.AvailableSizesMm.Where(s => s < filler).DefaultIfEmpty(0).Max();
        Assert.True(smaller == 0 || !MarkerSizing.Works(smaller, MarkerRole.Filler, wall, photo, Options));
    }

    [Fact]
    public void Explain_NamesTheRejectedSmallerSize_TheCamera_AndTheReason()
    {
        var text = MarkerSizingAdvice.Explain(MarkerRole.Filler, Rect(0, 3000, 3000), UltraWide(3000), Options);

        Assert.StartsWith("Fillers: 125 mm", text);
        Assert.Contains("100 mm would be ≈ 53 px", text);
        Assert.Contains("iPhone 16 Pro 0.5×", text);
        Assert.Contains("position accuracy from 3.0 m", text);
        Assert.Contains("125 mm is the smallest that works", text);
    }

    [Fact]
    public void Explain_SaysDecoding_WhenThePoseTargetIsBelowTheFloor()
    {
        var text = MarkerSizingAdvice.Explain(MarkerRole.Filler, Rect(0, 3000, 3000), UltraWide(1000), Options);

        Assert.Contains("smallest printable size already reaches the 22 px a filler needs to decode reliably", text);
    }

    [Fact]
    public void CloseUpHint_WarnsWhenACloseUpSeesOnlyOneMarker()
    {
        Assert.Null(MarkerSizingAdvice.CloseUpHint(UltraWide(3000)));
        var hint = MarkerSizingAdvice.CloseUpHint(UltraWide(3000) with { NearestDistanceMm = 800 })!;

        Assert.Contains("Close-ups from 0.8 m", hint);
        Assert.Contains("may see only one", hint);
    }

    private static PhotoSetup UltraWide(double mm) => Iphone16Pro("0.5x", mm);

    private static PhotoSetup Iphone16Pro(string lens, double mm)
    {
        var phone = PhoneCameraCatalog.Find("iphone-16-pro")!;
        return MarkerCameraPresets.ForPhone(phone, phone.Lens(lens)!, mm);
    }
}
