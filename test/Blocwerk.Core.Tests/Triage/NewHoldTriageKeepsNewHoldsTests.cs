// <copyright file="NewHoldTriageKeepsNewHoldsTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests.Triage;

/// <summary>
/// A detection on the spot of an old hold that was NOT found again is what is there now: it is never dropped as
/// "known" or "unchanged". Nor is a detection "known" for a carried hold of a clearly different size. On a wall that
/// did not change, second detections of the carried holds are still dropped, and markers and spots outside the old
/// photo keep their rules whatever sits there.
/// </summary>
public class NewHoldTriageKeepsNewHoldsTests
{
    private const int Side = 4000;

    // The prod case of issue #97: a volume (radius 0.044) detected at 0.511 / 0.328 over a small old hold (radius 0.019)
    // recorded at 0.497 / 0.327 that was not found again.
    private static readonly TriageCandidate Volume = new(Guid.NewGuid(), 0.511 * Side, 0.328 * Side, 0.044 * Side);
    private static readonly TriageSpot SmallOldHold = new(0.497 * Side, 0.327 * Side, 0.019 * Side);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ABigDetectionOverASmallOldHoldNotFoundAgain_IsKept_WhateverThePhotosAndTheModelSay(bool notFound)
    {
        var input = Input([Volume], notFound: notFound ? [SmallOldHold] : [], known: [Volume.Id]);

        var result = NewHoldTriage.Classify(input, AlwaysThere);

        Assert.Equal(notFound ? [] : [NewHoldDiscardReason.KnownHoldIn3D], result.Values);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AConfidentTracedDetection_IsNotDroppedBecauseTheSpotLooksUnchanged(bool confident)
    {
        // The owner's case: a hold the old wall had no record of, detected with confidence 0.997 and a good outline.
        var detection = new TriageCandidate(Guid.NewGuid(), 1000, 1000, 80, Confident: confident);

        var result = NewHoldTriage.Classify(Input([detection]), AlwaysThere);

        Assert.Equal(confident ? [] : [NewHoldDiscardReason.UnchangedSinceOldPhoto], result.Values);
    }

    [Fact]
    public void OnlyASureDetectionWithATracedOutline_IsConfident()
    {
        Assert.True(NewHoldTriage.IsConfident(0.95, outlined: true, outlineConfidence: 0.8));
        Assert.False(NewHoldTriage.IsConfident(0.8, outlined: true, outlineConfidence: 0.8));
        Assert.False(NewHoldTriage.IsConfident(0.99, outlined: false, outlineConfidence: null));
        Assert.False(NewHoldTriage.IsConfident(0.99, outlined: true, outlineConfidence: 0.2));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADetectionOnTheSpotOfAnOldHoldNotFoundAgain_IsNotUnchanged(bool notFound)
    {
        var detection = new TriageCandidate(Guid.NewGuid(), 1000, 1000, 80);
        var input = Input([detection], notFound: notFound ? [new TriageSpot(1030, 1010, 70)] : []);

        var result = NewHoldTriage.Classify(input, AlwaysThere);

        Assert.Equal(notFound ? [] : [NewHoldDiscardReason.UnchangedSinceOldPhoto], result.Values);
    }

    [Fact]
    public void AnOldHoldNotFoundAgainElsewhere_DoesNotKeepTheDetection()
    {
        var detection = new TriageCandidate(Guid.NewGuid(), 1000, 1000, 80);

        var result = NewHoldTriage.Classify(Input([detection], notFound: [new TriageSpot(1200, 1000, 70)]), AlwaysThere);

        Assert.Equal([NewHoldDiscardReason.UnchangedSinceOldPhoto], result.Values);
    }

    [Theory]
    [InlineData(80, true)]
    [InlineData(140, true)]
    [InlineData(150, false)]
    [InlineData(40, false)]
    public void ADetectionTheModelKnows_IsOnlyDroppedForACarriedHoldOfItsSize(double radius, bool dropped)
    {
        var detection = new TriageCandidate(Guid.NewGuid(), 1000, 1000, radius);
        var input = Input([detection], carried: [new TriageSpot(1010, 1000, 80)], known: [detection.Id]);

        var result = NewHoldTriage.Classify(input, null);

        Assert.Equal(dropped ? [NewHoldDiscardReason.KnownHoldIn3D] : [], result.Values);
    }

    [Fact]
    public void MarkersAndSpotsOutsideTheOldPhoto_KeepTheirRules_OnAnOldHoldNotFoundAgainToo()
    {
        var onMarker = new TriageCandidate(Guid.NewGuid(), 2000, 2000, 80);
        var outside = new TriageCandidate(Guid.NewGuid(), 4400, 1000, 80);
        var input = Input([onMarker, outside], notFound: [new TriageSpot(2000, 2000, 80), new TriageSpot(4400, 1000, 80)]) with
        {
            MarkerQuads = [[(1980, 1980), (2020, 1980), (2020, 2020), (1980, 2020)]],
        };

        var result = NewHoldTriage.Classify(input, AlwaysThere);

        Assert.Equal(NewHoldDiscardReason.OnMarker, result[onMarker.Id]);
        Assert.Equal(NewHoldDiscardReason.OutsideOldPhoto, result[outside.Id]);
    }

    // The lightly changed wall: 882 holds found again, each detected a second time, and 5 new holds.
    [Fact]
    public void OnAWallThatDidNotChange_SecondDetectionsAreDropped_AndOnlyTheNewHoldsStay()
    {
        var carried = Enumerable.Range(0, 882).Select(i => new TriageSpot(100.0 + (i % 42 * 90.0), 100.0 + (Math.Floor(i / 42.0) * 180.0), 30)).ToList();
        var seconds = carried.Select(s => new TriageCandidate(Guid.NewGuid(), s.X + 6, s.Y - 4, 28)).ToList();
        var known = seconds.Where((_, i) => i % 2 == 0).Select(c => c.Id).ToList();
        var fresh = Enumerable.Range(0, 5).Select(i => new TriageCandidate(Guid.NewGuid(), 145.0 + (i * 630.0), 190, 30)).ToList();

        // The new holds sit on the row at y 190, between the carried ones: only there the old photo showed something else.
        var result = NewHoldTriage.Classify(
            Input([.. seconds, .. fresh], carried: carried, known: known),
            queries => queries.Select(q => (double?)(Math.Abs(q.NewY - 190) < 1 ? 0.2 : 0.9)).ToList());

        Assert.Equal(882, result.Count);
        Assert.All(seconds, s => Assert.True(result.ContainsKey(s.Id)));
        Assert.Equal(441, result.Values.Count(r => r == NewHoldDiscardReason.KnownHoldIn3D));
        Assert.Equal(441, result.Values.Count(r => r == NewHoldDiscardReason.UnchangedSinceOldPhoto));
    }

    [Theory]
    [InlineData(40, 0)]
    [InlineData(111, 0)]
    [InlineData(112, 112)]
    [InlineData(176, 176)]
    public void ALargeDetection_IsComparedByItsOutline_ASmallOneByTheFixedPatch(double radius, double asked)
    {
        var seen = new List<PresenceQuery>();

        NewHoldTriage.Classify(Input([new TriageCandidate(Guid.NewGuid(), 1000, 1000, radius)]), q =>
        {
            seen.AddRange(q);
            return q.Select(_ => (double?)0.1).ToList();
        });

        Assert.Equal(asked, Assert.Single(seen).RadiusPx);
    }

    private static IReadOnlyList<double?> AlwaysThere(IReadOnlyList<PresenceQuery> queries) => queries.Select(_ => (double?)0.95).ToList();

    // The new photo and the old one show the wall from the same place (4000 x 4000).
    private static NewHoldTriageInput Input(
        IReadOnlyList<TriageCandidate> candidates,
        IReadOnlyList<TriageSpot>? carried = null,
        IReadOnlyList<TriageSpot>? notFound = null,
        IReadOnlyList<Guid>? known = null)
    {
        var pairs = Enumerable.Range(0, 5).SelectMany(i => Enumerable.Range(0, 5).Select(j =>
            new PointPair(i * 1000.0, j * 1000.0, i * 1000.0, j * 1000.0))).ToList();
        return new NewHoldTriageInput(
            candidates, pairs, (Side, Side), [], null,
            (known ?? []).ToDictionary(id => id, _ => Evidence3DVerdict.KnownHold),
            carried ?? [],
            notFound ?? []);
    }
}
