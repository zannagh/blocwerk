// <copyright file="NewHoldEdgeScenarios.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.LookOver.LookOverFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// The edges of a mixed "use the new hold" (the first boulder yes, the second no): frozen records on the old hold, a
/// boulder on both holds, what is stacked on or linked to the old hold, and a boulder that joins after the card was read.
/// </summary>
internal static class NewHoldEdgeScenarios
{
    /// <summary>An archived and a historic boulder on the old hold keep their hold: it moves to the new hold with them.</summary>
    public static async Task FrozenBouldersMoveAsync(WallTestHarness h)
    {
        var (w, second, view) = await NewHoldPerBoulderScenarios.TwoBouldersAsync(h);
        var archived = await AddBoulderAsync(h, w.WallId, view.HoldId!.Value, b => b.IsArchived = true);
        var historic = await AddBoulderAsync(h, w.WallId, view.HoldId!.Value, b => b.IsHistoric = true);

        await Service(h).AnswerAsync(w.WallId, view.Id, LookOverAnswer.UseNewHold, [w.BoulderId, second], [w.BoulderId]);

        foreach (var id in new[] { archived, historic })
        {
            Assert.Equal(w.DetectionId, (await BoulderAsync(h, id)).BoulderHolds.Single().HoldId);
        }

        Assert.Empty((await BoulderAsync(h, second)).BoulderHolds);
        Assert.False((await BoulderAsync(h, archived)).IsHistoric);
    }

    /// <summary>A yes boulder already on the new hold keeps one membership with the stronger role, and is marked for it.</summary>
    public static async Task BoulderOnBothHoldsAsync(WallTestHarness h)
    {
        var (w, second, view) = await NewHoldPerBoulderScenarios.TwoBouldersAsync(h);
        await using (var db = h.CreateContext())
        {
            db.BoulderHolds.Add(new BoulderHold { BoulderId = w.BoulderId, HoldId = w.DetectionId!.Value, Type = HoldType.Top });
            await db.SaveChangesAsync();
        }

        await Service(h).AnswerAsync(w.WallId, view.Id, LookOverAnswer.UseNewHold, [w.BoulderId, second], [w.BoulderId]);

        var yes = await BoulderAsync(h, w.BoulderId);
        var membership = Assert.Single(yes.BoulderHolds);
        Assert.Equal((w.DetectionId, HoldType.Top, true, false), (membership.HoldId, membership.Type, yes.NeedsReview, yes.IsHistoric));
        Assert.Empty((await FindingAsync(h, w.WallId)).ClearedBoulderIds!);
        Assert.True((await BoulderAsync(h, second)).IsHistoric);
    }

    /// <summary>A hold stacked on the old hold sits on the new one; the old hold's panel link goes with it. Undo puts both back.</summary>
    public static async Task StackedAndLinkedAsync(WallTestHarness h)
    {
        var (w, second, view) = await NewHoldPerBoulderScenarios.TwoBouldersAsync(h);
        Guid upper, linked;
        await using (var db = h.CreateContext())
        {
            var old = await db.Holds.SingleAsync(x => x.Id == view.HoldId);
            var top = new Hold { WallId = w.WallId, WallPanelId = old.WallPanelId, X = old.X, Y = old.Y, Radius = 0.01, Generation = old.Generation, StackedOnHoldId = old.Id };
            var twin = new Hold { WallId = w.WallId, X = 0.9, Y = 0.9, Radius = 0.02, Generation = old.Generation };
            db.Holds.AddRange(top, twin);
            db.HoldLinks.Add(new HoldLink { WallId = w.WallId, HoldAId = old.Id, HoldBId = twin.Id });
            await db.SaveChangesAsync();
            (upper, linked) = (top.Id, twin.Id);
        }

        await Service(h).AnswerAsync(w.WallId, view.Id, LookOverAnswer.UseNewHold, [w.BoulderId, second], [w.BoulderId]);

        Assert.Equal(w.DetectionId, (await HoldAsync(h, upper))!.StackedOnHoldId);
        await using (var db = h.CreateContext())
        {
            Assert.False(await db.HoldLinks.AnyAsync(l => l.HoldAId == view.HoldId || l.HoldBId == view.HoldId));
        }

        var result = await Duplicates(h).RevertAsync(w.WallId, (await FindingAsync(h, w.WallId)).MergeBatchId!.Value);
        Assert.True(result.Reverted, result.Error);
        Assert.Equal(view.HoldId, (await HoldAsync(h, upper))!.StackedOnHoldId);
        await using var after = h.CreateContext();
        Assert.True(await after.HoldLinks.AnyAsync(l => l.HoldAId == view.HoldId && l.HoldBId == linked));
    }

    /// <summary>
    /// A boulder that joins the old hold after the answer's first check and before the lock (the card as read, then a
    /// late boulder) is refused under the lock: it would otherwise be retired unasked. Frozen boulders do not count,
    /// and always move.
    /// </summary>
    public static async Task JoinedUnderTheLockAsync(WallTestHarness h)
    {
        var (w, _, view) = await NewHoldPerBoulderScenarios.TwoBouldersAsync(h);
        var archived = await AddBoulderAsync(h, w.WallId, view.HoldId!.Value, b => b.IsArchived = true);
        var oldId = view.HoldId!.Value;
        await using (var db = h.CreateContext())
        {
            var moving = await WallLookOverService.MovingUnderLockAsync(db, oldId, view, new HashSet<Guid> { w.BoulderId }, default);
            Assert.Equal(new[] { w.BoulderId, archived }.Order(), moving!.Order());
        }

        var late = await AddBoulderAsync(h, w.WallId, oldId, _ => { });
        await using var after = h.CreateContext();
        var refused = await Assert.ThrowsAsync<UserFacingException>(
            () => WallLookOverService.MovingUnderLockAsync(after, oldId, view, new HashSet<Guid> { w.BoulderId }, default));

        Assert.Equal(WallLookOverService.BouldersChanged, refused.Message);
        Assert.Equal((false, 1), ((await BoulderAsync(h, late)).IsHistoric, (await BoulderAsync(h, late)).BoulderHolds.Count));
    }

    private static async Task<Guid> AddBoulderAsync(WallTestHarness h, Guid wallId, Guid holdId, Action<Boulder> shape)
    {
        await using var db = h.CreateContext();
        var boulder = new Boulder { WallId = wallId, Name = $"Extra {Guid.NewGuid():N}", CreatedByUserId = h.Owner.Id, Generation = 3 };
        shape(boulder);
        db.Boulders.Add(boulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = holdId });
        await db.SaveChangesAsync();
        return boulder.Id;
    }
}
