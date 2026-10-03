// <copyright file="NeighbourPanelRuleTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests.Triage;

/// <summary>
/// A neighbour photo's detection that the overlap maps inside the centre photo belongs to the centre —
/// unless the centre kept a new hold right there (the same new hold seen twice), or the spot is far from
/// every overlap pair (the mapping is not trusted there).
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

    private static OverlapOwner Owner(IReadOnlyList<(double X, double Y)> keptNew) =>
        new(Overlap, (4000, 3000), keptNew, (3000, 4000));
}
