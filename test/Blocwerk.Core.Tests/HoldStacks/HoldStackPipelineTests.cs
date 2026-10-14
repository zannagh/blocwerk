// <copyright file="HoldStackPipelineTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldStacks;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests.HoldStacks;

/// <summary>
/// A small hold on a volume and the volume are two holds: the pieces of a wall update and of its look-over that compare
/// holds on one spot never take them for the same hold, a duplicate, or one that replaced the other.
/// </summary>
public class HoldStackPipelineTests
{
    [Fact]
    public void TheDuplicateReview_LeavesAHoldOnAVolumeAlone_AndStillFindsTheSameHoldTwice()
    {
        var volume = Outlined(0.5, 0.5, 0.12, "red");
        var onIt = Auto(0.53, 0.5, 0.02, "red");
        var twice = Outlined(0.505, 0.5, 0.12, "red");

        Assert.Empty(HoldDuplicateFinder.Find([volume, onIt]));
        Assert.Single(HoldDuplicateFinder.Find([volume, onIt, twice]));
    }

    [Fact]
    public void TheDuplicateReview_KeepsADetectionInsideAPlacedCircle_UntilSomebodyStacksTheTwo()
    {
        var placed = Auto(0.5, 0.5, 0.06, null);
        (placed.IsAutoDetected, placed.OutlineSource) = (false, HoldOutlineSource.Manual);
        var detection = Auto(0.51, 0.5, 0.02, null);

        Assert.Equal(HoldDuplicateKind.InsideHandPlaced, Assert.Single(HoldDuplicateFinder.Find([placed, detection])).Kind);

        detection.StackedOnHoldId = placed.Id;
        Assert.Empty(HoldDuplicateFinder.Find([placed, detection]));
    }

    [Fact]
    public void AKeptHoldThatSatOnAVolume_HasNoNewHoldOnItsSpot_ButOneUnderANewLargerHoldStillDoes()
    {
        var volume = Auto(0.5, 0.5, 0.12, null);
        var kept = Auto(0.53, 0.5, 0.02, null);
        kept.KeptUnconfirmedGeneration = 3;

        // No relation: the old hold was not found and a new, larger hold lies over its spot.
        Assert.Same(volume, SameSpotFinder.Nearest(kept, [volume], null));
        Assert.True(SameSpotFinder.Covers(volume, kept, null));

        // It sat on that volume before the update (also through a hold in between): the volume did not take its place.
        kept.StackedOnHoldId = volume.Id;
        Assert.Null(SameSpotFinder.Nearest(kept, [volume], null));
        Assert.False(SameSpotFinder.Covers(volume, kept, null));
        var plate = Auto(0.53, 0.5, 0.05, null);
        (kept.StackedOnHoldId, plate.StackedOnHoldId) = (plate.Id, volume.Id);
        Assert.Null(SameSpotFinder.Nearest(kept, [volume, plate], null));
    }

    [Fact]
    public void ADiscardedDetection_IsADuplicateOfAHoldOfItsSize_NotOfTheVolumeUnderIt()
    {
        var detection = new HoldArea(0.53, 0.5, 0.02, null, null);
        var volume = new HoldArea(0.5, 0.5, 0.12, ShapePoint.DefaultOctagon(0.12), null);
        var sameHold = new HoldArea(0.532, 0.5, 0.022, null, null);
        var placedCircle = new HoldArea(0.5, 0.5, 0.12, null, null);

        Assert.Equal(0, DiscardedDetections.Overlap(detection, [volume]));
        Assert.True(DiscardedDetections.Overlap(detection, [volume, sameHold]) >= LookOverRules.DuplicateOverlap);

        // A plain circle around the detection is that hold: the detection doubles it.
        Assert.Equal(1, DiscardedDetections.Overlap(detection, [placedCircle]));
    }

    [Theory]
    [InlineData(20, 110, true)] // the small hold was paired with the volume it sits on
    [InlineData(110, 20, true)]
    [InlineData(40, 60, false)] // about the same size: a hold swapped for another one
    [InlineData(0, 110, false)] // a size that is not known says nothing
    public void APairOfClearlyDifferentSize_IsAMispairing_NotAnotherHoldInItsPlace(double oldRadiusPx, double twinRadiusPx, bool otherSize)
    {
        Assert.Equal(otherSize, HoldLookCheck.OtherSize(oldRadiusPx, twinRadiusPx));
    }

    [Fact]
    public void ANewSmallHoldOnACarriedVolume_StaysANewHold_AlthoughTheModelKnowsAHoldThere()
    {
        var (small, sameSize) = (Guid.NewGuid(), Guid.NewGuid());
        var evidence = new Dictionary<Guid, Evidence3DVerdict> { [small] = Evidence3DVerdict.KnownHold, [sameSize] = Evidence3DVerdict.KnownHold };
        var input = new NewHoldTriageInput(
            [new TriageCandidate(small, 520, 500, 20), new TriageCandidate(sameSize, 1500, 500, 100)],
            [],
            null,
            [],
            Evidence3D: evidence,
            Carried: [new TriageSpot(500, 500, 110), new TriageSpot(1500, 500, 110)]);

        var discarded = NewHoldTriage.Classify(input, null);

        // The volume was carried: the small detection on it is another hold, the one of the volume's size is the volume again.
        Assert.False(discarded.ContainsKey(small));
        Assert.Equal(NewHoldDiscardReason.KnownHoldIn3D, discarded[sameSize]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheOutlineCleanUp_LeavesAHoldOnAVolumeAndTheVolumeAsTheyAre_WhicheverComesFirst(bool volumeFirst)
    {
        var (first, second) = (new Guid("00000000-0000-0000-0000-00000000000a"), new Guid("00000000-0000-0000-0000-00000000000b"));
        var volume = new HoldShapeInput(volumeFirst ? first : second, 0.5, 0.5, 0.12, ShapePoint.DefaultOctagon(0.12), Locked: false);
        var onIt = new HoldShapeInput(volumeFirst ? second : first, 0.53, 0.5, 0.02, null, Locked: false);
        var beside = new HoldShapeInput(new Guid("00000000-0000-0000-0000-00000000000c"), 0.64, 0.5, 0.03, null, Locked: false);

        var resolved = HoldShapeOverlapResolver.Resolve([volume, onIt, beside]).ToDictionary(r => r.Id);

        // The pair overlaps on purpose and neither is cut or shrunk for the other; a neighbour that only grazes the volume still is.
        Assert.Equal((HoldShapeFit.Unchanged, HoldShapeFit.Unchanged), (resolved[volume.Id].Fit, resolved[onIt.Id].Fit));
        Assert.Equal(0.02, resolved[onIt.Id].Radius);
        Assert.NotEqual(HoldShapeFit.Unchanged, resolved[beside.Id].Fit);
    }

    private static Hold Outlined(double x, double y, double radius, string? color)
    {
        var hold = Auto(x, y, radius, color);
        (hold.ShapePoints, hold.OutlineSource) = (ShapePoint.DefaultOctagon(radius), HoldOutlineSource.AutoContour);
        return hold;
    }

    private static Hold Auto(double x, double y, double radius, string? color) => new()
    {
        X = x, Y = y, Radius = radius, IsAutoDetected = true, Color = color, OutlineSource = HoldOutlineSource.AutoCircle,
    };
}
