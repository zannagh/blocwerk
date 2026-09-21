using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Cover for translating a boulder's holds onto an older generation — what the detail page's "Then"
/// view draws. A hold carried through a wall update is a FRESH row at today's generation pointing at
/// today's panel, so without this hop the historic view matched none of the old generation's panels
/// and rendered an empty overlay on a boulder whose holds never physically moved.
/// </summary>
public class BoulderHoldsAtGenerationTests
{
    private const int CurrentGeneration = 3;

    [Fact]
    public async Task HoldCarriedSame_MapsBackToItsOldGenerationRow()
    {
        // The production shape: gen-3 rows linked Kind=Same back to their gen-2 predecessors.
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: CurrentGeneration);
        var carried = await AddHoldsAsync(h, CurrentGeneration, count: 3);
        var ancestors = await AddHoldsAsync(h, 2, count: 3);
        for (var i = 0; i < carried.Count; i++)
        {
            await AddLineageAsync(h, ancestors[i], carried[i], 2, CurrentGeneration);
        }

        var boulderId = await AddBoulderAsync(h, carried, HoldType.Start, HoldUsage.FootOnly);

        var mapped = await h.BoulderService.GetBoulderHoldsAtGenerationAsync(boulderId, 2);

        Assert.NotNull(mapped);
        Assert.Equal(ancestors.OrderBy(id => id), mapped!.Select(m => m.HoldId).OrderBy(id => id));
        Assert.All(mapped, m => Assert.Equal(2, m.Hold.Generation));

        // The boulder's own marks belong to the boulder, not to any generation of the wall.
        Assert.All(mapped, m => Assert.Equal(HoldType.Start, m.Type));
        Assert.All(mapped, m => Assert.Equal(HoldUsage.FootOnly, m.Usage));
    }

    [Fact]
    public async Task HoldMarkedChanged_MapsBackJustTheSame()
    {
        // Kind does not enter the rule: a Changed hold still HAS a predecessor row to draw.
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: CurrentGeneration);
        var carried = (await AddHoldsAsync(h, CurrentGeneration, count: 1))[0];
        var ancestor = (await AddHoldsAsync(h, 2, count: 1))[0];
        await AddLineageAsync(h, ancestor, carried, 2, CurrentGeneration, HoldGenerationLinkKind.Changed);

        var boulderId = await AddBoulderAsync(h, [carried]);

        var mapped = await h.BoulderService.GetBoulderHoldsAtGenerationAsync(boulderId, 2);

        Assert.NotNull(mapped);
        Assert.Equal(ancestor, Assert.Single(mapped!).HoldId);
    }

    [Fact]
    public async Task HoldWithNoAncestorChain_DropsOutWithoutLosingTheRest()
    {
        // A hold added after the old generation did not exist then; it must vanish from the historic
        // overlay while its carried siblings still render.
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: CurrentGeneration);
        var carried = (await AddHoldsAsync(h, CurrentGeneration, count: 1))[0];
        var ancestor = (await AddHoldsAsync(h, 2, count: 1))[0];
        await AddLineageAsync(h, ancestor, carried, 2, CurrentGeneration);
        var orphan = (await AddHoldsAsync(h, CurrentGeneration, count: 1))[0];

        var boulderId = await AddBoulderAsync(h, [carried, orphan]);

        var mapped = await h.BoulderService.GetBoulderHoldsAtGenerationAsync(boulderId, 2);

        Assert.NotNull(mapped);
        Assert.Equal(ancestor, Assert.Single(mapped!).HoldId);
    }

    [Fact]
    public async Task HoldsAlreadyAtTheTargetGeneration_MapToThemselves()
    {
        // A frozen boulder: its rows ARE the old generation's, so it needs no lineage at all.
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: CurrentGeneration);
        var frozen = await AddHoldsAsync(h, 2, count: 4);

        var boulderId = await AddBoulderAsync(h, frozen);

        var mapped = await h.BoulderService.GetBoulderHoldsAtGenerationAsync(boulderId, 2);

        Assert.NotNull(mapped);
        Assert.Equal(frozen.OrderBy(id => id), mapped!.Select(m => m.HoldId).OrderBy(id => id));
    }

    [Fact]
    public async Task ChainedLineage_WalksBackSeveralGenerations()
    {
        // gen 3 → gen 2 → gen 1: asking for gen 1 must follow the whole chain, not stop at the
        // immediate predecessor.
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: CurrentGeneration);
        var origin = (await AddHoldsAsync(h, 1, count: 1))[0];
        var middle = (await AddHoldsAsync(h, 2, count: 1))[0];
        var carried = (await AddHoldsAsync(h, CurrentGeneration, count: 1))[0];
        await AddLineageAsync(h, origin, middle, 1, 2);
        await AddLineageAsync(h, middle, carried, 2, CurrentGeneration);

        var boulderId = await AddBoulderAsync(h, [carried]);

        var mapped = await h.BoulderService.GetBoulderHoldsAtGenerationAsync(boulderId, 1);

        Assert.NotNull(mapped);
        Assert.Equal(origin, Assert.Single(mapped!).HoldId);
    }

    [Fact]
    public async Task NothingMapped_IsAnEmptyList_NotNull()
    {
        // The caller must be able to tell "read fine, nothing existed back then" (render the current
        // view instead) from "could not read this boulder at all".
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: CurrentGeneration);
        var orphans = await AddHoldsAsync(h, CurrentGeneration, count: 2);

        var boulderId = await AddBoulderAsync(h, orphans);

        var mapped = await h.BoulderService.GetBoulderHoldsAtGenerationAsync(boulderId, 2);

        Assert.NotNull(mapped);
        Assert.Empty(mapped!);
    }

    [Fact]
    public async Task UnreadableBoulder_IsNull()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: CurrentGeneration);

        Assert.Null(await h.BoulderService.GetBoulderHoldsAtGenerationAsync(Guid.NewGuid(), 2));
    }

    [Fact]
    public async Task AnonymousShareViewer_MapsByToken_AndAWrongTokenIsNull()
    {
        // Same gate as the historic-generation read: the share path never resolves a viewer.
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: CurrentGeneration);
        var carried = (await AddHoldsAsync(h, CurrentGeneration, count: 1))[0];
        var ancestor = (await AddHoldsAsync(h, 2, count: 1))[0];
        await AddLineageAsync(h, ancestor, carried, 2, CurrentGeneration);
        var boulderId = await AddBoulderAsync(h, [carried]);
        var token = await SetShareTokenAsync(h);
        h.CurrentUser.GetCurrentUserAsync().ThrowsAsync(new UnauthorizedAccessException());

        var mapped = await h.BoulderService.GetBoulderHoldsAtGenerationAsync(boulderId, 2, token);

        Assert.NotNull(mapped);
        Assert.Equal(ancestor, Assert.Single(mapped!).HoldId);
        Assert.Null(await h.BoulderService.GetBoulderHoldsAtGenerationAsync(boulderId, 2, "not-the-token"));
    }

    [Fact]
    public async Task DraftBoulder_IsNotReadableThroughTheShareToken()
    {
        // The share path must carry the SAME draft gate as GetBoulderByShareTokenAsync. Without it a
        // share-token holder who guessed an unpublished boulder's id could read its hold geometry
        // through this read — the one way around the gate.
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: CurrentGeneration);
        var holds = await AddHoldsAsync(h, 2, count: 2);
        var draftId = await AddBoulderAsync(h, holds, isDraft: true);
        var publishedId = await AddBoulderAsync(h, holds);
        var token = await SetShareTokenAsync(h);
        h.CurrentUser.GetCurrentUserAsync().ThrowsAsync(new UnauthorizedAccessException());

        Assert.Null(await h.BoulderService.GetBoulderHoldsAtGenerationAsync(draftId, 2, token));

        // The control: same wall, same token, same holds — only the draft flag differs.
        var published = await h.BoulderService.GetBoulderHoldsAtGenerationAsync(publishedId, 2, token);
        Assert.NotNull(published);
        Assert.Equal(2, published!.Count);
    }

    [Fact]
    public async Task TwoAncestorsAtDifferentGenerations_ResolveTheSameWhicheverOrderTheLinksAreStored()
    {
        // A row recording TWO predecessors: only one backward step can be taken, so the pick must
        // come from the documented tie-break (the LATEST link — the closest predecessor in time) and
        // not from the order the link rows happen to come back in. Both shortcuts reach generation 1
        // here, but they land on DIFFERENT holds, so an order-dependent pick moves the boulder
        // between two identical reads.
        Assert.True(await ResolveNearestAncestorFoundAsync(reverseLinkOrder: false));
        Assert.True(await ResolveNearestAncestorFoundAsync(reverseLinkOrder: true));
    }

    [Fact]
    public async Task TwoAncestorsAtOneGeneration_ResolveToTheDocumentedTieBreak()
    {
        // Same generation on both links, so the second tie-break decides: the smallest predecessor
        // id — arbitrary, but the same on every read, machine and provider.
        var forward = await ResolveMergedAncestorAsync(reverseLinkOrder: false);
        var reversed = await ResolveMergedAncestorAsync(reverseLinkOrder: true);

        Assert.Equal(forward.Expected, forward.Resolved);
        Assert.Equal(reversed.Expected, reversed.Resolved);
    }

    private static async Task<bool> ResolveNearestAncestorFoundAsync(bool reverseLinkOrder)
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: CurrentGeneration);
        var carried = (await AddHoldsAsync(h, CurrentGeneration, count: 1))[0];
        var middle = (await AddHoldsAsync(h, 2, count: 1))[0];
        var origin = (await AddHoldsAsync(h, 1, count: 1))[0];
        var shortcut = (await AddHoldsAsync(h, 1, count: 1))[0];

        // The real chain (gen 3 -> gen 2 -> gen 1) and a second gen-1 link recorded straight onto the
        // gen-3 row. Both end at generation 1; the step-by-step chain is the one that must win.
        await AddLineageAsync(h, origin, middle, 1, 2);
        var links = new[] { (Ancestor: middle, From: 2), (Ancestor: shortcut, From: 1) };
        foreach (var (ancestor, from) in reverseLinkOrder ? Enumerable.Reverse(links) : links)
        {
            await AddLineageAsync(h, ancestor, carried, from, CurrentGeneration);
        }

        var boulderId = await AddBoulderAsync(h, [carried]);

        var mapped = await h.BoulderService.GetBoulderHoldsAtGenerationAsync(boulderId, 1);

        Assert.NotNull(mapped);
        return mapped!.Count == 1 && mapped[0].HoldId == origin;
    }

    [Fact]
    public async Task TwoBoulderHoldsOnOneAncestor_ResolveTheSameWhicheverOrderTheMarksArrive()
    {
        // The other direction: the boulder uses two of today's holds that both descend from ONE row
        // at the target generation. The mark drawn on that row must be the most prominent one either
        // way round, never "whichever the provider listed first".
        var forward = await ResolveConvergedMarkAsync(topFirst: true);
        var reversed = await ResolveConvergedMarkAsync(topFirst: false);

        Assert.Equal(HoldType.Top, forward);
        Assert.Equal(HoldType.Top, reversed);
    }

    [Fact]
    public async Task ConvergedHolds_ReportBothOfTheBouldersHoldsAsTheirSource()
    {
        // The counting half of the convergence: two of today's holds descend from ONE gen-2 row, so
        // the single row that comes back must name BOTH as its sources. Without that the detail page
        // can only subtract set sizes, and reports "1 hold has no match" on a boulder whose every
        // hold matched.
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: CurrentGeneration);
        var carried = await AddHoldsAsync(h, CurrentGeneration, count: 2);
        var ancestor = (await AddHoldsAsync(h, 2, count: 1))[0];
        await AddLineageAsync(h, ancestor, carried[0], 2, CurrentGeneration);
        await AddLineageAsync(h, ancestor, carried[1], 2, CurrentGeneration);

        var boulderId = await AddBoulderAsync(h, carried);

        var mapped = await h.BoulderService.GetBoulderHoldsAtGenerationAsync(boulderId, 2);

        Assert.NotNull(mapped);
        var row = Assert.Single(mapped!);
        Assert.Equal(ancestor, row.HoldId);
        Assert.Equal(carried.OrderBy(id => id), row.SourceHoldIds.OrderBy(id => id));
        Assert.Equal(0, BoulderGenerationView.UntranslatedHoldCount(carried, mapped!));
    }

    private static async Task<(Guid Expected, Guid Resolved)> ResolveMergedAncestorAsync(bool reverseLinkOrder)
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: CurrentGeneration);
        var carried = (await AddHoldsAsync(h, CurrentGeneration, count: 1))[0];
        var ancestors = await AddHoldsAsync(h, 2, count: 2);

        var order = reverseLinkOrder ? Enumerable.Reverse(ancestors).ToList() : ancestors;
        foreach (var ancestor in order)
        {
            await AddLineageAsync(h, ancestor, carried, 2, CurrentGeneration);
        }

        var boulderId = await AddBoulderAsync(h, [carried]);

        var mapped = await h.BoulderService.GetBoulderHoldsAtGenerationAsync(boulderId, 2);

        Assert.NotNull(mapped);
        return (ancestors.Min(), Assert.Single(mapped!).HoldId);
    }

    private static async Task<HoldType> ResolveConvergedMarkAsync(bool topFirst)
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: CurrentGeneration);
        var carried = await AddHoldsAsync(h, CurrentGeneration, count: 2);
        var ancestor = (await AddHoldsAsync(h, 2, count: 1))[0];
        await AddLineageAsync(h, ancestor, carried[0], 2, CurrentGeneration);
        await AddLineageAsync(h, ancestor, carried[1], 2, CurrentGeneration);

        await using var db = h.CreateContext();
        var boulder = new Boulder
        {
            WallId = h.WallId,
            Name = "Converged",
            CreatedByUserId = h.Owner.Id,
            Generation = 0,
        };
        db.Boulders.Add(boulder);
        var marks = topFirst
            ? new[] { (carried[0], HoldType.Top), (carried[1], HoldType.Normal) }
            : [(carried[0], HoldType.Normal), (carried[1], HoldType.Top)];
        foreach (var (holdId, type) in marks)
        {
            db.BoulderHolds.Add(new BoulderHold
            {
                BoulderId = boulder.Id,
                HoldId = holdId,
                Type = type,
                Usage = HoldUsage.HandAndFoot,
            });
        }

        await db.SaveChangesAsync();

        var mapped = await h.BoulderService.GetBoulderHoldsAtGenerationAsync(boulder.Id, 2);

        Assert.NotNull(mapped);
        return Assert.Single(mapped!).Type;
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

    private static async Task<Guid> AddBoulderAsync(
        WallTestHarness h,
        IReadOnlyList<Guid> holdIds,
        HoldType type = HoldType.Normal,
        HoldUsage usage = HoldUsage.HandAndFoot,
        bool isDraft = false)
    {
        await using var db = h.CreateContext();
        var boulder = new Boulder
        {
            WallId = h.WallId,
            Name = "Carried",
            CreatedByUserId = h.Owner.Id,
            Generation = 0,
            IsDraft = isDraft,
        };
        db.Boulders.Add(boulder);
        foreach (var holdId in holdIds)
        {
            db.BoulderHolds.Add(new BoulderHold
            {
                BoulderId = boulder.Id,
                HoldId = holdId,
                Type = type,
                Usage = usage,
            });
        }

        await db.SaveChangesAsync();
        return boulder.Id;
    }

    private static async Task<string> SetShareTokenAsync(WallTestHarness h)
    {
        const string token = "share-token-for-historic-holds";
        await using var db = h.CreateContext();
        var wall = await db.Walls.SingleAsync(w => w.Id == h.WallId);
        wall.ShareToken = token;
        await db.SaveChangesAsync();
        return token;
    }
}
