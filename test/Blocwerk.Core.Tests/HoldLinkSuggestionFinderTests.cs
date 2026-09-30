// <copyright file="HoldLinkSuggestionFinderTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.HoldLinks;

namespace Blocwerk.Core.Tests;

/// <summary>The pure pairing of <see cref="HoldLinkSuggestionFinder"/>: which cross-panel pairs are suggested.</summary>
public class HoldLinkSuggestionFinderTests
{
    private static readonly Guid PanelA = Guid.NewGuid();
    private static readonly Guid PanelB = Guid.NewGuid();
    private static readonly HashSet<(Guid A, Guid B)> NoPairs = [];

    [Fact]
    public void NearbyHoldsOnDifferentPanels_AreSuggested()
    {
        var a = Hold(PanelA, 1000, 1000);
        var b = Hold(PanelB, 1030, 1000);

        var pair = Assert.Single(HoldLinkSuggestionFinder.Find([a, b], NoPairs, NoPairs));

        Assert.Equal(HoldLinkPairSuggestion.Key(a.Id, b.Id), (pair.HoldAId, pair.HoldBId));
        Assert.Equal(30, pair.DistanceMm, 1);
    }

    [Fact]
    public void SamePanel_IsNotSuggested()
    {
        Assert.Empty(HoldLinkSuggestionFinder.Find([Hold(PanelA, 1000, 1000), Hold(PanelA, 1010, 1000)], NoPairs, NoPairs));
    }

    [Fact]
    public void FarApart_IsNotSuggested()
    {
        // 90 mm stand-in sizes: the limit is max(60, 0.5 × 180) = 90 mm.
        Assert.Empty(HoldLinkSuggestionFinder.Find([Hold(PanelA, 1000, 1000), Hold(PanelB, 1100, 1000)], NoPairs, NoPairs));
    }

    [Fact]
    public void LargerHolds_AllowAWiderGap()
    {
        var pairs = HoldLinkSuggestionFinder.Find([Hold(PanelA, 1000, 1000, size: 180), Hold(PanelB, 1150, 1000, size: 160)], NoPairs, NoPairs);

        Assert.Single(pairs);
    }

    [Fact]
    public void AlreadyLinked_IsNotSuggested()
    {
        var a = Hold(PanelA, 1000, 1000);
        var b = Hold(PanelB, 1010, 1000);

        Assert.Empty(HoldLinkSuggestionFinder.Find([a, b], [(b.Id, a.Id)], NoPairs));
    }

    [Fact]
    public void Rejected_IsNotSuggested()
    {
        var a = Hold(PanelA, 1000, 1000);
        var b = Hold(PanelB, 1010, 1000);

        Assert.Empty(HoldLinkSuggestionFinder.Find([a, b], NoPairs, new HashSet<(Guid, Guid)> { HoldLinkPairSuggestion.Key(a.Id, b.Id) }));
    }

    [Fact]
    public void DifferentColours_OrVeryDifferentSizes_AreNotSuggested()
    {
        Assert.Empty(HoldLinkSuggestionFinder.Find([Hold(PanelA, 1000, 1000, "red"), Hold(PanelB, 1010, 1000, "blue")], NoPairs, NoPairs));
        Assert.Empty(HoldLinkSuggestionFinder.Find([Hold(PanelA, 1000, 1000, size: 40), Hold(PanelB, 1010, 1000, size: 120)], NoPairs, NoPairs));
        Assert.Single(HoldLinkSuggestionFinder.Find([Hold(PanelA, 1000, 1000, "red"), Hold(PanelB, 1010, 1000)], NoPairs, NoPairs));
    }

    [Fact]
    public void AHold_IsSuggestedOncePerOtherPanel_ClosestFirst()
    {
        var a = Hold(PanelA, 1000, 1000);
        var near = Hold(PanelB, 1010, 1000);
        var farther = Hold(PanelB, 1040, 1000);

        var pair = Assert.Single(HoldLinkSuggestionFinder.Find([a, near, farther], NoPairs, NoPairs));

        Assert.Contains(near.Id, new[] { pair.HoldAId, pair.HoldBId });
    }

    [Fact]
    public void AHoldAlreadyLinkedToThatPanel_IsNotSuggestedAgain()
    {
        var a = Hold(PanelA, 1000, 1000);
        var linkedTwin = Hold(PanelB, 1200, 1000);
        var lookAlike = Hold(PanelB, 1010, 1000);

        Assert.Empty(HoldLinkSuggestionFinder.Find([a, linkedTwin, lookAlike], [(a.Id, linkedTwin.Id)], NoPairs));
    }

    [Fact]
    public void AHoldLinkedToAnUnplacedHoldOfThatPanel_IsNotSuggested()
    {
        // The twin on panel B is not placed on the 3D model, so it is no candidate; its panel still counts.
        var a = Hold(PanelA, 1000, 1000);
        var lookAlike = Hold(PanelB, 1010, 1000);
        var unplacedTwin = Guid.NewGuid();
        var panelOf = new Dictionary<Guid, Guid> { [a.Id] = PanelA, [lookAlike.Id] = PanelB, [unplacedTwin] = PanelB };

        Assert.Empty(HoldLinkSuggestionFinder.Find([a, lookAlike], [(a.Id, unplacedTwin)], NoPairs, panelOf));
    }

    [Fact]
    public void AdjacentFacets_CompareIn3D()
    {
        // Same world point reached through two facets: z differs by 20 mm only.
        var a = new HoldLinkCandidate(Guid.NewGuid(), PanelA, null, null, false, [2000, 0, 1500]);
        var b = new HoldLinkCandidate(Guid.NewGuid(), PanelB, null, null, false, [2000, 5, 1520]);

        Assert.Single(HoldLinkSuggestionFinder.Find([a, b], NoPairs, NoPairs));
    }

    private static HoldLinkCandidate Hold(Guid panel, double x, double z, string? color = null, double? size = null) =>
        new(Guid.NewGuid(), panel, color, size, false, [x, 0, z]);
}
