// <copyright file="FullRevisionNeighboursTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests.Triage;

/// <summary>
/// In a full revision every centre hold is new, the neighbour-panel rule spares a detection on a kept new centre hold,
/// and the quick review links only sure pairs: on The Attic (2026-10-14 rehearsal) 296 of 509 right-panel holds became a
/// second copy of a centre hold. The overlap matcher's pairs now decide which neighbour detections are seen again.
/// </summary>
public class FullRevisionNeighboursTests
{
    private static readonly Guid Panel = Guid.NewGuid();

    [Fact]
    public void AnUnsurePair_LeavesTheNeighbourDetectionOut_TheCentreKeepsTheHold()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());

        var result = FullRevisionNeighbours.WithPairedTwins(new Dictionary<Guid, NewHoldDiscardReason>(), Neighbours(Pair(a, b, 0.6)));

        Assert.Equal(NewHoldDiscardReason.SeenOnNeighbourPanel, Assert.Single(result).Value);
        Assert.Equal(b, result.Single().Key);
    }

    [Fact]
    public void ASurePair_KeepsTheNeighbourDetection_SoTheQuickReviewLinksIt()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());

        var result = FullRevisionNeighbours.WithPairedTwins(new Dictionary<Guid, NewHoldDiscardReason>(), Neighbours(Pair(a, b, 0.95)));

        Assert.Empty(result);
    }

    [Fact]
    public void ASurePair_WinsOverAnUnsurePairOfTheSameNeighbourDetection()
    {
        var b = Guid.NewGuid();

        var result = FullRevisionNeighbours.WithPairedTwins(
            new Dictionary<Guid, NewHoldDiscardReason>(), Neighbours(Pair(Guid.NewGuid(), b, 0.5), Pair(Guid.NewGuid(), b, 0.93)));

        Assert.Empty(result);
    }

    [Fact]
    public void AMovedPair_OrOneWhoseCentreHoldIsDropped_LeavesTheNeighbourDetectionAlone()
    {
        var droppedCentre = Guid.NewGuid();
        var discards = new Dictionary<Guid, NewHoldDiscardReason> { [droppedCentre] = NewHoldDiscardReason.OnMarker };

        var result = FullRevisionNeighbours.WithPairedTwins(
            discards, Neighbours(Pair(Guid.NewGuid(), Guid.NewGuid(), 0.6, moved: true), Pair(droppedCentre, Guid.NewGuid(), 0.6)));

        Assert.Equal(discards, result);
    }

    [Fact]
    public void AnEarlierReason_IsKept_AndADetectionWithoutAPairStays()
    {
        var (a, b, alone) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var discards = new Dictionary<Guid, NewHoldDiscardReason> { [b] = NewHoldDiscardReason.OnMarker };

        var result = FullRevisionNeighbours.WithPairedTwins(discards, Neighbours(Pair(a, b, 0.6)));

        Assert.Equal(NewHoldDiscardReason.OnMarker, result[b]);
        Assert.DoesNotContain(alone, result.Keys);
        Assert.Single(result);
    }

    private static OverlapProposalDto Pair(Guid centre, Guid neighbour, double confidence, bool moved = false) =>
        new(Guid.Empty, centre, neighbour, confidence, moved, 3);

    private static List<NeighbourOverlap> Neighbours(params OverlapProposalDto[] pairs) => [new(Panel, 1, 0, [.. pairs])];
}
