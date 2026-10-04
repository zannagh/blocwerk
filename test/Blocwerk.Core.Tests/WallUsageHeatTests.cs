using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>Cover for <see cref="WallUsageHeat"/>: which boulders count and how often each hold is used.</summary>
public class WallUsageHeatTests
{
    private static readonly Guid HoldA = Guid.NewGuid();
    private static readonly Guid HoldB = Guid.NewGuid();
    private static readonly Guid HoldC = Guid.NewGuid();
    private static readonly Guid Kick = Guid.NewGuid();

    private static Boulder MakeBoulder(
        bool archived = false,
        bool draft = false,
        bool historic = false,
        bool kickboard = false,
        params (Guid Hold, HoldType Type)[] holds) => new()
    {
        Name = "b",
        IsArchived = archived,
        IsDraft = draft,
        IsHistoric = historic,
        KickboardFootholdsOn = kickboard,
        BoulderHolds = holds.Select(h => new BoulderHold { HoldId = h.Hold, Type = h.Type }).ToList(),
    };

    [Fact]
    public void CountsDistinctBouldersPerHold_RolesCountTheSame()
    {
        var boulders = new[]
        {
            MakeBoulder(holds: [(HoldA, HoldType.Start), (HoldB, HoldType.Normal)]),
            MakeBoulder(holds: [(HoldA, HoldType.Top)]),
        };

        var counts = WallUsageHeat.CountByHold(boulders, []);

        Assert.Equal(2, counts[HoldA]);
        Assert.Equal(1, counts[HoldB]);
        Assert.False(counts.ContainsKey(HoldC));
    }

    [Fact]
    public void ArchivedDraftAndHistoricBouldersAreExcluded()
    {
        var boulders = new[]
        {
            MakeBoulder(archived: true, holds: [(HoldA, HoldType.Normal)]),
            MakeBoulder(draft: true, holds: [(HoldA, HoldType.Normal)]),
            MakeBoulder(historic: true, holds: [(HoldA, HoldType.Normal)]),
            MakeBoulder(holds: [(HoldB, HoldType.Normal)]),
        };

        var counts = WallUsageHeat.CountByHold(boulders, []);

        Assert.False(counts.ContainsKey(HoldA));
        Assert.Equal(1, counts[HoldB]);
    }

    [Fact]
    public void LinkedTwinsShareOneCount_AndABoulderSavingBothCountsOnce()
    {
        var boulders = new[]
        {
            MakeBoulder(holds: [(HoldA, HoldType.Normal), (HoldB, HoldType.Normal)]),
            MakeBoulder(holds: [(HoldA, HoldType.Normal)]),
        };

        var counts = WallUsageHeat.CountByHold(boulders, [new HoldLinkPair(HoldA, HoldB)]);

        Assert.Equal(2, counts[HoldA]);
        Assert.Equal(2, counts[HoldB]);
    }

    [Fact]
    public void KickboardHoldsCountForEveryBoulderWithKickboardOn()
    {
        var boulders = new[]
        {
            MakeBoulder(kickboard: true, holds: [(HoldA, HoldType.Normal)]),
            MakeBoulder(kickboard: true, holds: [(HoldB, HoldType.Normal)]),
            MakeBoulder(kickboard: false, holds: [(HoldB, HoldType.Normal)]),
        };

        var counts = WallUsageHeat.CountByHold(boulders, [], [Kick]);

        Assert.Equal(2, counts[Kick]);
    }

    [Fact]
    public void ExplicitKickboardHoldIsNotDoubleCounted()
    {
        var boulders = new[] { MakeBoulder(kickboard: true, holds: [(Kick, HoldType.Normal)]) };

        var counts = WallUsageHeat.CountByHold(boulders, [], [Kick]);

        Assert.Equal(1, counts[Kick]);
    }

    [Fact]
    public void KickboardHoldsOfExcludedBouldersDoNotCount()
    {
        var boulders = new[] { MakeBoulder(archived: true, kickboard: true, holds: [(HoldA, HoldType.Normal)]) };

        Assert.Empty(WallUsageHeat.CountByHold(boulders, [], [Kick]));
    }

    [Fact]
    public void RecomputingAfterABoulderChangesReflectsTheChange()
    {
        var boulder = MakeBoulder(holds: [(HoldA, HoldType.Normal)]);
        Assert.Equal(1, WallUsageHeat.CountByHold([boulder], [])[HoldA]);

        boulder.IsArchived = true;

        Assert.Empty(WallUsageHeat.CountByHold([boulder], []));
    }
}
