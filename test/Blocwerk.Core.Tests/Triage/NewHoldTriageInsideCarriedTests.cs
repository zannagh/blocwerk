// <copyright file="NewHoldTriageInsideCarriedTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests.Triage;

/// <summary>
/// A detection mostly inside a carried hold's footprint is a second outline or a part of that hold (a bolt hole, a screw, a
/// facet), not a new hold — unless it is a confident hold sitting on a clearly larger outlined carried hold (a hold on a
/// volume). And a marker the old photo showed is still a marker on the new photo, mapped over through the pairs.
/// </summary>
public class NewHoldTriageInsideCarriedTests
{
    // A plain identity alignment between the photos, spread over the frame.
    private static readonly List<PointPair> Identity =
        [.. Enumerable.Range(0, 4).SelectMany(i => Enumerable.Range(0, 3).Select(j => new PointPair(500 + (i * 1000), 500 + (j * 1000), 500 + (i * 1000), 500 + (j * 1000))))];

    [Fact]
    public void ASecondOutlineOfACarriedHold_IsDiscarded()
    {
        var carried = new TriageOutline(2000, 1500, 60);
        var second = new TriageCandidate(Guid.NewGuid(), 2010, 1505, 50, Confident: true);

        var result = NewHoldTriage.Classify(Input([second], [carried]), null);

        Assert.Equal(NewHoldDiscardReason.InsideCarriedHold, result[second.Id]);
    }

    [Fact]
    public void ABoltHoleInsideAHandPlacedCircle_IsDiscarded_EvenWhenTheDetectorIsSure()
    {
        // A plain circle marks roughly where a hold is: what lies in it is that hold again or a part of it.
        var circle = new TriageOutline(2000, 1500, 200);
        var bolt = new TriageCandidate(Guid.NewGuid(), 2050, 1450, 12, Confident: true);

        Assert.Equal(NewHoldDiscardReason.InsideCarriedHold, NewHoldTriage.Classify(Input([bolt], [circle]), null)[bolt.Id]);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AHoldOnAnOutlinedVolume_StaysWhenConfident(bool confident, bool discarded)
    {
        var volume = new TriageOutline(2000, 1500, 300, Square(2000, 1500, 300));
        var hold = new TriageCandidate(Guid.NewGuid(), 2100, 1400, 30, Confident: confident, Outline: Square(2100, 1400, 30));

        var result = NewHoldTriage.Classify(Input([hold], [volume]), null);

        Assert.Equal(discarded, result.ContainsKey(hold.Id));
    }

    [Theory]
    [InlineData(139, 111, 0.3, true)] // N57: purple where an orange hold (143 / 146) was, and the spot looks changed
    [InlineData(140, 140, 0.3, false)] // the orange hold again, lit differently
    [InlineData(139, 111, 0.9, false)] // another colour, but the old photo shows the same thing there
    public void ADetectionOfAnotherColour_InsideAnOldHold_ThatLooksChanged_IsASwap_AndStays(double a, double b, double presence, bool swapped)
    {
        var oldId = Guid.NewGuid();
        var orange = new TriageOutline(2000, 1500, 60, Lab: new TriageLab(76, 143, 146), OldHoldId: oldId);
        var detection = new TriageCandidate(Guid.NewGuid(), 2010, 1505, 50, Confident: true, Lab: new TriageLab(130, a, b));
        var swaps = new List<TriageSwap>();

        var result = NewHoldTriage.Classify(Input([detection], [orange]), q => q.Select(_ => (double?)presence).ToList(), swaps);

        Assert.Equal(swapped, !result.ContainsKey(detection.Id));
        Assert.Equal(swapped ? [new TriageSwap(detection.Id, oldId)] : [], swaps);
    }

    [Fact]
    public void ADarkBoltHoleInsideAnOrangeHold_IsNoSwap()
    {
        // Dark and of another colour, and the spot looks changed, but far smaller than the hold: a part of it.
        var orange = new TriageOutline(2000, 1500, 60, Lab: new TriageLab(150, 160, 175), OldHoldId: Guid.NewGuid());
        var bolt = new TriageCandidate(Guid.NewGuid(), 2010, 1505, 10, Confident: true, Lab: new TriageLab(40, 128, 128));
        var swaps = new List<TriageSwap>();

        var result = NewHoldTriage.Classify(Input([bolt], [orange]), q => q.Select(_ => (double?)0.2).ToList(), swaps);

        Assert.Equal(NewHoldDiscardReason.InsideCarriedHold, result[bolt.Id]);
        Assert.Empty(swaps);
    }

    [Fact]
    public void TheCapturesWhiteBalance_IsTakenOutBeforeTheColoursAreCompared()
    {
        // The new capture is bluer: every colour moved by (-25, -25) in a/b. The orange hold seen again is no swap.
        var orange = new TriageOutline(2000, 1500, 60, Lab: new TriageLab(76, 143, 146), OldHoldId: Guid.NewGuid());
        var again = new TriageCandidate(Guid.NewGuid(), 2010, 1505, 50, Confident: true, Lab: new TriageLab(80, 118, 121));
        var input = Input([again], [orange]);
        Func<IReadOnlyList<PresenceQuery>, IReadOnlyList<double?>> changed = q => q.Select(_ => (double?)0.3).ToList();

        Assert.Empty(NewHoldTriage.Classify(input, changed, []));
        Assert.Equal(
            NewHoldDiscardReason.InsideCarriedHold,
            NewHoldTriage.Classify(input with { OldToNewColourShift = new TriageLab(0, -25, -25) }, changed, [])[again.Id]);
    }

    [Fact]
    public void ADetectionOnlyTouchingACarriedHold_Stays()
    {
        var carried = new TriageOutline(2000, 1500, 60);
        var beside = new TriageCandidate(Guid.NewGuid(), 2090, 1500, 50, Confident: true);

        Assert.Empty(NewHoldTriage.Classify(Input([beside], [carried]), null));
    }

    [Fact]
    public void WithoutCarriedFootprints_TheRuleDoesNotRun()
    {
        var second = new TriageCandidate(Guid.NewGuid(), 2010, 1505, 50, Confident: true);

        Assert.Empty(NewHoldTriage.Classify(Input([second], null), null));
    }

    [Fact]
    public void AMarkerOnlyTheOldPhotoDecoded_IsStillAMarker()
    {
        // The old photo is the new one shifted by (+100, +50): the marker at old (1600, 1050) is at new (1500, 1000).
        var pairs = Identity.Select(p => p with { DstX = p.SrcX + 100, DstY = p.SrcY + 50 }).ToList();
        var onMarker = new TriageCandidate(Guid.NewGuid(), 1500, 1000, 30, Confident: true);
        var elsewhere = new TriageCandidate(Guid.NewGuid(), 2500, 2000, 30, Confident: true);
        var input = Input([onMarker, elsewhere], null) with { NewToOld = pairs, OldMarkerQuads = [Square(1600, 1050, 40)] };

        var result = NewHoldTriage.Classify(input, null);

        Assert.Equal(NewHoldDiscardReason.OnMarker, result[onMarker.Id]);
        Assert.False(result.ContainsKey(elsewhere.Id));
    }

    [Fact]
    public void AnOldPhotoMarker_IsNotMappedWhenThePhotosAreNotAligned()
    {
        var onMarker = new TriageCandidate(Guid.NewGuid(), 1500, 1000, 30, Confident: true);
        var input = Input([onMarker], null) with { NewToOld = [], OldMarkerQuads = [Square(1500, 1000, 40)] };

        Assert.Empty(NewHoldTriage.Classify(input, null));
    }

    private static NewHoldTriageInput Input(IReadOnlyList<TriageCandidate> candidates, IReadOnlyList<TriageOutline>? carried) =>
        new(candidates, Identity, (4000, 3000), [], CarriedOutlines: carried);

    private static List<(double X, double Y)> Square(double x, double y, double half) =>
        [(x - half, y - half), (x + half, y - half), (x + half, y + half), (x - half, y + half)];
}
