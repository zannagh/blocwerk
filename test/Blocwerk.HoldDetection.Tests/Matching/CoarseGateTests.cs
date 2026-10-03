using Blocwerk.HoldDetection.Matching;

namespace Blocwerk.HoldDetection.Tests.Matching;

/// <summary>
/// The coarse-homography gate below <see cref="HomographyHelper.MinInliers"/>: a borderline fit is only kept
/// when the wider texture evidence is far beyond anything a garbage pair produced (measured 2026-09-23:
/// ratio matches 20-44, texture anchors 1-77, inliers 4-12 on garbage; 186 / 951 / 16 on the real reframed
/// right panel). The numbers below are those measurements.
/// </summary>
public class CoarseGateTests
{
    [Theory]
    [InlineData(10, 44, 46)] // IMG_2770 -> IMG_2771, no seed
    [InlineData(12, 39, 77)] // IMG_2771 -> IMG_2775, no seed
    [InlineData(4, 20, 11)] // IMG_2771 -> IMG_2783, no seed
    [InlineData(4, 36, 2)] // reframed update, staged centre -> staged right
    public void MeasuredGarbagePairs_StayRejected(int inliers, int ratio, int anchors)
    {
        Assert.False(HomographyHelper.Rescued(inliers, ratio, () => anchors));
    }

    // The real right panel passes the primary gate by one inlier (16 >= 15); the rescue is its second, much
    // wider margin should a slightly different photo drop it below.
    [Fact]
    public void RealRightPanelEvidence_WouldSurviveADropBelowTheInlierGate()
    {
        Assert.True(HomographyHelper.Rescued(14, 186, () => 951));
    }

    [Fact]
    public void WeakCoarseEvidence_NeverPaysForTheDenseAnchorPass()
    {
        var asked = false;
        Assert.False(HomographyHelper.Rescued(12, 44, () =>
        {
            asked = true;
            return 10_000;
        }));
        Assert.False(asked);
    }

    [Fact]
    public void AMinimalFit_OrNoAnchorCounter_IsNeverRescued()
    {
        Assert.False(HomographyHelper.Rescued(HomographyHelper.MinRescueInliers - 1, 500, () => 5_000));
        Assert.False(HomographyHelper.Rescued(14, 500, null));
    }
}
