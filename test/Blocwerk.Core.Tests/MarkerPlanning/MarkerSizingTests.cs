// <copyright file="MarkerSizingTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.MarkerPlanning;
using static Blocwerk.Core.Tests.MarkerPlanning.PlanFixtures;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>The pinhole sizing maths, pinned on the Attic's 2.5 m / ultra-wide setup.</summary>
public class MarkerSizingTests
{
    [Fact]
    public void UltraWideAt2_5m_SeesAbout6_4mAndResolves0_63PxPerMm()
    {
        var photo = MarkerCameraPresets.Create(MarkerCameraPresets.PhoneUltraWide, 2500);

        Assert.Equal(6400, MarkerSizing.FootprintWidthMm(photo), 0);
        Assert.Equal(4800, MarkerSizing.FootprintHeightMm(photo), 0);
        Assert.Equal(0.630, MarkerSizing.PxPerMm(photo), 3);
        Assert.Equal(2400, MarkerSizing.MaxSpacingMm(photo), 0);
    }

    [Fact]
    public void Overhang_Foreshortens_AndPushesCornersFrom125To150mm()
    {
        var photo = MarkerCameraPresets.Create(MarkerCameraPresets.PhoneUltraWide, 2500);
        var overhang = Rect(0, 5000, 3000, overhang: 45);

        Assert.Equal(55.7, MarkerSizing.EstimatedPx(125, overhang, photo), 1);
        Assert.Equal(150, MarkerSizing.PickSize(60, overhang, photo, MarkerGenerationOptions.Default.AvailableSizesMm, out var meets));
        Assert.True(meets);
        Assert.Equal(100, MarkerSizing.PickSize(40, overhang, photo, MarkerGenerationOptions.Default.AvailableSizesMm, out _));
    }

    [Fact]
    public void GrazingSurface_IsSizedForAFaceOnShot()
    {
        var sideWall = Rect(0, 2000, 2000, yaw: 90);

        Assert.True(MarkerSizing.IsGrazing(sideWall));
        Assert.Equal(1.0, MarkerSizing.Foreshortening(sideWall));
        Assert.False(MarkerSizing.IsGrazing(Rect(0, 2000, 2000, overhang: 70)));
        Assert.True(MarkerSizing.IsGrazing(Rect(0, 2000, 2000, overhang: 85)));
    }

    [Fact]
    public void NoSizeReachesTheTarget_ReturnsTheLargest_AndSaysSo()
    {
        var far = MarkerCameraPresets.Create(MarkerCameraPresets.PhoneUltraWide, 15000);

        var size = MarkerSizing.PickSize(60, Rect(0, 1000, 1000), far, [50, 100], out var meets);

        Assert.Equal(100, size);
        Assert.False(meets);
    }
}
