using Blocwerk.Core.Abstractions;
using Blocwerk.HoldDetection.Matching;

namespace Blocwerk.HoldDetection.Tests.Matching;

/// <summary>
/// Wall-space anchors never override the image matcher: a contradiction is counted (and logged) and the
/// image proposal stays; only an anchor whose holds are both still free becomes a proposal, at confirm tier.
/// </summary>
public class AnchorReconcilerTests
{
    private static readonly List<MatcherHold> Left = [new(10, 0.1, 0.1), new(11, 0.5, 0.5), new(12, 0.8, 0.2)];
    private static readonly List<MatcherHold> Right = [new(20, 0.1, 0.1), new(21, 0.5, 0.5), new(22, 0.8, 0.2)];

    [Fact]
    public void AContradictingAnchor_LeavesTheImageMatchAlone()
    {
        var proposals = new List<Proposal> { new(0, 1, 0.8, false, 3, null) };
        var diags = new List<MatchDiag> { new(5, 0.9) };

        var outcome = Apply([new HoldOverlapAnchor(10, 20, 30, 0.9)], proposals, diags, [0], [1]);

        Assert.Equal(1, outcome.Conflicts);
        Assert.Equal(0, outcome.Added);
        var kept = Assert.Single(proposals);
        Assert.Equal((0, 1), (kept.LeftIdx, kept.RightIdx));
    }

    [Fact]
    public void AnAgreeingAnchor_IsCounted_AndAFreeOneIsAddedAtConfirmTier()
    {
        var proposals = new List<Proposal> { new(0, 0, 0.8, false, 3, null) };
        var diags = new List<MatchDiag> { new(5, 0.9) };

        var outcome = Apply(
            [new HoldOverlapAnchor(10, 20, 30, 0.9), new HoldOverlapAnchor(12, 22, 40, 0.8)], proposals, diags, [0], [0]);

        Assert.Equal((1, 0, 1), (outcome.Agreed, outcome.Conflicts, outcome.Added));
        var added = proposals[1];
        Assert.Equal((2, 2), (added.LeftIdx, added.RightIdx));
        Assert.Equal(AnchorReconciler.RescueTag, added.Rescue);
        Assert.InRange(added.Confidence, 0.30, 0.45);
        Assert.Equal(proposals.Count, diags.Count);
    }

    [Fact]
    public void AFreeAnchorTheWarpFieldPlacesFarOff_IsRejected()
    {
        var proposals = new List<Proposal>();
        var outcome = Apply([new HoldOverlapAnchor(10, 22, 30, 0.9)], proposals, [], [], []);

        Assert.Equal(1, outcome.Rejected);
        Assert.Empty(proposals);
    }

    private static AnchorReconciler.Outcome Apply(
        HoldOverlapAnchor[] anchors, List<Proposal> proposals, List<MatchDiag> diags, HashSet<int> usedL, HashSet<int> usedR)
    {
        // Identity field over a 1000 px square: left pixel == right pixel.
        Pt[] cL = Left.Select(h => new Pt(h.X * 1000, h.Y * 1000)).ToArray();
        Pt[] cR = Right.Select(h => new Pt(h.X * 1000, h.Y * 1000)).ToArray();
        var grid = new List<Pt>();
        for (int i = 0; i <= 10; i++)
        {
            for (int j = 0; j <= 10; j++)
            {
                grid.Add(new Pt(i * 100, j * 100));
            }
        }

        var field = new LocalWarpField(grid, grid);
        return AnchorReconciler.Apply(anchors, Left, Right, field, cL, cR, 180, proposals, diags, usedL, usedR, null);
    }
}
