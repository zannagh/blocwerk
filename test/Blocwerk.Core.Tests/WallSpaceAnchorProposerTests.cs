using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Geometry;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The wall-space anchor rule: same facet, plane distance within a generous relief-aware radius, fingerprint
/// similarity as the deciding signal, and a clear winner on BOTH sides (mutual best + margin).
/// </summary>
public class WallSpaceAnchorProposerTests
{
    private static readonly HoldFingerprint Red = Fp(120, 185, 165, 0);
    private static readonly HoldFingerprint Blue = Fp(90, 140, 70, 7);
    private static readonly HoldFingerprint Yellow = Fp(200, 125, 200, 2);

    [Fact]
    public void TwinFortyMillimetresApart_WithTheSameLook_IsAnAnchor()
    {
        var anchors = WallSpaceAnchorProposer.Propose(
            [W(0, "0", 1000, 1000, Red), W(1, "0", 1400, 1000, Blue)],
            [W(5, "0", 1400, 1030, Blue), W(4, "0", 1030, 1025, Red)]);

        Assert.Equal(2, anchors.Count);
        Assert.Contains(anchors, a => a is { LeftHoldId: 0, RightHoldId: 4 });
        Assert.Contains(anchors, a => a is { LeftHoldId: 1, RightHoldId: 5 });
    }

    [Fact]
    public void DifferentFacet_OrTooFar_OrDifferentLook_IsNeverAnAnchor()
    {
        Assert.Empty(WallSpaceAnchorProposer.Propose([W(0, "0", 1000, 1000, Red)], [W(0, "1", 1000, 1000, Red)]));
        Assert.Empty(WallSpaceAnchorProposer.Propose([W(0, "0", 1000, 1000, Red)], [W(0, "0", 1200, 1000, Red)]));
        Assert.Empty(WallSpaceAnchorProposer.Propose([W(0, "0", 1000, 1000, Red)], [W(0, "0", 1010, 1000, Blue)]));
    }

    [Fact]
    public void TwoLookAlikesWithinReach_AreAmbiguous_AndYieldNothing()
    {
        var anchors = WallSpaceAnchorProposer.Propose(
            [W(0, "0", 1000, 1000, Red)],
            [W(0, "0", 1030, 1000, Red), W(1, "0", 970, 1010, Red)]);

        Assert.Empty(anchors);
    }

    [Fact]
    public void ALookAlikeNearby_DoesNotBlockTheAppearanceWinner()
    {
        // The yellow hold is closer but looks nothing like the red one; the red twin still wins.
        var anchors = WallSpaceAnchorProposer.Propose(
            [W(0, "0", 1000, 1000, Red)],
            [W(0, "0", 1005, 1000, Yellow), W(1, "0", 1045, 1000, Red)]);

        Assert.Equal(1, Assert.Single(anchors).RightHoldId);
    }

    [Fact]
    public void Tolerance_UsesTheSmallerMeasuredSize_SoAPlaceholderCannotInflateIt()
    {
        Assert.Equal(WallSpaceAnchorProposer.MinToleranceMm, WallSpaceAnchorProposer.ToleranceMm(null, 400));
        Assert.Equal(90, WallSpaceAnchorProposer.ToleranceMm(90, 400));
        Assert.Equal(WallSpaceAnchorProposer.MaxToleranceMm, WallSpaceAnchorProposer.ToleranceMm(500, 400));
    }

    [Fact]
    public void HoldsWithoutFingerprintOrPlane_AreIgnored()
    {
        Assert.Empty(WallSpaceAnchorProposer.Propose(
            [W(0, "0", 1000, 1000, null), new WallSpaceHold(1, "0", null, null, null, Red)],
            [W(0, "0", 1000, 1000, Red), W(1, "0", 1000, 1000, Red)]));
    }

    private static WallSpaceHold W(int i, string facet, double a, double b, HoldFingerprint? fp) =>
        new(i, facet, a, b, 50, fp);

    private static HoldFingerprint Fp(double l, double a, double b, int hueBin)
    {
        var hist = new double[HoldFingerprint.HueBins + 1];
        hist[hueBin] = 1;
        return new HoldFingerprint { L = l, A = a, B = b, Histogram = hist, Aspect = 1.3, Solidity = 0.9, Hu = [2.5, 6, 8, 9, 0, 0, 0] };
    }
}
