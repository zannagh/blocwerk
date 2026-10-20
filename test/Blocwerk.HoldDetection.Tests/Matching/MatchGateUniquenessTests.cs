// <copyright file="MatchGateUniquenessTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.HoldDetection.Matching;

namespace Blocwerk.HoldDetection.Tests.Matching;

/// <summary>
/// An old hold whose predicted centre is not on the chosen detection, with another detection about as near, is a toss-up
/// between two neighbours: declined, so the old hold does not "jump" to its neighbour and report a move (The Attic, gen 5 →
/// 6: all 8 such pairs of 961 were false moves). A pick on the hold, or a clearly nearest one, stays.
/// </summary>
public class MatchGateUniquenessTests
{
    [Theory]
    [InlineData(44, 48, 46, true)] // M3: 44 px off a 46 px detection, the next one at 48 px
    [InlineData(28, 25, 21, true)] // M12: the size rule picked the farther one; the nearer is 25 px off
    [InlineData(57, 113, 60, false)] // clearly the nearest
    [InlineData(10, 11, 40, false)] // on the detection (within half its radius)
    [InlineData(11, 12, 8, false)] // within the on-hold floor
    [InlineData(30, double.PositiveInfinity, 20, false)] // no other detection
    public void OnlyANonUniquePickOffTheDetection_IsAmbiguous(double residual, double competitor, double radius, bool ambiguous)
    {
        Assert.Equal(ambiguous, MatchGate.Ambiguous(residual, competitor, radius));
    }

    [Fact]
    public void ASmallHoldOnAVolume_IsRefound_TheVolumeIsNoRival()
    {
        // The volume (size 0.1) is the nearest detection, 5 px off the predicted centre; the hold itself (0.02, as old) 20 px.
        var nearest = (Idx: new[] { 0, 1 }, Dist: new[] { 5.0, 20.0 });
        List<double?> sizes = [0.1, 0.02];
        List<double?> radii = [300, 15];

        var pick = MatchGate.Pick(nearest, 0.02, sizes, radii, 90, requireUnique: true, new MatchStats());

        Assert.Equal((1, 20.0), pick);
    }

    [Fact]
    public void TwoHoldsOfItsSize_AboutAsNear_StayAmbiguous()
    {
        var nearest = (Idx: new[] { 0, 1 }, Dist: new[] { 40.0, 44.0 });
        List<double?> sizes = [0.02, 0.022];

        Assert.Null(MatchGate.Pick(nearest, 0.02, sizes, [30, 30], 90, requireUnique: true, new MatchStats()));
        Assert.NotNull(MatchGate.Pick(nearest, 0.02, sizes, [30, 30], 90, requireUnique: false, new MatchStats()));
    }
}
