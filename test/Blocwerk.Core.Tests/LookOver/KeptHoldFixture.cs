// <copyright file="KeptHoldFixture.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.HoldLookFixture;
using static Blocwerk.Core.Tests.LookOver.LookOverFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// The update of <see cref="LookOverFixture"/> with TWO holds it could not find: the target, which the boulder uses,
/// and one anchor no boulder uses, which optionally lies under a big new hold the update detected.
/// </summary>
internal static class KeptHoldFixture
{
    /// <summary>Where the anchor sits that the update does not find again.</summary>
    public static readonly (double X, double Y) Unused = Anchors[^1];

    /// <summary>A rectangular outline around a hold's centre.</summary>
    public static List<ShapePoint> Box(double halfWidth, double halfHeight) =>
    [
        new() { Dx = -halfWidth, Dy = -halfHeight }, new() { Dx = halfWidth, Dy = -halfHeight },
        new() { Dx = halfWidth, Dy = halfHeight }, new() { Dx = -halfWidth, Dy = halfHeight },
    ];

    /// <summary>Applies that update and returns the wall with what became of the two holds.</summary>
    public static async Task<KeptWall> ApplyAsync(WallTestHarness h, bool covered = false)
    {
        var w = await SeedAsync(h, targetDetected: false);
        Guid unusedOld;
        Guid? big = null;
        await using (var db = h.CreateContext())
        {
            var staged = await db.Holds.SingleAsync(s => s.Generation == 3 && Math.Abs(s.X - Unused.X) < Tolerance && Math.Abs(s.Y - Unused.Y) < Tolerance);
            unusedOld = (await db.Holds.SingleAsync(s => s.Generation == 2 && Math.Abs(s.X - Unused.X) < Tolerance && Math.Abs(s.Y - Unused.Y) < Tolerance)).Id;
            db.Holds.Remove(staged);
            var panel = w.StagedByPanel.Keys.Single();
            var ids = w.StagedByPanel[panel].Where(id => id != staged.Id).ToList();
            if (covered)
            {
                // A volume-sized hold beside the old one, whose outline reaches over it.
                var volume = new Hold
                {
                    WallId = w.WallId, WallPanelId = panel, X = Unused.X + 0.09, Y = Unused.Y, Radius = 0.12, IsAutoDetected = true, NeedsReview = true,
                    Generation = 3, ShapePoints = Box(0.12, 0.1),
                };
                db.Holds.Add(volume);
                ids.Add(volume.Id);
                big = volume.Id;
            }

            await db.SaveChangesAsync();
            w.StagedByPanel[panel] = ids;
        }

        w.Probe.Set(TargetX, TargetY, 0.2);
        w.Probe.Set(Unused.X, Unused.Y, 0.2);

        // The new photo shows something else where the volume is: a detection that looks like the old photo is left out.
        w.Probe.Set(Unused.X + 0.09, Unused.Y, 0.1);
        var session = await w.Service.ResumeAsync(w.WallId);
        await RecordAsync(w, session);
        await PromoteLikeApplyAsync(w, session);
        await using var after = h.CreateContext();
        var used = await after.BoulderHolds.Where(bh => bh.BoulderId == w.BoulderId).Select(bh => bh.HoldId).SingleAsync();
        var unused = await after.HoldGenerationLinks.Where(l => l.OldHoldId == unusedOld).Select(l => l.NewHoldId!.Value).SingleAsync();
        var found = await after.Holds.Where(x => x.Generation == 3 && Math.Abs(x.X - Anchors[0].X) < Tolerance && Math.Abs(x.Y - Anchors[0].Y) < Tolerance).Select(x => x.Id).SingleAsync();
        return new KeptWall(w, used, unused, unusedOld, big, found);
    }

    /// <summary>Puts the wall back to how the version before this one left it: no hold marked, a finding only for what a boulder uses or a new hold sits on.</summary>
    public static async Task ForgetKeptStateAsync(WallTestHarness h, KeptWall k)
    {
        await using var db = h.CreateContext();
        await db.Holds.Where(x => x.WallId == k.Wall.WallId).ExecuteUpdateAsync(s => s.SetProperty(x => x.KeptUnconfirmedGeneration, (int?)null));
        await db.Walls.Where(x => x.Id == k.Wall.WallId).ExecuteUpdateAsync(s => s.SetProperty(x => x.KeptHoldsGeneration, (int?)null));
        await db.WallLookOverFindings
            .Where(f => f.WallId == k.Wall.WallId && f.BouldersAtApply == 0 && f.OtherHoldId == null)
            .ExecuteDeleteAsync();
        await db.WallLookOverFindings.Where(f => f.WallId == k.Wall.WallId)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.HoldKept, false).SetProperty(f => f.Covered, false));
    }

    public static async Task<WallLookOverFinding> FindingOfAsync(WallTestHarness h, Guid holdId)
    {
        await using var db = h.CreateContext();
        return await db.WallLookOverFindings.AsNoTracking().SingleAsync(f => f.HoldId == holdId);
    }

    public static async Task<Hold> AddByHandAsync(WallTestHarness h, Guid wallId, Guid panelId, double x, double y, double radius = 0.03)
    {
        await using var db = h.CreateContext();
        var hold = new Hold { WallId = wallId, WallPanelId = panelId, X = x, Y = y, Radius = radius, IsAutoDetected = false, Generation = 3, Category = HoldCategory.Hand };
        db.Holds.Add(hold);
        await db.SaveChangesAsync();
        return hold;
    }
}
