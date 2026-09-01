using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Covers the wall tool that answers "is this hold currently used by a boulder?".
/// </summary>
public class HoldUsageTests
{
    [Fact]
    public async Task GetHoldUsage_ReportsUsedHolds_AndOmitsFreeOnes()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 4);

        await h.BoulderService.CreateBoulderAsync(
            h.WallId,
            "Alpha",
            "6A",
            [new BoulderHoldInput(holds[0].Id, HoldType.Start), new BoulderHoldInput(holds[1].Id, HoldType.Top)]);

        // A dedicated foothold only survives on a boulder whose hands do not follow feet.
        await h.BoulderService.CreateBoulderAsync(
            h.WallId,
            "Beta",
            "7A",
            [new BoulderHoldInput(holds[1].Id, HoldType.Normal, HoldUsage.FootOnly)],
            handsFollowFeet: false);

        var usage = await h.BoulderService.GetHoldUsageAsync(h.WallId);

        Assert.Equal(2, usage.Count);
        Assert.Equal("Alpha", Assert.Single(usage[holds[0].Id]).Name);

        // The shared hold reports both boulders, with each one's own type and usage.
        var shared = usage[holds[1].Id];
        Assert.Equal(2, shared.Count);
        Assert.Contains(shared, r => r.Name == "Alpha" && r.Type == HoldType.Top);
        Assert.Contains(shared, r => r.Name == "Beta" && r.Usage == HoldUsage.FootOnly && r.Grade == "7A");

        // Untouched holds are simply absent, which is what the "free" count keys off.
        Assert.False(usage.ContainsKey(holds[2].Id));
        Assert.False(usage.ContainsKey(holds[3].Id));
    }

    [Fact]
    public async Task GetHoldUsage_ShowsDraftsToAllMembers()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 3);

        await h.BoulderService.CreateBoulderAsync(
            h.WallId, "My draft", null, [new BoulderHoldInput(holds[0].Id)], isDraft: true);

        var mine = await h.BoulderService.GetHoldUsageAsync(h.WallId);
        Assert.True(mine.ContainsKey(holds[0].Id));
        Assert.True(Assert.Single(mine[holds[0].Id]).IsDraft);

        var other = new User { Identifier = "other@test", DisplayName = "Other" };
        await using (var db = h.CreateContext())
        {
            db.Users.Add(other);
            db.WallMembers.Add(new WallMember { WallId = h.WallId, UserId = other.Id, Role = WallRole.Member });
            await db.SaveChangesAsync();
        }

        h.CurrentUser.GetCurrentUserAsync().Returns(_ => Task.FromResult(other));
        var theirs = await h.BoulderService.GetHoldUsageAsync(h.WallId);

        // Drafts are visible to every wall member now, so the hold shows as spoken-for (still
        // flagged as a draft) to a second member.
        Assert.True(theirs.ContainsKey(holds[0].Id));
        Assert.True(Assert.Single(theirs[holds[0].Id]).IsDraft);
    }

    [Fact]
    public async Task GetHoldUsage_IncludesHistoricBoulders_ButNotArchivedOnes()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 3);

        var historic = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Historic", null, [new BoulderHoldInput(holds[0].Id)]);
        var archived = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Archived", null, [new BoulderHoldInput(holds[1].Id)]);

        await using (var db = h.CreateContext())
        {
            (await db.Boulders.FirstAsync(b => b.Id == historic.Id)).IsHistoric = true;
            (await db.Boulders.FirstAsync(b => b.Id == archived.Id)).IsArchived = true;
            await db.SaveChangesAsync();
        }

        var usage = await h.BoulderService.GetHoldUsageAsync(h.WallId);

        // A historic boulder still pins its holds against pruning, so it must show up.
        Assert.True(Assert.Single(usage[holds[0].Id]).IsHistoric);
        Assert.False(usage.ContainsKey(holds[1].Id));
    }

    [Fact]
    public async Task GetHoldUsage_IsScopedToTheRequestedWall()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);
        await h.BoulderService.CreateBoulderAsync(h.WallId, "A", null, [new BoulderHoldInput(holds[0].Id)]);

        Guid otherWallId;
        await using (var db = h.CreateContext())
        {
            var otherWall = new Wall { Name = "Other", OwnerId = h.Owner.Id };
            db.Walls.Add(otherWall);
            db.WallMembers.Add(new WallMember { WallId = otherWall.Id, UserId = h.Owner.Id, Role = WallRole.Admin });
            await db.SaveChangesAsync();
            otherWallId = otherWall.Id;
        }

        Assert.Empty(await h.BoulderService.GetHoldUsageAsync(otherWallId));
        Assert.Single(await h.BoulderService.GetHoldUsageAsync(h.WallId));
    }

    [Fact]
    public async Task GetHoldUsage_OneSidedBoulder_ReportsOnBothLinkedTwins()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 3);
        await LinkHoldsAsync(h, holds[0].Id, holds[1].Id);

        // The boulder saves only the panel-1 twin (holds[0]); holds[1] is its un-saved twin.
        await h.BoulderService.CreateBoulderAsync(
            h.WallId, "OneSided", "6B", [new BoulderHoldInput(holds[0].Id, HoldType.Start)]);

        var usage = await h.BoulderService.GetHoldUsageAsync(h.WallId);

        // Both twins report the boulder, carrying the saved twin's resolved Type/Usage.
        Assert.Equal("OneSided", Assert.Single(usage[holds[0].Id]).Name);
        var twin = Assert.Single(usage[holds[1].Id]);
        Assert.Equal("OneSided", twin.Name);
        Assert.Equal(HoldType.Start, twin.Type);

        // The unlinked third hold is untouched.
        Assert.False(usage.ContainsKey(holds[2].Id));
    }

    [Fact]
    public async Task GetHoldUsage_BothSidedBoulder_CountedOncePerPhysicalHold_MostProminentType()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 3);
        await LinkHoldsAsync(h, holds[0].Id, holds[1].Id);

        // A legacy both-sided boulder: both twins saved as separate rows with conflicting types.
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "BothSided", "7A", [new BoulderHoldInput(holds[0].Id, HoldType.Normal)]);
        await using (var db = h.CreateContext())
        {
            db.Set<BoulderHold>().Add(new BoulderHold
            {
                BoulderId = boulder.Id,
                HoldId = holds[1].Id,
                Type = HoldType.Top,
                Usage = HoldUsage.HandAndFoot,
            });
            await db.SaveChangesAsync();
        }

        var usage = await h.BoulderService.GetHoldUsageAsync(h.WallId);

        // The boulder appears once under each twin (never twice under one), with Type resolved to the
        // most prominent of the two rows (Top).
        var first = Assert.Single(usage[holds[0].Id]);
        var second = Assert.Single(usage[holds[1].Id]);
        Assert.Equal("BothSided", first.Name);
        Assert.Equal(HoldType.Top, first.Type);
        Assert.Equal("BothSided", second.Name);
        Assert.Equal(HoldType.Top, second.Type);
    }

    private static async Task LinkHoldsAsync(WallTestHarness h, Guid holdAId, Guid holdBId)
    {
        await using var db = h.CreateContext();
        db.Set<HoldLink>().Add(new HoldLink
        {
            WallId = h.WallId,
            HoldAId = holdAId,
            HoldBId = holdBId,
        });
        await db.SaveChangesAsync();
    }
}
