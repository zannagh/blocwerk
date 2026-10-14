// <copyright file="RedetectFollowUpTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Redetect;
using Blocwerk.Core.Services;
using Blocwerk.Core.Tests.HoldColors;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests.Redetect;

/// <summary>What follows an applied re-detect, and the small things a re-detect never proposes as holds.</summary>
public class RedetectFollowUpTests
{
    private static readonly HoldSamenessOptions Options = new();

    [Fact]
    public async Task AHoldThatTakesANewOutline_GetsItsThreeDShapeMadeAgain_AndKeepsTheOldOneUntilThen()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var panel = await s.AddPanelAsync(0);
        var hold = await s.AddHoldAsync(panel, (0.5, 0.5), configure: h => (h.Radius, h.IsAutoDetected, h.FootprintMm) = (0.03, true, "{}"));
        s.Harness.HoldDetection.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(_ => Task.FromResult(new List<DetectedHold> { new(0.508, 0.5, 0.028, null, 0.9) }));
        var queue = Substitute.For<IHoldRefinementQueue>();
        var redetect = new PanelRedetectService(
            s.Harness.DbContextFactory, s.Harness.CurrentUser, s.Harness.HoldDetection, Options,
            NullLogger<PanelRedetectService>.Instance, refinement: queue);

        await redetect.ApplyAsync(await redetect.PlanAsync(s.WallId, panel));

        Assert.Equal("{}", (await s.GetAsync(hold)).FootprintMm);
        queue.Received(1).Enqueue(s.WallId, Arg.Is<IEnumerable<Guid>>(ids => ids.Single() == hold));
    }

    [Fact]
    public void ASliverBetweenTwoHolds_IsNotProposedAsANewHold()
    {
        var detections = new[] { Traced(0.2, 0.2, 0.03, 0.03), Traced(0.5, 0.5, 0.03, 0.03), Traced(0.8, 0.8, 0.03, 0.03), Traced(0.5, 0.2, 0.02, 0.003) };

        var decisions = RedetectPlanner.Plan(new RedetectInput([], detections, new HashSet<(Guid, Guid)>(), CanMerge: true), Options);

        Assert.Equal([0, 1, 2], decisions.NewDetections);
    }

    [Fact]
    public void AThingMeasuredSmallerThanAnyHold_InsideAHold_IsAPart_AndARailIsNot()
    {
        var hold = Traced(0.5, 0.5, 0.04, 0.04).Shape;
        var input = new RedetectInput([new RedetectExisting(hold, new HashSet<Guid>(), 0, false)], [], new HashSet<(Guid, Guid)>(), CanMerge: true);
        var boltHole = new Hold { X = 0.51, Y = 0.5, Radius = 0.02, WidthMm = 14, HeightMm = 12 };
        var screwOn = new Hold { X = 0.51, Y = 0.5, Radius = 0.02, WidthMm = 60, HeightMm = 40 };

        Assert.True(RedetectRules.IsPart(boltHole, input, medianRadius: 0.02));
        Assert.False(RedetectRules.IsPart(screwOn, input, medianRadius: 0.02));
        Assert.False(RedetectRules.IsSliver(Traced(0.5, 0.5, 0.2, 0.01).Shape, medianRadius: 0.03));
    }

    [Fact]
    public void TwoHoldsThatEachHaveATwinOnTheNeighbourPanel_AreNeverProposedAsOneHold()
    {
        var placed = new Hold { X = 0.5, Y = 0.5, Radius = 0.03, IsAutoDetected = false, Color = "red" };
        var added = Traced(0.505, 0.5, 0.027, 0.024).Shape;
        RedetectInput Input(bool linked) => new(
            [new RedetectExisting(placed, new HashSet<Guid>(), 0, linked), new RedetectExisting(added, new HashSet<Guid>(), 0, linked)],
            [Traced(0.505, 0.5, 0.027, 0.025)],
            new HashSet<(Guid, Guid)>(),
            CanMerge: true);

        Assert.Single(RedetectPlanner.Plan(Input(linked: false), Options).LikelyMerges);
        var decisions = RedetectPlanner.Plan(Input(linked: true), Options);
        Assert.Empty(decisions.LikelyMerges);
        Assert.Empty(decisions.Merges);
    }

    [Fact]
    public void ABoltRecessInsideAStoredHold_IsNotProposedAsANewHold_AndAHoldScrewedOntoItIs()
    {
        // A 200 mm hold; a 20 mm recess in it and a 70 mm hold on it.
        var hold = Traced(0.5, 0.5, 0.05, 0.05).Shape;
        (hold.WidthMm, hold.HeightMm) = (200, 200);
        var far = new[] { Traced(0.1, 0.1, 0.03, 0.03), Traced(0.9, 0.1, 0.03, 0.03), Traced(0.1, 0.9, 0.03, 0.03) };
        RedetectDecisions Plan(double half) => RedetectPlanner.Plan(
            new RedetectInput(
                [new RedetectExisting(hold, new HashSet<Guid>(), 0, false)],
                [.. far, Traced(0.52, 0.52, half, half)],
                new HashSet<(Guid, Guid)>(),
                CanMerge: true),
            Options);

        Assert.DoesNotContain(3, Plan(half: 0.005).NewDetections);
        Assert.Contains(3, Plan(half: 0.0175).NewDetections);
    }

    [Fact]
    public void ADetectionOnAPrintedMarker_OrMeasuredSmallerThanAnyHold_OnBareWall_IsNotProposed_AndOneThatCoversSeveralHoldsIsNot()
    {
        var far = new[] { Traced(0.1, 0.1, 0.03, 0.03), Traced(0.9, 0.1, 0.03, 0.03), Traced(0.1, 0.9, 0.03, 0.03) };
        var onMarker = Traced(0.5, 0.5, 0.02, 0.02);
        var screwHole = Traced(0.7, 0.7, 0.02, 0.02);
        (screwHole.Shape.WidthMm, screwHole.Shape.HeightMm) = (30, 28);
        var foothold = Traced(0.3, 0.7, 0.02, 0.02);
        (foothold.Shape.WidthMm, foothold.Shape.HeightMm) = (60, 45);
        var board = Traced(0.5, 0.25, 0.1, 0.05);
        Hold Pocket(double x) => new() { X = x, Y = 0.25, Radius = 0.01, IsAutoDetected = true };
        var input = new RedetectInput(
            [.. new[] { Pocket(0.45), Pocket(0.5), Pocket(0.55) }.Select(h => new RedetectExisting(h, new HashSet<Guid>(), 0, false))],
            [.. far, onMarker, screwHole, foothold, board],
            new HashSet<(Guid, Guid)>(),
            CanMerge: true)
        {
            Markers = [[(0.48, 0.48), (0.52, 0.48), (0.52, 0.52), (0.48, 0.52)]],
        };

        var added = RedetectPlanner.Plan(input, Options).NewDetections;

        Assert.Contains(5, added);
        Assert.DoesNotContain(3, added);
        Assert.DoesNotContain(4, added);
        Assert.DoesNotContain(6, added);
    }

    [Fact]
    public void ALargeColouredHoldWithHoldsOnIt_IsProposedAsANewHold_NamingTheHoldsOnIt_AndABoardIsNot()
    {
        // A red base with two stored holds screwed on (one of them only a circle), and a white board with two pockets.
        var far = new[] { Traced(0.1, 0.1, 0.03, 0.03), Traced(0.9, 0.1, 0.03, 0.03), Traced(0.1, 0.9, 0.03, 0.03) };
        var onRed = new[] { new Hold { X = 0.45, Y = 0.5, Radius = 0.012, IsAutoDetected = true }, Traced(0.55, 0.5, 0.012, 0.012).Shape };
        var pockets = new[] { Traced(0.45, 0.8, 0.01, 0.01).Shape, Traced(0.55, 0.8, 0.01, 0.01).Shape };
        var red = Traced(0.5, 0.5, 0.1, 0.05) with { Color = "red" };
        red.Shape.Confidence = 0.9;
        var board = Traced(0.5, 0.8, 0.1, 0.04) with { Color = "white" };
        var detections = new[] { far[0], far[1], far[2], red, Traced(0.45, 0.5, 0.011, 0.011), Traced(0.55, 0.5, 0.012, 0.012), board };
        var input = new RedetectInput(
            [.. onRed.Concat(pockets).Select(h => new RedetectExisting(h, new HashSet<Guid>(), 0, false))], detections, new HashSet<(Guid, Guid)>(), CanMerge: true);

        var decisions = RedetectPlanner.Plan(input, Options);
        var proposal = RedetectProposals.Build(input, decisions, []).Single(p => p.Key == "new:3");

        Assert.Contains(3, decisions.NewDetections);
        Assert.DoesNotContain(6, decisions.NewDetections);
        Assert.Equal(onRed.Select(h => h.Id).Order(), proposal.OtherHoldIds.Order());
    }

    [Fact]
    public void ASmallColouredHoldOnABase_IsAHold_ADarkSpotOnItIsNot_AndTheBaseFoundTwiceIsProposedOnce()
    {
        var far = new[] { Traced(0.1, 0.1, 0.03, 0.03), Traced(0.9, 0.1, 0.03, 0.03), Traced(0.1, 0.9, 0.03, 0.03) };
        var red = Traced(0.5, 0.5, 0.1, 0.05) with { Color = "red" };
        var twice = Traced(0.501, 0.5, 0.1, 0.05) with { Color = "red" };
        twice.Shape.Confidence = 0.5;
        var small = Traced(0.45, 0.5, 0.006, 0.006) with { Color = "red" };
        (small.Shape.WidthMm, small.Shape.HeightMm) = (30, 28);
        var bolt = Traced(0.55, 0.5, 0.006, 0.006) with { Color = "black" };
        (bolt.Shape.WidthMm, bolt.Shape.HeightMm) = (30, 28);
        red.Shape.Confidence = 0.9;
        var input = new RedetectInput([], [.. far, red, twice, small, bolt], new HashSet<(Guid, Guid)>(), CanMerge: true);

        var added = RedetectPlanner.Plan(input, Options).NewDetections;

        Assert.Contains(3, added);
        Assert.DoesNotContain(4, added);
        Assert.Contains(5, added);
        Assert.DoesNotContain(6, added);
    }

    private static RedetectDetection Traced(double x, double y, double halfWidth, double halfHeight) => new(
        new Hold
        {
            X = x,
            Y = y,
            Radius = Math.Max(halfWidth, halfHeight),
            IsAutoDetected = true,
            OutlineSource = HoldOutlineSource.AutoContour,
            OutlineConfidence = 0.9,
            ShapePoints =
            [
                new() { Dx = -halfWidth, Dy = -halfHeight }, new() { Dx = halfWidth, Dy = -halfHeight },
                new() { Dx = halfWidth, Dy = halfHeight }, new() { Dx = -halfWidth, Dy = halfHeight },
            ],
        },
        null);
}
