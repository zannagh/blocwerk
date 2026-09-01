using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Cover for <see cref="BoulderHoldReconciler"/>: the twin-aware read-time collapse of a boulder's
/// holds to one entry per physical hold. Prominence must match the detail viewer
/// (<c>BoulderDetail.razor</c> <c>ExpandLinkedTwinsAsync</c>): Type Top &gt; Start &gt; Normal, and
/// Usage hand-capable &gt; foot-only.
/// </summary>
public class BoulderHoldReconcilerTests
{
    private static readonly Guid HoldA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid HoldB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid HoldC = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void Reconcile_NoLinks_IsIdentity()
    {
        var rows = new[]
        {
            new ReconcilableHold(HoldA, HoldType.Start, HoldUsage.HandAndFoot),
            new ReconcilableHold(HoldB, HoldType.Normal, HoldUsage.FootOnly),
        };

        var result = BoulderHoldReconciler.Reconcile(rows, []);

        Assert.Equal(2, result.Count);
        Assert.All(result, entry => Assert.Single(entry.HoldIds));
        Assert.Contains(result, e => e.HoldIds[0] == HoldA && e.Type == HoldType.Start && e.Usage == HoldUsage.HandAndFoot);
        Assert.Contains(result, e => e.HoldIds[0] == HoldB && e.Type == HoldType.Normal && e.Usage == HoldUsage.FootOnly);
    }

    [Fact]
    public void Reconcile_EmptyRows_ReturnsEmpty()
    {
        var result = BoulderHoldReconciler.Reconcile([], [new HoldLinkPair(HoldA, HoldB)]);

        Assert.Empty(result);
    }

    [Fact]
    public void Reconcile_OneSided_ExpandsMembershipToUnsavedTwin()
    {
        // The boulder saved only HoldA; HoldA is linked to its panel-2 twin HoldB.
        var rows = new[] { new ReconcilableHold(HoldA, HoldType.Start, HoldUsage.HandOnly) };
        var links = new[] { new HoldLinkPair(HoldA, HoldB) };

        var result = BoulderHoldReconciler.Reconcile(rows, links);

        var entry = Assert.Single(result);
        Assert.Equal(2, entry.HoldIds.Count);
        Assert.Contains(HoldA, entry.HoldIds);
        Assert.Contains(HoldB, entry.HoldIds);
        Assert.Equal(HoldType.Start, entry.Type);
        Assert.Equal(HoldUsage.HandOnly, entry.Usage);
    }

    [Fact]
    public void Reconcile_BothSided_CollapsesToOneEntry_MostProminentType()
    {
        // Both twins saved as separate rows with conflicting Type: Normal vs Top -> Top wins, one entry.
        var rows = new[]
        {
            new ReconcilableHold(HoldA, HoldType.Normal, HoldUsage.HandAndFoot),
            new ReconcilableHold(HoldB, HoldType.Top, HoldUsage.HandAndFoot),
        };
        var links = new[] { new HoldLinkPair(HoldA, HoldB) };

        var result = BoulderHoldReconciler.Reconcile(rows, links);

        var entry = Assert.Single(result);
        Assert.Equal(2, entry.HoldIds.Count);
        Assert.Equal(HoldType.Top, entry.Type);
    }

    [Theory]
    [InlineData(HoldType.Normal, HoldType.Start, HoldType.Start)]
    [InlineData(HoldType.Start, HoldType.Top, HoldType.Top)]
    [InlineData(HoldType.Normal, HoldType.Top, HoldType.Top)]
    public void Reconcile_TypeProminence_HigherValueWins(HoldType a, HoldType b, HoldType expected)
    {
        var rows = new[]
        {
            new ReconcilableHold(HoldA, a, HoldUsage.HandAndFoot),
            new ReconcilableHold(HoldB, b, HoldUsage.HandAndFoot),
        };
        var links = new[] { new HoldLinkPair(HoldA, HoldB) };

        var entry = Assert.Single(BoulderHoldReconciler.Reconcile(rows, links));

        Assert.Equal(expected, entry.Type);
    }

    [Theory]
    [InlineData(HoldUsage.HandOnly, HoldUsage.FootOnly, HoldUsage.HandOnly)]
    [InlineData(HoldUsage.HandAndFoot, HoldUsage.FootOnly, HoldUsage.HandAndFoot)]
    [InlineData(HoldUsage.HandAndFoot, HoldUsage.HandOnly, HoldUsage.HandAndFoot)]
    public void Reconcile_UsageProminence_HandCapableBeatsFootOnly(HoldUsage a, HoldUsage b, HoldUsage expected)
    {
        var rows = new[]
        {
            new ReconcilableHold(HoldA, HoldType.Normal, a),
            new ReconcilableHold(HoldB, HoldType.Normal, b),
        };
        var links = new[] { new HoldLinkPair(HoldA, HoldB) };

        var entry = Assert.Single(BoulderHoldReconciler.Reconcile(rows, links));

        Assert.Equal(expected, entry.Usage);
    }

    [Fact]
    public void Reconcile_UnlinkedHoldsPassThrough_LinkedHoldsCollapse()
    {
        // HoldA and HoldB are linked twins; HoldC is an unrelated hold with no link.
        var rows = new[]
        {
            new ReconcilableHold(HoldA, HoldType.Normal, HoldUsage.HandAndFoot),
            new ReconcilableHold(HoldB, HoldType.Start, HoldUsage.HandAndFoot),
            new ReconcilableHold(HoldC, HoldType.Top, HoldUsage.HandAndFoot),
        };
        var links = new[] { new HoldLinkPair(HoldA, HoldB) };

        var result = BoulderHoldReconciler.Reconcile(rows, links);

        // Two physical holds: the A/B twin pair collapsed, plus the standalone HoldC.
        Assert.Equal(2, result.Count);
        var twin = Assert.Single(result, e => e.HoldIds.Count == 2);
        Assert.Equal(HoldType.Start, twin.Type);
        var single = Assert.Single(result, e => e.HoldIds.Count == 1);
        Assert.Equal(HoldC, single.HoldIds[0]);
        Assert.Equal(HoldType.Top, single.Type);
    }

    [Fact]
    public void Reconcile_TransitiveChain_CollapsesToOneComponent()
    {
        // A-B and B-C linked: a three-panel physical hold. Saving only the middle twin still covers all.
        var rows = new[] { new ReconcilableHold(HoldB, HoldType.Top, HoldUsage.HandAndFoot) };
        var links = new[] { new HoldLinkPair(HoldA, HoldB), new HoldLinkPair(HoldB, HoldC) };

        var entry = Assert.Single(BoulderHoldReconciler.Reconcile(rows, links));

        Assert.Equal(3, entry.HoldIds.Count);
        Assert.Contains(HoldA, entry.HoldIds);
        Assert.Contains(HoldB, entry.HoldIds);
        Assert.Contains(HoldC, entry.HoldIds);
    }
}
