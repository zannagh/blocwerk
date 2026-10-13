// <copyright file="RedetectPlannerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Holds.Colors;
using Blocwerk.Core.Redetect;

namespace Blocwerk.Core.Tests.Redetect;

/// <summary>
/// What a re-detect decides, on synthetic geometry: which stored hold a detection is, which stored holds are one hold
/// twice, what is new and what goes. Circles of radius 0.03 that are 0.012 apart overlap by about 0.6 (IoU).
/// </summary>
public class RedetectPlannerTests
{
    private static readonly HoldSamenessOptions Options = new();

    [Fact]
    public void ADetectionOnAStoredHold_GivesItItsShape_AndIsNotNew()
    {
        var hold = Auto(0.5, 0.5, 0.03, "red");

        var plan = Plan([Existing(hold)], [Detection(0.508, 0.5, 0.028, "red")]);

        Assert.Equal(new RedetectShapeUpdate(hold.Id, 0, RedetectShapeChange.Replace), Assert.Single(plan.Shapes));
        Assert.Empty(plan.NewDetections);
        Assert.Empty(plan.Removed);
    }

    [Fact]
    public void TheSameShapeAgain_ChangesNothing()
    {
        var plan = Plan([Existing(Auto(0.5, 0.5, 0.03, "red"))], [Detection(0.5, 0.5, 0.03, "red")]);

        Assert.False(plan.Any);
    }

    [Fact]
    public void AShapeDrawnOrSmoothedByHand_Stays()
    {
        var drawn = Auto(0.3, 0.3, 0.03);
        drawn.OutlineSource = HoldOutlineSource.Manual;
        drawn.ShapePoints = Square(0.03);
        var smooth = Auto(0.6, 0.6, 0.03);
        smooth.ShapePoints = Square(0.03);
        smooth.ShapeControlPoints = Square(0.03);

        var plan = Plan([Existing(drawn), Existing(smooth)], [Detection(0.305, 0.3, 0.03), Detection(0.605, 0.6, 0.03)]);

        Assert.False(plan.Any);
        Assert.Equal(2, plan.HandShapesKept);
    }

    [Fact]
    public void AHandPlacedCircle_OnlyGetsTheOutlineItNeverHad()
    {
        var placed = Auto(0.3, 0.3, 0.03);
        placed.IsAutoDetected = false;
        var detection = Detection(0.305, 0.3, 0.03);
        detection.Shape.ShapePoints = Square(0.025);

        var plan = Plan([Existing(placed)], [detection]);

        var update = Assert.Single(plan.Shapes);
        Assert.Equal(RedetectShapeChange.FillOutline, update.Change);
        RedetectShapes.Apply(placed, detection.Shape, update.Change);
        Assert.Equal((0.3, 0.3, 0.03), (placed.X, placed.Y, placed.Radius));
        Assert.Equal(0.305 - 0.025, placed.X + placed.ShapePoints!.Min(p => p.Dx), 6);
        Assert.False(Plan([Existing(placed)], [detection]).Any);
    }

    [Fact]
    public void AnOutline_IsNeverTradedForACircle()
    {
        var outlined = Auto(0.3, 0.3, 0.03);
        outlined.ShapePoints = Square(0.025);

        Assert.False(Plan([Existing(outlined)], [Detection(0.306, 0.3, 0.03)]).Any);
    }

    [Fact]
    public void ASmallHoldOnAVolume_IsItsOwnHold()
    {
        var volume = Auto(0.5, 0.5, 0.1, "white");
        var small = Auto(0.52, 0.5, 0.02, "white");

        var onlyVolume = Plan([Existing(volume)], [Detection(0.5, 0.5, 0.1, "white"), Detection(0.52, 0.5, 0.02, "white")]);
        var both = Plan([Existing(volume), Existing(small)], [Detection(0.5, 0.5, 0.1, "white")]);

        Assert.Equal([1], onlyVolume.NewDetections);
        Assert.Empty(onlyVolume.Shapes);
        Assert.Empty(both.Merges);
        Assert.Empty(both.ForReview);
        Assert.Equal(HoldSamenessVerdict.Stacked, HoldSameness.Judge(HoldSameness.Measure(volume, small), HoldColorRelation.Same, Options));
    }

    [Fact]
    public void TwoStoredHoldsOnOneDetection_AreMerged_AndTheSurvivorTakesTheShape()
    {
        var used = Auto(0.3, 0.3, 0.03, "blue");
        var spare = Auto(0.302, 0.3, 0.03, "blue");

        var plan = Plan([Existing(spare), Existing(used, boulders: 1)], [Detection(0.31, 0.3, 0.03, "blue")]);

        var merge = Assert.Single(plan.Merges);
        Assert.Equal((used.Id, spare.Id), (merge.KeptId, merge.RemovedId));
        Assert.Equal(used.Id, Assert.Single(plan.Shapes).HoldId);
        Assert.Empty(plan.NewDetections);
        Assert.Empty(plan.Removed);
    }

    [Fact]
    public void TwoStoredHoldsOfDifferentColours_AreNotMerged()
    {
        var blue = Auto(0.3, 0.3, 0.03, "blue");
        var yellow = Auto(0.302, 0.3, 0.03, "yellow");

        var unknown = Plan([Existing(blue), Existing(yellow)], [Detection(0.301, 0.3, 0.03)]);
        var seenBlue = Plan([Existing(blue), Existing(yellow)], [Detection(0.301, 0.3, 0.03, "blue")]);

        Assert.Empty(unknown.Merges);
        Assert.Empty(seenBlue.Merges);
        Assert.Empty(seenBlue.NewDetections);
        Assert.Empty(seenBlue.Removed);
    }

    [Fact]
    public void APairThatIsOnlyLikely_IsLeftForTheReview()
    {
        var a = Auto(0.3, 0.3, 0.03, "green");
        var b = Auto(0.312, 0.3, 0.03, "green");

        var plan = Plan([Existing(a), Existing(b)], [Detection(0.306, 0.3, 0.03, "green")]);

        Assert.Empty(plan.Merges);
        Assert.Equal(HoldMergeRules.PairKey(a.Id, b.Id), Assert.Single(plan.ForReview));
    }

    [Fact]
    public void ASurePair_IsNotMerged_WhenABoulderUsesBoth_APersonRuledItOut_OrMergingIsNotAllowed()
    {
        var a = Auto(0.3, 0.3, 0.03, "green");
        var b = Auto(0.302, 0.3, 0.03, "green");
        var boulder = Guid.NewGuid();
        RedetectDetection[] detections = [Detection(0.301, 0.3, 0.03, "green")];
        RedetectExisting[] shared = [Existing(a, uses: [boulder]), Existing(b, uses: [boulder])];
        var key = HoldMergeRules.PairKey(a.Id, b.Id);

        var sharedBoulder = RedetectPlanner.Plan(new RedetectInput(shared, detections, new HashSet<(Guid, Guid)>(), true), Options);
        var dismissed = RedetectPlanner.Plan(new RedetectInput([Existing(a), Existing(b)], detections, new HashSet<(Guid, Guid)> { key }, true), Options);
        var notAllowed = RedetectPlanner.Plan(new RedetectInput([Existing(a), Existing(b)], detections, new HashSet<(Guid, Guid)>(), false), Options);

        Assert.Empty(sharedBoulder.Merges);
        Assert.Equal(key, Assert.Single(sharedBoulder.ForReview));
        Assert.Empty(dismissed.Merges);
        Assert.Empty(dismissed.ForReview);
        Assert.Empty(notAllowed.Merges);
        Assert.Equal(key, Assert.Single(notAllowed.ForReview));
    }

    [Fact]
    public void VirtualAndHiddenHolds_AreNotTouched_AndADetectionOnTheirSpotIsLeftOut()
    {
        var dotted = Auto(0.3, 0.3, 0.03);
        dotted.IsVirtual = true;
        var hidden = Auto(0.6, 0.6, 0.03);
        hidden.KeptUnconfirmedGeneration = 1;
        var lost = Auto(0.8, 0.8, 0.03);
        lost.KeptUnconfirmedGeneration = 1;

        var plan = Plan([Existing(dotted), Existing(hidden), Existing(lost)], [Detection(0.305, 0.3, 0.03), Detection(0.6, 0.605, 0.03)]);

        Assert.False(plan.Any);
        Assert.Equal(2, plan.SkippedDetections);
    }

    [Fact]
    public void OnlyAnAutomaticHoldNothingDependsOn_GoesWhenItIsNotFoundAgain()
    {
        var plain = Auto(0.1, 0.1, 0.03);
        var used = Auto(0.2, 0.2, 0.03);
        var named = Auto(0.3, 0.3, 0.03);
        named.Name = "Jug";
        var coloured = Auto(0.4, 0.4, 0.03, "red");
        var linked = Auto(0.5, 0.5, 0.03);
        var placed = Auto(0.6, 0.6, 0.03);
        placed.IsAutoDetected = false;
        RedetectExisting[] existing =
        [
            Existing(plain), Existing(used, boulders: 1), Existing(named), Existing(coloured), Existing(linked, linked: true), Existing(placed),
        ];

        var plan = Plan(existing, [Detection(0.9, 0.9, 0.03)]);
        var nothingDetected = Plan(existing, []);

        Assert.Equal([plain.Id], plan.Removed);
        Assert.Equal([0], plan.NewDetections);
        Assert.False(nothingDetected.Any);
    }

    private static RedetectDecisions Plan(RedetectExisting[] existing, RedetectDetection[] detections) =>
        RedetectPlanner.Plan(new RedetectInput(existing, detections, new HashSet<(Guid, Guid)>(), CanMerge: true), Options);

    private static RedetectExisting Existing(Hold hold, int boulders = 0, bool linked = false, Guid[]? uses = null)
    {
        var ids = (uses ?? [.. Enumerable.Range(0, boulders).Select(_ => Guid.NewGuid())]).ToHashSet();
        return new RedetectExisting(hold, ids, ids.Count, linked);
    }

    private static RedetectDetection Detection(double x, double y, double radius, string? color = null) =>
        new(Auto(x, y, radius), color);

    private static Hold Auto(double x, double y, double radius, string? color = null) => new()
    {
        X = x,
        Y = y,
        Radius = radius,
        Color = color,
        IsAutoDetected = true,
        OutlineSource = HoldOutlineSource.AutoCircle,
    };

    private static List<ShapePoint> Square(double half) =>
    [
        new() { Dx = -half, Dy = -half }, new() { Dx = half, Dy = -half }, new() { Dx = half, Dy = half }, new() { Dx = -half, Dy = half },
    ];
}
