// <copyright file="NewHoldOutsideWallTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests.Triage;

/// <summary>
/// The outside-the-wall rule: an unpaired detection the detector is not sure of, more than <see cref="NewHoldWallRegion.MarginRadii"/>
/// hold radii beyond every carried hold, is suggested as a discard. A sure, outlined detection there, one inside the margin,
/// and every detection of a panel whose carried holds outline no wall stay proposed.
/// </summary>
public class NewHoldOutsideWallTests
{
    private const double Radius = 40;

    [Fact]
    public void Region_IsTheCarriedHoldsHull_GrownByTheMargin()
    {
        var region = NewHoldWallRegion.Of(Grid(), Enumerable.Repeat(Radius, 25))!;

        Assert.False(region.IsOutside(1500, 1500));
        Assert.False(region.IsOutside(2000 + (2 * Radius), 1500));
        Assert.True(region.IsOutside(2000 + (3 * Radius), 1500));
        Assert.True(region.IsOutside(1500, 1000 - (3 * Radius)));
    }

    [Fact]
    public void Region_NeedsEnoughSpotsAndAKnownHoldSize()
    {
        Assert.Null(NewHoldWallRegion.Of(Grid().Take(NewHoldWallRegion.MinimumSpots - 1).ToList(), [Radius]));
        Assert.Null(NewHoldWallRegion.Of(Grid(), [0, 0]));
    }

    [Fact]
    public void Classify_AnUnsureDetectionBeyondTheWall_IsOutsideTheWall_ASureOneStays()
    {
        var unsure = new TriageCandidate(Guid.NewGuid(), 2300, 1500, 30);
        var sure = new TriageCandidate(Guid.NewGuid(), 2300, 1600, 30, Confident: true);
        var near = new TriageCandidate(Guid.NewGuid(), 2060, 1500, 30);

        var result = NewHoldTriage.Classify(Input([unsure, sure, near]), null);

        Assert.Equal(NewHoldDiscardReason.OutsideWall, Assert.Single(result).Value);
        Assert.Equal(unsure.Id, result.Keys.Single());
    }

    [Fact]
    public void Classify_WithoutCarriedFootprints_TheWallRuleDoesNotRun()
    {
        var unsure = new TriageCandidate(Guid.NewGuid(), 2300, 1500, 30);

        var result = NewHoldTriage.Classify(Input([unsure]) with { CarriedOutlines = null }, null);

        Assert.Empty(result);
    }

    [Fact]
    public void Classify_ADetectionOnAModelMarker_IsOnMarker_AlsoAMarkerSideOff()
    {
        var quad = new List<(double X, double Y)> { (1450, 1450), (1530, 1450), (1530, 1530), (1450, 1530) };
        var onIt = new TriageCandidate(Guid.NewGuid(), 1490, 1490, 20);
        var offBy = new TriageCandidate(Guid.NewGuid(), 1490 + 75, 1490, 20);
        var beside = new TriageCandidate(Guid.NewGuid(), 1490 + 130, 1490, 20);

        var result = NewHoldTriage.Classify(Input([onIt, offBy, beside]) with { ModelMarkerQuads = [quad] }, null);

        Assert.Equal(NewHoldDiscardReason.OnMarker, result[onIt.Id]);
        Assert.Equal(NewHoldDiscardReason.OnMarker, result[offBy.Id]);
        Assert.False(result.ContainsKey(beside.Id));
    }

    /// <summary>Carried holds every 250 px over (1000..2000) × (1000..2000); the panel is not aligned, so only the geometry rules run.</summary>
    private static List<(double X, double Y)> Grid() =>
        [.. from x in Enumerable.Range(0, 5) from y in Enumerable.Range(0, 5) select (1000.0 + (250 * x), 1000.0 + (250 * y))];

    private static NewHoldTriageInput Input(IReadOnlyList<TriageCandidate> candidates) => new(
        candidates,
        [],
        null,
        [],
        CarriedOutlines: Grid().Select(p => new TriageOutline(p.X, p.Y, Radius)).ToList());
}
