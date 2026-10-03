// <copyright file="ShapeDecisionApplierTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>The pure write of one reviewed shape onto its hold.</summary>
public class ShapeDecisionApplierTests
{
    [Fact]
    public void Accepted_RebasesOntoTheHoldsCurrentCentre_AndDropsStaleMetricSize()
    {
        var hold = new Hold { X = 0.52, Y = 0.5, WidthMm = 80, OutlineSource = HoldOutlineSource.AutoCircle };
        var proposal = new WallUpdateShapeProposal
        {
            AnchorX = 0.5,
            AnchorY = 0.5,
            Confidence = 0.8,
            Decision = ShapeReviewDecision.Accepted,
            ShapeJson = ShapeJson.Write([new() { Dx = 0.01, Dy = 0 }, new() { Dx = 0, Dy = 0.01 }, new() { Dx = -0.01, Dy = 0 }]),
        };

        Assert.True(ShapeDecisionApplier.Apply(hold, proposal));

        Assert.Equal(-0.01, hold.ShapePoints![0].Dx, 9);
        Assert.Equal(-0.03, hold.ShapePoints[2].Dx, 9);
        Assert.Null(hold.WidthMm);
        Assert.Equal(0.8, hold.OutlineConfidence);
    }

    [Theory]
    [InlineData(ShapeReviewDecision.Pending)]
    [InlineData(ShapeReviewDecision.KeepPrevious)]
    public void PendingAndKeepPrevious_ChangeNothing(ShapeReviewDecision decision)
    {
        var shape = new List<ShapePoint> { new() { Dx = 0.01 }, new() { Dy = 0.01 }, new() { Dx = -0.01 } };
        var hold = new Hold { X = 0.5, Y = 0.5, ShapePoints = shape, OutlineSource = HoldOutlineSource.AutoContour };
        var proposal = new WallUpdateShapeProposal { Decision = decision, ShapeJson = ShapeJson.Write(shape) };

        Assert.False(ShapeDecisionApplier.Apply(hold, proposal));
        Assert.Same(shape, hold.ShapePoints);
    }

    [Fact]
    public void AcceptedCircleFallback_KeepsTheHoldAsItIs()
    {
        var hold = new Hold { X = 0.5, Y = 0.5 };

        Assert.False(ShapeDecisionApplier.Apply(hold, new WallUpdateShapeProposal { Decision = ShapeReviewDecision.Accepted }));
        Assert.Null(hold.OutlineSource);
    }
}
