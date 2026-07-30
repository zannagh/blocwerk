using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Covers the boulder foothold rules. A "hands follow feet" boulder has no dedicated
/// footholds at all, so a FootOnly mark leaking through would make the boulder unclimbable
/// as written.
/// </summary>
public class BoulderRuleTests
{
    [Fact]
    public async Task CreateBoulder_CoercesFootOnly_WhenHandsFollowFeet()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);

        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId,
            "B",
            null,
            [
                new BoulderHoldInput(holds[0].Id, HoldType.Start, HoldUsage.FootOnly),
                new BoulderHoldInput(holds[1].Id, HoldType.Top, HoldUsage.HandOnly),
            ],
            handsFollowFeet: true);

        await using var check = h.CreateContext();
        var saved = await check.Boulders
            .Include(b => b.BoulderHolds)
            .FirstAsync(b => b.Id == boulder.Id);

        Assert.DoesNotContain(saved.BoulderHolds, bh => bh.Usage == HoldUsage.FootOnly);
        Assert.Equal(HoldUsage.HandAndFoot, saved.BoulderHolds.First(bh => bh.HoldId == holds[0].Id).Usage);

        // A hand-only mark is untouched: it says nothing about footholds.
        Assert.Equal(HoldUsage.HandOnly, saved.BoulderHolds.First(bh => bh.HoldId == holds[1].Id).Usage);
    }

    [Fact]
    public async Task CreateBoulder_KeepsFootOnly_WhenHandsDoNotFollowFeet()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);

        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId,
            "B",
            null,
            [new BoulderHoldInput(holds[0].Id, HoldType.Start, HoldUsage.FootOnly)],
            handsFollowFeet: false);

        await using var check = h.CreateContext();
        var saved = await check.Boulders
            .Include(b => b.BoulderHolds)
            .FirstAsync(b => b.Id == boulder.Id);

        Assert.Equal(HoldUsage.FootOnly, Assert.Single(saved.BoulderHolds).Usage);
    }

    [Fact]
    public async Task UpdateBoulder_CoercesFootOnly_WhenHandsFollowFeet()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);

        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)], handsFollowFeet: false);

        await h.BoulderService.UpdateBoulderAsync(
            boulder.Id,
            "B",
            null,
            [new BoulderHoldInput(holds[1].Id, HoldType.Normal, HoldUsage.FootOnly)],
            handsFollowFeet: true);

        await using var check = h.CreateContext();
        var saved = await check.Boulders
            .Include(b => b.BoulderHolds)
            .FirstAsync(b => b.Id == boulder.Id);

        Assert.Equal(HoldUsage.HandAndFoot, Assert.Single(saved.BoulderHolds).Usage);
    }

    [Fact]
    public async Task ReviseBoulder_CoercesFootOnly_WhenHandsFollowFeet()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);

        var draft = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "WIP", null, [new BoulderHoldInput(holds[0].Id)], isDraft: true);

        await h.BoulderService.ReviseBoulderAsync(
            draft.Id,
            [new BoulderHoldInput(holds[1].Id, HoldType.Normal, HoldUsage.FootOnly)]);

        await using var check = h.CreateContext();
        var saved = await check.Boulders
            .Include(b => b.BoulderHolds)
            .FirstAsync(b => b.Id == draft.Id);

        Assert.Equal(HoldUsage.HandAndFoot, Assert.Single(saved.BoulderHolds).Usage);
    }

    [Theory]
    [InlineData(true, null, FootholdMode.AllKickboard)]
    [InlineData(false, null, FootholdMode.DefinedOnly)]
    [InlineData(true, "yellow", FootholdMode.DefinedOnly)]
    [InlineData(false, "yellow", FootholdMode.DefinedOnly)]
    public void FootholdMode_FollowsTheRules(bool handsFollowFeet, string? footColorOnly, FootholdMode expected)
    {
        var boulder = new Boulder
        {
            Name = "B",
            HandsFollowFeet = handsFollowFeet,
            FootColorOnly = footColorOnly,
        };

        Assert.Equal(expected, boulder.FootholdMode);
    }

    [Fact]
    public async Task CreateBoulder_StoresFootColorRule()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);

        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)], footColorOnly: "yellow");

        await using var check = h.CreateContext();
        var saved = await check.Boulders.FirstAsync(b => b.Id == boulder.Id);

        Assert.Equal("yellow", saved.FootColorOnly);
        Assert.Equal(FootholdMode.DefinedOnly, saved.FootholdMode);
    }

    [Fact]
    public async Task UpdateBoulder_ClearsFootColorRule_OnEmptyString()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);

        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)], footColorOnly: "yellow");

        await h.BoulderService.UpdateBoulderAsync(boulder.Id, "B", null, footColorOnly: string.Empty);

        await using var check = h.CreateContext();
        var saved = await check.Boulders.FirstAsync(b => b.Id == boulder.Id);

        Assert.Null(saved.FootColorOnly);
        Assert.Equal(FootholdMode.AllKickboard, saved.FootholdMode);
    }
}
