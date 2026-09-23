using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.HoldDetection.Outlines;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Tests.Outlines;

/// <summary>
/// The outline-upgrade planner against the real outliner on real hold crops: an existing circle hold (centre +
/// radius only, as stored) on a coloured hold is upgraded; one on a wall-coloured volume stays a circle.
/// </summary>
public class HoldOutlineUpgradePlannerRealTests
{
    [Fact]
    public void ColouredHold_GetsAnOutline_AroundItsUnchangedCentre()
    {
        var (proposal, hold) = PlanOne(HoldFixtures.Green, autoDetected: true);

        Assert.Equal(HoldOutlineUpgradeOutcome.Outline, proposal.Outcome);
        Assert.True(proposal.Result.ShapePoints!.Count >= 3);
        Assert.Equal((hold.X, hold.Y), (proposal.Result.AnchorX, proposal.Result.AnchorY));
    }

    [Fact]
    public void WallColouredVolume_StaysACircle_ButStillGetsAFingerprint()
    {
        var (proposal, _) = PlanOne(HoldFixtures.BigVolume, autoDetected: true);

        Assert.Equal(HoldOutlineUpgradeOutcome.KeepCircle, proposal.Outcome);
        Assert.True(proposal.FillsFingerprint);
    }

    [Fact]
    public void ManualHold_IsOnlyPlanned_WhenIncluded()
    {
        using Mat img = HoldFixtures.Load(HoldFixtures.Green.File);
        var hold = CircleHold(HoldFixtures.Green, img, autoDetected: false);
        using var session = new OpenCvHoldOutlineService().OpenSession(img);

        Assert.Empty(HoldOutlineUpgradePlanner.Plan(session, [hold], includeManual: false));
        var included = Assert.Single(HoldOutlineUpgradePlanner.Plan(session, [hold], includeManual: true));
        Assert.NotEqual(HoldOutlineUpgradeOutcome.KeepCircle, included.Outcome);
    }

    private static (HoldOutlineUpgradeProposal Proposal, Hold Hold) PlanOne(HoldFixture fixture, bool autoDetected)
    {
        using Mat img = HoldFixtures.Load(fixture.File);
        var hold = CircleHold(fixture, img, autoDetected);
        using var session = new OpenCvHoldOutlineService().OpenSession(img);
        return (Assert.Single(HoldOutlineUpgradePlanner.Plan(session, [hold], includeManual: true)), hold);
    }

    /// <summary>The fixture as a stored circle hold: box centre, radius from the box's longer side.</summary>
    private static Hold CircleHold(HoldFixture f, Mat img, bool autoDetected) => new()
    {
        X = f.Centre.X / img.Width,
        Y = f.Centre.Y / img.Height,
        Radius = Math.Max(f.Width, f.Height) / (2.0 * Math.Max(img.Width, img.Height)),
        IsAutoDetected = autoDetected,
    };
}
