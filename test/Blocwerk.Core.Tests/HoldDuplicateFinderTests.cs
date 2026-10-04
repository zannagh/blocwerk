// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Tests.RealData;

namespace Blocwerk.Core.Tests;

/// <summary>The pure detection of possible duplicate holds on one photo.</summary>
public sealed class HoldDuplicateFinderTests
{
    private static int next;

    [Fact]
    public void AnAutoHoldWhoseCentreIsInsideAManualHoldIsFlagged_AndTheManualHoldIsLeft()
    {
        var manual = Hold(0.3, 0.3, 0.05, auto: false);
        var auto = Hold(0.31, 0.3, 0.02, auto: true);

        var found = HoldDuplicateFinder.Find([auto, manual]).Single();

        Assert.Equal((manual.Id, auto.Id, HoldDuplicateKind.InsideHandPlaced), (found.HoldA, found.HoldB, found.Kind));
        Assert.Equal(HoldMergeMode.KeepHandUseDetectedShape, found.SuggestedMode);
    }

    [Fact]
    public void AnAutoHoldOverlappingAManualOneWithItsCentreOutsideIsNotFlagged()
    {
        var manual = Hold(0.3, 0.3, 0.05, auto: false);
        var auto = Hold(0.37, 0.3, 0.03, auto: true);

        Assert.Empty(HoldDuplicateFinder.Find([manual, auto]));
    }

    [Fact]
    public void TwoNearIdenticalAutoDetectionsAreFlagged_UnlessTheColoursDiffer()
    {
        var a = Hold(0.5, 0.5, 0.04, auto: true, color: "red");
        var b = Hold(0.505, 0.5, 0.04, auto: true, color: "red");
        var c = Hold(0.5, 0.5, 0.04, auto: true, color: "blue");

        var same = HoldDuplicateFinder.Find([a, b]).Single();
        Assert.Equal(HoldDuplicateKind.NearDuplicateAutomatic, same.Kind);
        Assert.Empty(HoldDuplicateFinder.Find([a, c]));
    }

    [Fact]
    public void DistinctNeighboursAndTouchingHoldsAreNotFlagged()
    {
        var a = Hold(0.5, 0.5, 0.04, auto: true);
        var near = Hold(0.575, 0.5, 0.04, auto: true);
        var far = Hold(0.9, 0.9, 0.04, auto: true);
        var partly = Hold(0.55, 0.5, 0.04, auto: true);

        Assert.Empty(HoldDuplicateFinder.Find([a, near, far]));
        Assert.Empty(HoldDuplicateFinder.Find([a, partly]));
    }

    [Fact]
    public void TwoHandPlacedHoldsAreFlaggedOnlyWhenAlmostIdentical()
    {
        var a = Hold(0.5, 0.5, 0.04, auto: false);
        var same = Hold(0.502, 0.5, 0.04, auto: false);
        var partly = Hold(0.54, 0.5, 0.04, auto: false);

        Assert.Equal(HoldDuplicateKind.HandPlacedPair, HoldDuplicateFinder.Find([a, same]).Single().Kind);
        Assert.Empty(HoldDuplicateFinder.Find([a, partly]));
    }

    [Fact]
    public void AVirtualHoldNextToADetectionIsFlaggedAsHandMadeAgainstDetected()
    {
        var virt = Hold(0.5, 0.5, 0.03, auto: false);
        virt.IsVirtual = true;
        var detected = Hold(0.52, 0.5, 0.04, auto: true);

        var found = HoldDuplicateFinder.Find([detected, virt]).Single();

        Assert.Equal((virt.Id, HoldDuplicateKind.InsideHandPlaced), (found.HoldA, found.Kind));
    }

    [Fact]
    public void TheKeeperIsTheHandMadeHold_ThenTheOneMoreBouldersUse_ThenTheLarger()
    {
        var small = Hold(0.5, 0.5, 0.04, auto: true);
        var large = Hold(0.5, 0.5, 0.045, auto: true);
        var used = Hold(0.5, 0.5, 0.04, auto: true);

        Assert.Equal(large.Id, HoldDuplicateFinder.Find([small, large]).Single().HoldA);
        Assert.Equal(small.Id, HoldDuplicateFinder.Find([small, large], new Dictionary<Guid, int> { [small.Id] = 2 }).Single().HoldA);
        Assert.Equal(used.Id, HoldDuplicateFinder.Find([small, used], new Dictionary<Guid, int> { [used.Id] = 1 }).Single().HoldA);
    }

    [Fact]
    public void ARoleIsMergedToTheMoreProminent_AndUsesAreCombined()
    {
        Assert.Equal(HoldType.Top, HoldMergeRules.MergeType(HoldType.Start, HoldType.Top));
        Assert.Equal(HoldType.Start, HoldMergeRules.MergeType(HoldType.Normal, HoldType.Start));
        Assert.Equal(HoldUsage.HandAndFoot, HoldMergeRules.MergeUsage(HoldUsage.HandOnly, HoldUsage.FootOnly));
        Assert.Equal(HoldUsage.HandOnly, HoldMergeRules.MergeUsage(HoldUsage.HandOnly, HoldUsage.HandOnly));
    }

    [Fact]
    public void OnTheAtticTheRealHoldsGiveSuggestionsOfEveryKind_AndNoPairTwice()
    {
        var all = AtticRealData.HoldPanels().SelectMany(p => HoldDuplicateFinder.Find(p)).ToList();

        Assert.NotEmpty(all);
        Assert.Equal(all.Count, all.Select(c => c.Key).Distinct().Count());
        Assert.All(all, c => Assert.InRange(c.Confidence, 0.5, 1.0));
    }

    private static Hold Hold(double x, double y, double radius, bool auto, string? color = null) => new()
    {
        Id = new Guid(Interlocked.Increment(ref next), 0, 0, new byte[8]),
        X = x,
        Y = y,
        Radius = radius,
        IsAutoDetected = auto,
        Color = color,
        OutlineSource = auto ? HoldOutlineSource.AutoCircle : HoldOutlineSource.Manual,
    };
}
