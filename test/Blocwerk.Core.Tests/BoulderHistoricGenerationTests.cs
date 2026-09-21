using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Cover for the generation the boulder detail page offers as "Then". The rule is per hold: the
/// generation of its cross-generation ANCESTOR when lineage records one, else the hold's own
/// generation; the boulder's value is the MAX of those, and it is offered only while that is below
/// the wall's current generation. The ancestor hop is the whole point — a hold CARRIED through a
/// wall update is a fresh row at the CURRENT generation, so gating on the boulder's own hold
/// generations hid the toggle on exactly the boulders flagged because a hold changed.
/// </summary>
public class BoulderHistoricGenerationTests
{
    private const int CurrentGeneration = 3;

    [Fact]
    public async Task AllHoldsCarriedToCurrentGeneration_ReturnsTheAncestorGeneration()
    {
        // The production shape of "Batman" / "Gummy bear": every hold sits at gen 3 with an ancestor
        // at gen 2. Max(own) would be 3 and hide the toggle; the ancestor hop yields 2.
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: CurrentGeneration);
        var carried = await AddHoldsAsync(h, CurrentGeneration, count: 3);
        var ancestors = await AddHoldsAsync(h, 2, count: 3);
        for (var i = 0; i < carried.Count; i++)
        {
            await AddLineageAsync(h, ancestors[i], carried[i], 2, CurrentGeneration);
        }

        var boulderId = await AddBoulderAsync(h, carried);

        Assert.Equal(2, await h.BoulderService.GetHistoricGenerationAsync(boulderId));
    }

    [Fact]
    public async Task ChangedLineageKind_IsTreatedTheSameAsCarried()
    {
        // "Gummy bear" has one CHANGED hold among carried ones. The kind does not enter the rule:
        // what matters is that a predecessor exists at all.
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: CurrentGeneration);
        var carried = await AddHoldsAsync(h, CurrentGeneration, count: 2);
        var ancestors = await AddHoldsAsync(h, 2, count: 2);
        await AddLineageAsync(h, ancestors[0], carried[0], 2, CurrentGeneration, HoldGenerationLinkKind.Same);
        await AddLineageAsync(h, ancestors[1], carried[1], 2, CurrentGeneration, HoldGenerationLinkKind.Changed);

        var boulderId = await AddBoulderAsync(h, carried);

        Assert.Equal(2, await h.BoulderService.GetHistoricGenerationAsync(boulderId));
    }

    [Fact]
    public async Task HoldsFrozenBelowCurrentGeneration_WithNoAncestors_ReturnsTheirOwnGeneration()
    {
        // "Evening Wood" / "High Heels Corrected": the holds were left behind at an old generation
        // and no lineage row points at them. Their own generation is the answer.
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: CurrentGeneration);
        var frozen = await AddHoldsAsync(h, 0, count: 4);

        var boulderId = await AddBoulderAsync(h, frozen);

        Assert.Equal(0, await h.BoulderService.GetHistoricGenerationAsync(boulderId));
    }

    [Fact]
    public async Task HoldsSpanningTwoOldGenerations_ReturnsTheMaxOfThem()
    {
        // "I'll come up with a name": holds span gen 1..2, none carried. The newest state that still
        // predates today's wall is gen 2.
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: CurrentGeneration);
        var older = await AddHoldsAsync(h, 1, count: 3);
        var newer = await AddHoldsAsync(h, 2, count: 5);

        var boulderId = await AddBoulderAsync(h, [.. older, .. newer]);

        Assert.Equal(2, await h.BoulderService.GetHistoricGenerationAsync(boulderId));
    }

    [Fact]
    public async Task FullyCurrentWithNoAncestors_ReturnsNull()
    {
        // An ordinary live boulder on today's wall: nothing older to show, so no toggle.
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: CurrentGeneration);
        var current = await AddHoldsAsync(h, CurrentGeneration, count: 3);

        var boulderId = await AddBoulderAsync(h, current);

        Assert.Null(await h.BoulderService.GetHistoricGenerationAsync(boulderId));
    }

    [Fact]
    public async Task MixedCarriedAndFrozenHolds_TakesTheNewestPredecessorState()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: CurrentGeneration);
        var frozen = await AddHoldsAsync(h, 0, count: 2);
        var carried = await AddHoldsAsync(h, CurrentGeneration, count: 1);
        var ancestor = (await AddHoldsAsync(h, 2, count: 1))[0];
        await AddLineageAsync(h, ancestor, carried[0], 2, CurrentGeneration);

        var boulderId = await AddBoulderAsync(h, [.. frozen, .. carried]);

        Assert.Equal(2, await h.BoulderService.GetHistoricGenerationAsync(boulderId));
    }

    [Fact]
    public async Task AncestorOnlyOneGenerationBack_IsUsed_NotTheOrigin()
    {
        // Lineage reaches back to gen 0, but "Then" means the state before the change that flagged
        // the boulder — the immediate predecessor at gen 2, not the origin.
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: CurrentGeneration);
        var origin = (await AddHoldsAsync(h, 0, count: 1))[0];
        var middle = (await AddHoldsAsync(h, 2, count: 1))[0];
        var carried = (await AddHoldsAsync(h, CurrentGeneration, count: 1))[0];
        await AddLineageAsync(h, origin, middle, 0, 2);
        await AddLineageAsync(h, middle, carried, 2, CurrentGeneration);

        var boulderId = await AddBoulderAsync(h, [carried]);

        Assert.Equal(2, await h.BoulderService.GetHistoricGenerationAsync(boulderId));
    }

    [Fact]
    public async Task BoulderWithNoHolds_ReturnsNull()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: CurrentGeneration);

        var boulderId = await AddBoulderAsync(h, []);

        Assert.Null(await h.BoulderService.GetHistoricGenerationAsync(boulderId));
    }

    [Fact]
    public async Task AnonymousShareViewer_ResolvesItByToken()
    {
        // The share path must never resolve a viewer: there is nobody signed in behind the link, and
        // a gate that queried as the viewer silently hid the toggle for exactly these viewers.
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: CurrentGeneration);
        var carried = await AddHoldsAsync(h, CurrentGeneration, count: 2);
        var ancestors = await AddHoldsAsync(h, 2, count: 2);
        await AddLineageAsync(h, ancestors[0], carried[0], 2, CurrentGeneration);
        await AddLineageAsync(h, ancestors[1], carried[1], 2, CurrentGeneration);
        var boulderId = await AddBoulderAsync(h, carried);
        var token = await SetShareTokenAsync(h);
        h.CurrentUser.GetCurrentUserAsync().ThrowsAsync(new UnauthorizedAccessException());

        Assert.Equal(2, await h.BoulderService.GetHistoricGenerationAsync(boulderId, token));
    }

    [Fact]
    public async Task WrongShareToken_ResolvesNothing()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: CurrentGeneration);
        var frozen = await AddHoldsAsync(h, 0, count: 2);
        var boulderId = await AddBoulderAsync(h, frozen);
        await SetShareTokenAsync(h);
        h.CurrentUser.GetCurrentUserAsync().ThrowsAsync(new UnauthorizedAccessException());

        Assert.Null(await h.BoulderService.GetHistoricGenerationAsync(boulderId, "not-the-token"));
    }

    private static async Task<List<Guid>> AddHoldsAsync(WallTestHarness h, int generation, int count)
    {
        await using var db = h.CreateContext();
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var hold = new Hold
            {
                WallId = h.WallId,
                X = 0.01 * (i + 1),
                Y = 0.01 * (generation + 1),
                Radius = 0.02,
                Generation = generation,
            };
            db.Holds.Add(hold);
            ids.Add(hold.Id);
        }

        await db.SaveChangesAsync();
        return ids;
    }

    private static async Task AddLineageAsync(
        WallTestHarness h,
        Guid oldHoldId,
        Guid newHoldId,
        int from,
        int to,
        HoldGenerationLinkKind kind = HoldGenerationLinkKind.Same)
    {
        await using var db = h.CreateContext();
        db.HoldGenerationLinks.Add(new HoldGenerationLink
        {
            WallId = h.WallId,
            OldHoldId = oldHoldId,
            NewHoldId = newHoldId,
            Kind = kind,
            FromGeneration = from,
            ToGeneration = to,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> AddBoulderAsync(WallTestHarness h, IReadOnlyList<Guid> holdIds)
    {
        await using var db = h.CreateContext();
        var boulder = new Boulder
        {
            WallId = h.WallId,
            Name = "Flagged",
            CreatedByUserId = h.Owner.Id,
            Generation = 0,
        };
        db.Boulders.Add(boulder);
        foreach (var holdId in holdIds)
        {
            db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = holdId });
        }

        await db.SaveChangesAsync();
        return boulder.Id;
    }

    private static async Task<string> SetShareTokenAsync(WallTestHarness h)
    {
        const string token = "share-token-for-then-toggle";
        await using var db = h.CreateContext();
        var wall = await db.Walls.SingleAsync(w => w.Id == h.WallId);
        wall.ShareToken = token;
        await db.SaveChangesAsync();
        return token;
    }
}
