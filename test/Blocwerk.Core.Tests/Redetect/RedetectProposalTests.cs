// <copyright file="RedetectProposalTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldStacks;
using Blocwerk.Core.Redetect;

namespace Blocwerk.Core.Tests.Redetect;

/// <summary>
/// What a re-detect proposes instead of writing: splits, offers for hand-made shapes, what a person already turned
/// down, and that only accepted proposals become decisions. Synthetic squares on one photo.
/// </summary>
public class RedetectProposalTests
{
    private static readonly HoldSamenessOptions Options = new();

    [Fact]
    public void AStoredHoldThatIsTwoDetections_IsOneSplitProposal_AndNothingElse()
    {
        // One stored outline over two holds side by side: the bleed the owner fixed by hand.
        var bled = Outlined(0.5, 0.5, 0.04, 0.02);
        var input = Input([bled], [Piece(0.48, 0.5), Piece(0.52, 0.5)]);

        var decisions = RedetectPlanner.Plan(input, Options);

        var split = Assert.Single(decisions.Splits);
        Assert.Equal(bled.Id, split.HoldId);
        Assert.Equal([0, 1], split.Detections.Order());
        Assert.Empty(decisions.Shapes);
        Assert.Empty(decisions.NewDetections);
        var proposal = Assert.Single(RedetectProposals.Build(input, decisions, []));
        Assert.Equal(RedetectProposalKind.Split, proposal.Kind);
        Assert.False(proposal.Safe);
    }

    [Fact]
    public void AnAcceptedSplit_GivesTheHoldOnePiece_AndAddsTheOther()
    {
        var bled = Outlined(0.5, 0.5, 0.04, 0.02);
        var input = Input([bled], [Piece(0.48, 0.5), Piece(0.52, 0.5)]);
        var decisions = RedetectPlanner.Plan(input, Options);

        var accepted = RedetectProposals.Accepted(decisions, new HashSet<string> { $"split:{bled.Id}" });

        var shape = Assert.Single(accepted.Shapes);
        Assert.Equal(bled.Id, shape.HoldId);
        Assert.Equal(RedetectShapeChange.Replace, shape.Change);
        Assert.Single(accepted.NewDetections);
        Assert.NotEqual(shape.Detection, accepted.NewDetections[0]);
    }

    [Fact]
    public void ASmallHoldOnALargeOne_IsNotASplit()
    {
        var volume = Outlined(0.5, 0.5, 0.1, 0.1);
        var input = Input([volume], [Square(0.5, 0.5, 0.098, 0.098), Square(0.47, 0.5, 0.012, 0.012), Square(0.54, 0.5, 0.012, 0.012)]);

        Assert.Empty(RedetectPlanner.Plan(input, Options).Splits);
    }

    [Fact]
    public void NothingAccepted_WritesNothing()
    {
        var hold = Outlined(0.3, 0.3, 0.03, 0.03);
        var input = Input([hold], [Square(0.303, 0.3, 0.026, 0.03), Square(0.7, 0.7, 0.02, 0.02)]);
        var decisions = RedetectPlanner.Plan(input, Options);
        Assert.True(decisions.Any);

        Assert.False(RedetectProposals.Accepted(decisions, new HashSet<string>()).Any);
    }

    [Fact]
    public void AShapeDrawnByHand_IsOfferedTheDetectedShape_AndKeptUnlessAccepted()
    {
        var drawn = Outlined(0.3, 0.3, 0.03, 0.03);
        drawn.OutlineSource = HoldOutlineSource.Manual;
        var input = Input([drawn], [Square(0.305, 0.3, 0.022, 0.03)]);

        var decisions = RedetectPlanner.Plan(input, Options);

        Assert.Empty(decisions.Shapes);
        Assert.Equal(1, decisions.HandShapesKept);
        var proposal = Assert.Single(RedetectProposals.Build(input, decisions, []));
        Assert.Equal((RedetectProposalKind.HandShape, false), (proposal.Kind, proposal.Safe));
        Assert.Single(RedetectProposals.Accepted(decisions, new HashSet<string> { proposal.Key }).Shapes);
    }

    [Fact]
    public void AHoldMovedByHand_CountsAsHandMade_ThoughNothingOnItSaysSo()
    {
        var dragged = Outlined(0.3, 0.3, 0.03, 0.03);
        var input = Input([dragged], [Square(0.305, 0.3, 0.022, 0.03)]) with { HandEdited = new HashSet<Guid> { dragged.Id } };

        var decisions = RedetectPlanner.Plan(input, Options);

        Assert.Empty(decisions.Shapes);
        Assert.Empty(decisions.Removed);
        Assert.Single(decisions.HandOffers);
    }

    [Fact]
    public void ADetectionWhereAPersonSaidNoHold_IsStillProposed_AndItsCardSaysWhatWasAnswered()
    {
        var when = new DateTimeOffset(2026, 9, 30, 18, 0, 0, TimeSpan.Zero);
        var rejected = new RedetectTurnedDown(new Hold { X = 0.7, Y = 0.7, Radius = 0.02 }, new RedetectEarlier(RedetectEarlierKind.NotAHold, when));
        var input = Input([], [Square(0.701, 0.7, 0.018, 0.018), Square(0.2, 0.2, 0.02, 0.02)]) with { TurnedDown = [rejected] };

        var decisions = RedetectPlanner.Plan(input, Options);
        var proposals = RedetectProposals.Build(input, decisions, []);

        Assert.Equal([0, 1], decisions.NewDetections);
        Assert.Equal(1, decisions.TurnedDown);
        Assert.Equal(new RedetectEarlier(RedetectEarlierKind.NotAHold, when), proposals.Single(p => p.Key == "new:0").Earlier);
        Assert.Null(proposals.Single(p => p.Key == "new:1").Earlier);
    }

    [Fact]
    public void ADetectionOnAnEmptySpot_ThatIsOnlyACircleOrARoughShape_IsNotProposedAsANewHold()
    {
        // What the detector gives on a crash mat or bare wood: a box nothing can be traced in.
        var circle = new RedetectDetection(new Hold { X = 0.2, Y = 0.8, Radius = 0.03, IsAutoDetected = true }, null);
        var rough = Square(0.6, 0.8, 0.03, 0.02);
        rough.Shape.OutlineConfidence = 0.1;
        var traced = Square(0.4, 0.3, 0.03, 0.02);
        traced.Shape.OutlineConfidence = 0.9;

        var decisions = RedetectPlanner.Plan(Input([], [circle, rough, traced]), Options);

        Assert.Equal([2], decisions.NewDetections);
        Assert.Equal(2, decisions.Untraced);
    }

    [Fact]
    public void ABoltHoleInsideAHold_IsNotProposedAsANewHold()
    {
        var hold = Outlined(0.5, 0.5, 0.04, 0.04);
        var detections = new[]
        {
            Traced(0.5, 0.5, 0.04), Traced(0.2, 0.2, 0.03), Traced(0.8, 0.2, 0.03), Traced(0.2, 0.8, 0.03),
            Traced(0.52, 0.51, 0.004), // the bolt hole
            Traced(0.8, 0.8, 0.004), // as tiny, but on bare wall: a foot chip
        };

        var decisions = RedetectPlanner.Plan(Input([hold], detections), Options);

        Assert.DoesNotContain(4, decisions.NewDetections);
        Assert.Contains(5, decisions.NewDetections);
    }

    [Fact]
    public void OnlyAShapeThatKeepsTheHoldInPlace_IsSafeToAcceptInBulk()
    {
        var near = Outlined(0.3, 0.3, 0.03, 0.03);
        var far = Outlined(0.6, 0.6, 0.03, 0.03);
        var input = Input([near, far], [Square(0.304, 0.3, 0.03, 0.03), Square(0.612, 0.6, 0.016, 0.02)]);

        var proposals = RedetectProposals.Build(input, RedetectPlanner.Plan(input, Options), []);

        Assert.True(proposals.Single(p => p.HoldId == near.Id).Safe);
        Assert.False(proposals.Single(p => p.HoldId == far.Id).Safe);
    }

    [Fact]
    public void ACirclePlacedByHand_ThatIsSmallerThanTheHold_GetsTheDetectedOutline_AsASafeProposal()
    {
        // The owner's case: a placeholder circle on a hold the detector traces well, and larger than the circle.
        var placed = new Hold { X = 0.5, Y = 0.5, Radius = 0.012, IsAutoDetected = false, Color = "red", Name = "Jug" };
        var detection = Square(0.505, 0.5, 0.03, 0.025);
        detection.Shape.Confidence = 0.95;
        var input = Input([placed], [detection]);

        var decisions = RedetectPlanner.Plan(input, Options);

        Assert.Empty(decisions.NewDetections);
        var proposal = Assert.Single(RedetectProposals.Build(input, decisions, []));
        Assert.Equal((RedetectProposalKind.HandShape, placed.Id), (proposal.Kind, proposal.HoldId!.Value));
        var update = Assert.Single(RedetectProposals.Accepted(decisions, new HashSet<string> { proposal.Key }).Shapes);
        RedetectShapes.Apply(placed, detection.Shape, update.Change);
        Assert.Equal((0.5, 0.5, "red", "Jug"), (placed.X, placed.Y, placed.Color, placed.Name));
        Assert.NotNull(placed.ShapePoints);
    }

    [Fact]
    public void AnOutlineForACircle_IsSafeOnlyWhenTheDetectorIsSure_AndTheSizesAgree()
    {
        var placed = new Hold { X = 0.5, Y = 0.5, Radius = 0.03, IsAutoDetected = false };
        var sure = Square(0.5, 0.5, 0.028, 0.028);
        sure.Shape.Confidence = 0.9;
        var unsure = Square(0.5, 0.5, 0.028, 0.028);
        unsure.Shape.Confidence = 0.4;

        Assert.True(Single(Input([placed], [sure])).Safe);
        Assert.False(Single(Input([placed], [unsure])).Safe);
    }

    [Fact]
    public void TwoStoredHoldsOnOneDetection_ThatAreOnlyLikelyTheSame_AreAMergeProposal_NeverASilentMerge()
    {
        // A circle placed by hand and the detection added later for the same hold: likely one hold, not surely.
        var placed = new Hold { X = 0.5, Y = 0.5, Radius = 0.03, IsAutoDetected = false, Color = "red" };
        var added = Outlined(0.505, 0.5, 0.027, 0.024);
        var input = Input([placed, added], [Square(0.505, 0.5, 0.027, 0.025)]);

        var decisions = RedetectPlanner.Plan(input, Options);

        Assert.Empty(decisions.Merges);
        var proposal = Assert.Single(RedetectProposals.Build(input, decisions, []), p => p.Kind == RedetectProposalKind.Merge);
        Assert.False(proposal.Safe);
        Assert.Empty(RedetectProposals.Accepted(decisions, new HashSet<string>()).Merges);
        Assert.Single(RedetectProposals.Accepted(decisions, new HashSet<string> { proposal.Key }).Merges);
    }

    [Fact]
    public void AShapeOnlyOneMethodTraced_IsNeverInTheBulkClass()
    {
        var hold = Outlined(0.3, 0.3, 0.03, 0.03);
        var detection = Square(0.303, 0.3, 0.024, 0.03);
        detection.Shape.OutlineCorroborated = false;

        Assert.False(Single(Input([hold], [detection])).Safe);
    }

    [Fact]
    public void AStack_IsAProposal_AndItsKeyNamesBothHolds()
    {
        var volume = Outlined(0.5, 0.5, 0.1, 0.1);
        var onIt = Outlined(0.5, 0.5, 0.015, 0.015);
        var input = Input([volume, onIt], []);
        var stacks = HoldStacking.Plan([volume, onIt]);

        var proposal = Assert.Single(RedetectProposals.Build(input, RedetectPlanner.Plan(input, Options), stacks));

        Assert.Equal((RedetectProposalKind.Stack, onIt.Id), (proposal.Kind, proposal.HoldId!.Value));
        Assert.Equal([(onIt.Id, volume.Id)], RedetectProposals.AcceptedStacks(new HashSet<string> { proposal.Key }));
    }

    private static RedetectProposal Single(RedetectInput input) =>
        Assert.Single(RedetectProposals.Build(input, RedetectPlanner.Plan(input, Options), []));

    private static RedetectInput Input(Hold[] existing, RedetectDetection[] detections) => new(
        [.. existing.Select(h => new RedetectExisting(h, new HashSet<Guid>(), 0, false))], detections, new HashSet<(Guid, Guid)>(), CanMerge: true);

    private static RedetectDetection Traced(double x, double y, double half)
    {
        var detection = Square(x, y, half, half);
        detection.Shape.OutlineConfidence = 0.9;
        return detection;
    }

    private static RedetectDetection Piece(double x, double y) => Square(x, y, 0.019, 0.02);

    private static RedetectDetection Square(double x, double y, double halfWidth, double halfHeight) => new(Outlined(x, y, halfWidth, halfHeight), null);

    private static Hold Outlined(double x, double y, double halfWidth, double halfHeight) => new()
    {
        X = x,
        Y = y,
        Radius = Math.Max(halfWidth, halfHeight),
        IsAutoDetected = true,
        OutlineCorroborated = true,
        OutlineDistinct = true,
        OutlineSource = HoldOutlineSource.AutoContour,
        ShapePoints =
        [
            new() { Dx = -halfWidth, Dy = -halfHeight }, new() { Dx = halfWidth, Dy = -halfHeight },
            new() { Dx = halfWidth, Dy = halfHeight }, new() { Dx = -halfWidth, Dy = halfHeight },
        ],
    };
}
