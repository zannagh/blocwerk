// <copyright file="RelocationDisplacementGuardTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A "Possibly moved" suggestion survives only when the jump from the aligned old position to the
/// look-alike is plausible for a shifted hold: within the local alignment noise or the physical limit.
/// A look-alike across the wall is dropped; a pair with no aligned position is kept.
/// </summary>
public class RelocationDisplacementGuardTests
{
    private const int LongerSide = 4032;

    private static readonly List<ResidualAnchor> Anchors =
        [.. Enumerable.Range(0, 20).Select(i => new ResidualAnchor(200 * i, 1500, 5))];

    [Fact]
    public void Filter_DropsAJumpAcrossTheWall_KeepsANearbyShift()
    {
        var near = Pair();
        var across = Pair();
        var expected = new Dictionary<Guid, (double X, double Y)>
        {
            [near.OldHoldId] = (1600, 2100),
            [across.OldHoldId] = (1650, 2150),
        };
        var actual = new Dictionary<Guid, (double X, double Y)>
        {
            [near.NewHoldId] = (1660, 2140),
            [across.NewHoldId] = (3350, 1480),
        };

        var kept = RelocationDisplacementGuard.Filter([near, across], expected, actual, Anchors, LongerSide);

        Assert.Equal([near], kept);
    }

    [Fact]
    public void Filter_KeepsAPairWithNoAlignedPosition()
    {
        var pair = Pair();

        var kept = RelocationDisplacementGuard.Filter(
            [pair], new Dictionary<Guid, (double X, double Y)>(), new Dictionary<Guid, (double X, double Y)> { [pair.NewHoldId] = (0, 0) },
            Anchors, LongerSide);

        Assert.Equal([pair], kept);
    }

    [Fact]
    public void AllowedShift_GrowsWithPoorLocalAlignment()
    {
        List<ResidualAnchor> poor = [.. Enumerable.Range(0, 10).Select(i => new ResidualAnchor(100 * i, 100, 120))];

        Assert.Equal(RelocationDisplacementGuard.MaxShiftFraction * LongerSide, RelocationDisplacementGuard.AllowedShiftPx((500, 100), Anchors, LongerSide));
        Assert.Equal(360, RelocationDisplacementGuard.AllowedShiftPx((500, 100), poor, LongerSide));
    }

    private static RelocationPair Pair() => new(Guid.NewGuid(), Guid.NewGuid(), 0.9, 0.1, false);
}
