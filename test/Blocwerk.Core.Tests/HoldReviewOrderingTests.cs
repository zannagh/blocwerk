// <copyright file="HoldReviewOrderingTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Cover for <see cref="HoldReviewOrdering"/>: the wall-update review queues must put holds that carry
/// a live boulder first, keep their previous ranking inside each group, and never let a historic-only
/// hold jump the queue.
/// </summary>
public class HoldReviewOrderingTests
{
    private static readonly Guid Plain = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OnBoulder = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OnDraft = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid OnHistoric = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static HoldUsageRef Usage(bool isDraft, bool isHistoric) =>
        new(Guid.NewGuid(), "Boulder", "6A", HoldType.Normal, HoldUsage.HandAndFoot, isDraft, isHistoric);

    private static Dictionary<Guid, List<HoldUsageRef>> SampleUsage() => new()
    {
        [OnBoulder] = [Usage(isDraft: false, isHistoric: false)],
        [OnDraft] = [Usage(isDraft: true, isHistoric: false)],
        [OnHistoric] = [Usage(isDraft: false, isHistoric: true)],
    };

    [Fact]
    public void LiveBoulderHoldIds_TakesLiveAndDraft_NotHistoricOnly()
    {
        var ids = HoldReviewOrdering.LiveBoulderHoldIds(SampleUsage());

        Assert.Contains(OnBoulder, ids);
        Assert.Contains(OnDraft, ids);
        Assert.DoesNotContain(OnHistoric, ids);
        Assert.DoesNotContain(Plain, ids);
    }

    [Fact]
    public void LiveBoulderHoldIds_HoldOnBothHistoricAndLive_Counts()
    {
        var usage = new Dictionary<Guid, List<HoldUsageRef>>
        {
            [OnBoulder] = [Usage(isDraft: false, isHistoric: true), Usage(isDraft: false, isHistoric: false)],
        };

        Assert.Contains(OnBoulder, HoldReviewOrdering.LiveBoulderHoldIds(usage));
    }

    [Fact]
    public void LiveBoulderHoldIds_EmptyUsage_IsEmpty()
    {
        Assert.Empty(HoldReviewOrdering.LiveBoulderHoldIds(new Dictionary<Guid, List<HoldUsageRef>>()));
    }

    [Fact]
    public void BoulderFirst_LiftsBoulderHolds_AndKeepsOriginalOrderWithinGroups()
    {
        var boulderHolds = HoldReviewOrdering.LiveBoulderHoldIds(SampleUsage());
        var queue = new[] { Plain, OnHistoric, OnBoulder, OnDraft };

        var ordered = HoldReviewOrdering.BoulderFirst(queue, id => id, boulderHolds);

        Assert.Equal([OnBoulder, OnDraft, Plain, OnHistoric], ordered);
    }

    [Fact]
    public void BoulderFirstThenByDescending_RanksInsideEachGroupOnly()
    {
        var boulderHolds = HoldReviewOrdering.LiveBoulderHoldIds(SampleUsage());
        var queue = new (Guid Id, double Residual)[]
        {
            (Plain, 900),
            (OnBoulder, 1),
            (OnDraft, 50),
            (OnHistoric, 500),
        };

        var ordered = HoldReviewOrdering.BoulderFirstThenByDescending(
            queue,
            item => item.Id,
            item => item.Residual,
            boulderHolds);

        // A boulder hold with a tiny residual still outranks a 900px non-boulder one.
        Assert.Equal([OnDraft, OnBoulder, Plain, OnHistoric], ordered.Select(o => o.Id));
    }

    [Fact]
    public void BoulderFirst_NullHoldId_NeverRanksAsBoulderHold()
    {
        var boulderHolds = HoldReviewOrdering.LiveBoulderHoldIds(SampleUsage());

        Assert.False(HoldReviewOrdering.IsBoulderHold(null, boulderHolds));

        var queue = new Guid?[] { null, OnBoulder };
        var ordered = HoldReviewOrdering.BoulderFirst(queue, id => id, boulderHolds);

        Assert.Equal([OnBoulder, null], ordered);
    }

    [Fact]
    public void BoulderFirst_NoBoulderHolds_IsIdentity()
    {
        var queue = new[] { Plain, OnHistoric, OnBoulder };

        var ordered = HoldReviewOrdering.BoulderFirst(queue, id => id, new HashSet<Guid>());

        Assert.Equal(queue, ordered);
    }
}
