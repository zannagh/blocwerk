// <copyright file="MatchGateSizeTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.HoldDetection.Matching;

namespace Blocwerk.HoldDetection.Tests.Matching;

/// <summary>
/// The matcher's size gate: an old hold is paired with the nearest detection of ITS size, so a hold on a volume and
/// the volume, which share a spot, are never taken for each other.
/// </summary>
public class MatchGateSizeTests
{
    [Theory]
    [InlineData(0.02, 0.1, true)] // a small hold against the volume it sits on
    [InlineData(0.1, 0.02, true)] // the volume against the small hold
    [InlineData(0.02, 0.045, false)] // the same hold, photographed and outlined a little differently
    [InlineData(0.02, 0.02, false)]
    public void ClearlyDifferentSizes_AreNotTheSameHold(double oldSize, double newSize, bool other)
    {
        Assert.Equal(other, MatchGate.OtherSize(oldSize, newSize));
    }

    [Fact]
    public void AnUnknownSize_NeverGates()
    {
        Assert.False(MatchGate.OtherSize(null, 0.1));
        Assert.False(MatchGate.OtherSize(0.02, null));
        Assert.False(MatchGate.OtherSize(0, 0.1));
    }

    [Fact]
    public void TheSmallHoldOnAVolume_TakesTheNextDetectionOfItsSize_AndNoneWhenThereIsOnlyTheVolume()
    {
        // The volume's centre is nearest, the small hold's own detection second.
        Assert.Equal(1, MatchGate.NearestOfItsSize(0.02, [0.1, 0.021]));
        Assert.Equal(0, MatchGate.NearestOfItsSize(0.1, [0.1, 0.021]));

        // The small hold was not detected: it is not carried onto the volume.
        Assert.Equal(-1, MatchGate.NearestOfItsSize(0.02, [0.1, 0.12]));
        Assert.Equal(-1, MatchGate.NearestOfItsSize(0.02, [0.1]));
    }

    [Fact]
    public void AHoldsSize_IsItsOutlinesWhenItHasOne_SoAHandDrawnVolumeWithASmallRadiusIsStillLarge()
    {
        var drawn = new MatcherHold(0, 0.5, 0.5, 0.02, Shape: [(0.4, 0.45), (0.6, 0.45), (0.6, 0.55), (0.4, 0.55)]);
        var plain = new MatcherHold(1, 0.5, 0.5, 0.02);

        // 0.2 of 2000 px wide and 0.1 of 1000 px high: 400 px across, half of it over the long side.
        Assert.Equal(0.1, MatchGate.SizeOf(drawn, 2000, 1000)!.Value, 6);
        Assert.Equal(0.02, MatchGate.SizeOf(plain, 2000, 1000));
        Assert.False(MatchGate.OtherSize(MatchGate.SizeOf(drawn, 2000, 1000), 0.11));
    }
}
