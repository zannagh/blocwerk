// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>Seeds and reads the hold-to-hold facts the merge and split tools must carry: lineage, panel links, stacks.</summary>
internal static class HoldToolSeed
{
    /// <summary>A hold of an earlier generation (not live), as a lineage predecessor.</summary>
    public static async Task<Guid> OldHoldAsync(WallTestHarness h, double x, double y, Guid? panelId = null)
    {
        await using var db = h.CreateContext();
        var hold = new Hold { WallId = h.WallId, WallPanelId = panelId, X = x, Y = y, Radius = 0.02, Generation = 0 };
        db.Holds.Add(hold);
        await db.SaveChangesAsync();
        return hold.Id;
    }

    /// <summary>Records "<paramref name="oldId"/> became <paramref name="newId"/>".</summary>
    public static async Task LineageAsync(WallTestHarness h, Guid oldId, Guid newId)
    {
        await using var db = h.CreateContext();
        db.HoldGenerationLinks.Add(new HoldGenerationLink { WallId = h.WallId, OldHoldId = oldId, NewHoldId = newId, FromGeneration = 0, ToGeneration = 1 });
        await db.SaveChangesAsync();
    }

    /// <summary>Links two holds as the same physical hold on two panels.</summary>
    public static async Task PanelLinkAsync(WallTestHarness h, Guid a, Guid b)
    {
        await using var db = h.CreateContext();
        db.HoldLinks.Add(new HoldLink { WallId = h.WallId, HoldAId = a, HoldBId = b });
        await db.SaveChangesAsync();
    }

    /// <summary>Puts <paramref name="upper"/> on <paramref name="lower"/>.</summary>
    public static async Task StackAsync(WallTestHarness h, Guid upper, Guid lower)
    {
        await using var db = h.CreateContext();
        (await db.Holds.FirstAsync(x => x.Id == upper)).StackedOnHoldId = lower;
        await db.SaveChangesAsync();
    }

    /// <summary>The lineage rows as (old, new) pairs.</summary>
    public static async Task<List<(Guid? Old, Guid? New)>> LineageRowsAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return [.. (await db.HoldGenerationLinks.AsNoTracking().ToListAsync()).Select(l => (l.OldHoldId, l.NewHoldId))];
    }

    /// <summary>The panel links as unordered pairs.</summary>
    public static async Task<List<HashSet<Guid>>> PanelLinksAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return [.. (await db.HoldLinks.AsNoTracking().ToListAsync()).Select(l => new HashSet<Guid> { l.HoldAId, l.HoldBId })];
    }

    /// <summary>An octagon outline around the centre, for holds that need a traced shape.</summary>
    public static List<ShapePoint> Octagon(double radius) => ShapePoint.DefaultOctagon(radius);
}
