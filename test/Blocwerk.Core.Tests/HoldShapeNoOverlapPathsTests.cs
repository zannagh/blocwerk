using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>The overlap resolver on the wall-update proposals and on the circle-upgrade planner (radius shrink).</summary>
public sealed class HoldShapeNoOverlapPathsTests
{
    private static readonly Guid A = new("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = new("00000000-0000-0000-0000-00000000000b");

    [Fact]
    public void OverlappingProposalsAreClippedBeforeReview()
    {
        var holds = new List<Hold> { StagedHold(A, 0.30), StagedHold(B, 0.37) };
        var proposals = new List<WallUpdateShapeProposal> { Proposal(holds[0]), Proposal(holds[1]) };

        var changed = ShapeProposalOverlaps.Resolve(holds, proposals, 1.33);

        Assert.Equal(1, changed);
        Assert.Equal(ShapeJson.Write(HoldShapeSmootherTests.Blob(0.05, 0.05, 24)), proposals[0].ShapeJson);
        Assert.NotEqual(proposals[0].ShapeJson, proposals[1].ShapeJson);
        Assert.Equal(HoldOutlineMethod.Contour, proposals[1].Method);
    }

    [Fact]
    public void AProposalThatCannotBeClippedBecomesACircleFallback()
    {
        var wide = StagedHold(A, 0.5);
        var left = StagedHold(B, 0.4348);
        left.ShapePoints = null;
        left.Radius = 0.025;
        var proposal = Proposal(wide, [new() { Dx = -0.08, Dy = -0.01 }, new() { Dx = 0.08, Dy = -0.01 }, new() { Dx = 0.08, Dy = 0.01 }, new() { Dx = -0.08, Dy = 0.01 }]);
        var right = StagedHold(new Guid("00000000-0000-0000-0000-00000000000c"), 0.5652);
        right.ShapePoints = null;
        right.Radius = 0.025;

        ShapeProposalOverlaps.Resolve([wide, left, right], [proposal], 1);

        Assert.Equal(HoldOutlineMethod.CircleFallback, proposal.Method);
        Assert.Null(proposal.ShapeJson);
        Assert.True(proposal.Confidence <= HoldOutlineRefiner.CircleConfidenceCeiling);
    }

    [Fact]
    public void TheUpgradePlannerShrinksAnAutoCircleThatStillOverlaps()
    {
        var wide = new Hold { Id = A, X = 0.5, Y = 0.5, Radius = 0.04, IsAutoDetected = true };
        var manual = new Hold { Id = B, X = 0.565, Y = 0.5, Radius = 0.04, IsAutoDetected = false };
        var otherSide = new Hold { Id = new Guid("00000000-0000-0000-0000-00000000000c"), X = 0.435, Y = 0.5, Radius = 0.04, IsAutoDetected = false };
        var session = Substitute.For<IHoldOutlineSession>();
        session.ImageWidth.Returns(400);
        session.ImageHeight.Returns(300);
        session.Outline(Arg.Any<HoldSeed>()).Returns(Contour(
            wide,
            [new() { Dx = -0.08, Dy = -0.01 }, new() { Dx = 0.08, Dy = -0.01 }, new() { Dx = 0.08, Dy = 0.01 }, new() { Dx = -0.08, Dy = 0.01 }]));

        var proposal = Assert.Single(HoldOutlineUpgradePlanner.Plan(session, [wide, manual, otherSide], includeManual: false));

        Assert.Equal(HoldOutlineUpgradeOutcome.KeepCircle, proposal.Outcome);
        Assert.NotNull(proposal.NewRadius);
        Assert.InRange(proposal.NewRadius.Value, 0.02, 0.0399);
    }

    [Fact]
    public void TheUpgradePlannerNeverShrinksAManualHold()
    {
        var manual = new Hold { Id = A, X = 0.5, Y = 0.5, Radius = 0.04, IsAutoDetected = false };
        var other = new Hold { Id = B, X = 0.565, Y = 0.5, Radius = 0.04, IsAutoDetected = false };
        var session = Substitute.For<IHoldOutlineSession>();
        session.ImageWidth.Returns(400);
        session.ImageHeight.Returns(300);
        session.Outline(Arg.Any<HoldSeed>()).Returns(Contour(
            manual,
            [new() { Dx = -0.08, Dy = -0.01 }, new() { Dx = 0.08, Dy = -0.01 }, new() { Dx = 0.08, Dy = 0.01 }, new() { Dx = -0.08, Dy = 0.01 }]));

        var proposals = HoldOutlineUpgradePlanner.Plan(session, [manual, other], includeManual: true);

        Assert.All(proposals, p => Assert.Null(p.NewRadius));
    }

    private static Hold StagedHold(Guid id, double x) => new()
    {
        Id = id,
        X = x,
        Y = 0.5,
        Radius = 0.05,
        IsAutoDetected = true,
        ShapePoints = HoldShapeSmootherTests.Blob(0.05, 0.05, 24),
    };

    private static WallUpdateShapeProposal Proposal(Hold hold, List<ShapePoint>? shape = null) => new()
    {
        HoldId = hold.Id,
        Method = HoldOutlineMethod.Contour,
        Confidence = 0.8,
        AnchorX = hold.X,
        AnchorY = hold.Y,
        ImageWidth = 400,
        ImageHeight = 300,
        ShapeJson = ShapeJson.Write(shape ?? HoldShapeSmootherTests.Blob(0.05, 0.05, 24)),
    };

    private static HoldOutlineResult Contour(Hold hold, List<ShapePoint> shape) =>
        new(
            HoldOutlineGeometry.ToPolygon(shape, hold.X, hold.Y).ToList(),
            hold.X,
            hold.Y,
            shape,
            100,
            new HoldOutlineBounds(0, 0, 0, 0),
            0.8,
            HoldOutlineMethod.Contour,
            new HoldFingerprint());
}
