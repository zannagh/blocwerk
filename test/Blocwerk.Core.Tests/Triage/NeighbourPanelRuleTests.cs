// <copyright file="NeighbourPanelRuleTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests.Triage;

/// <summary>
/// A neighbour photo's detection that the overlap maps inside the centre photo belongs to the centre —
/// unless the centre kept a new hold right there (the same new hold seen twice), or the spot is far from
/// every overlap pair (the mapping is not trusted there), or the centre has no hold of its own on that spot.
/// </summary>
public class NeighbourPanelRuleTests
{
    // The neighbour's left strip (x < 1000) shows the centre's right edge: centre = neighbour + (3000, 0).
    private static readonly List<PointPair> Overlap =
        [.. Enumerable.Range(0, 4).SelectMany(i => Enumerable.Range(0, 4).Select(j => new PointPair(
            100 + (i * 250), 500 + (j * 500), 3100 + (i * 250), 500 + (j * 500))))];

    [Fact]
    public void ShownByOwner_InsideTheCentreFrame_IsTheCentres()
    {
        Assert.True(NeighbourPanelRule.ShownByOwner(Owner([]), 400, 1200));
    }

    [Fact]
    public void ShownByOwner_OnAKeptNewCentreHold_StaysAsItsTwin()
    {
        Assert.False(NeighbourPanelRule.ShownByOwner(Owner([(3405, 1195)]), 400, 1200));
    }

    [Fact]
    public void ShownByOwner_BeyondTheCentreFrame_IsTheNeighbours()
    {
        Assert.False(NeighbourPanelRule.ShownByOwner(Owner([]), 1150, 1200));
    }

    [Fact]
    public void ShownByOwner_FarFromEveryOverlapPair_IsNotJudged()
    {
        Assert.False(NeighbourPanelRule.ShownByOwner(Owner([]), 850, 2600));
    }

    [Theory]
    [InlineData(3400, 1200, true)]
    [InlineData(3470, 1260, true)]
    [InlineData(3400, 1500, false)]
    public void ShownByOwner_OnlyWhereTheCentreHasAHoldOfItsOwn(double centreX, double centreY, bool shown)
    {
        var owner = Owner([]) with { OwnerHolds = [new TriageSpot(centreX, centreY, 60)] };

        Assert.Equal(shown, NeighbourPanelRule.ShownByOwner(owner, 400, 1200));
    }

    [Fact]
    public void ShownByOwner_WhereTheCentreHasNothing_StaysANewHold()
    {
        Assert.False(NeighbourPanelRule.ShownByOwner(Owner([]) with { OwnerHolds = [] }, 400, 1200));
    }

    [Fact]
    public void InsideTheMeasuredOverlap_ASpotFarFromEveryMeasuredPair_IsNotJudged()
    {
        // Measured pairs only along the strip's top and bottom rows (y 500 and 2000): (400, 1250) lies between them, within
        // reach of the unmeasured pairs at y 1000 / 1500 only.
        var measured = Overlap.Where(p => p.SrcY is 500 or 2000).ToList();

        Assert.False(NeighbourPanelRule.ShownByOwner(Owner([]) with { Measured = measured }, 400, 1250));
        Assert.True(NeighbourPanelRule.ShownByOwner(Owner([]) with { Measured = measured }, 400, 550));
    }

    [Fact]
    public void BeyondTheMeasuredOverlap_TheRuleWorksAsBefore()
    {
        // Measured pairs only in the strip's right half: (150, 1200) lies outside their hull, where the field is all there is.
        var measured = Overlap.Where(p => p.SrcX >= 600).ToList();

        Assert.True(NeighbourPanelRule.ShownByOwner(Owner([]) with { Measured = measured }, 150, 1200));
    }

    [Theory]
    [InlineData(0.40, false)]
    [InlineData(0.41, true)]
    public void OnlyAProposalAboveTheMatchersUnsupportedCap_IsMeasured(double confidence, bool measured)
    {
        Assert.Equal(measured, NeighbourPanelRule.IsMeasured(confidence));
    }

    private static OverlapOwner Owner(IReadOnlyList<(double X, double Y)> keptNew) =>
        new(Overlap, (4000, 3000), keptNew, (3000, 4000));
}
