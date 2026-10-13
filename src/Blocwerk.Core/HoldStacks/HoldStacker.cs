// <copyright file="HoldStacker.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.HoldStacks;

/// <summary>
/// Writes "this hold sits on that hold" (<see cref="Hold.StackedOnHoldId"/>) wherever holds are created, carried into a
/// new generation, merged or re-stacked by hand. Nothing here saves: the caller's save commits it with the rest.
/// </summary>
internal static class HoldStacker
{
    /// <summary>Two holds are on the same photo: the same panel row, or (without panels) the same generation.</summary>
    /// <param name="a">One hold.</param>
    /// <param name="b">The other.</param>
    /// <returns>True when one can sit on the other.</returns>
    public static bool SamePhoto(Hold a, Hold b) =>
        a.WallId == b.WallId && a.WallPanelId == b.WallPanelId && (a.WallPanelId is not null || a.Generation == b.Generation);

    /// <summary>
    /// Stacks holds that were just created (saved or only added to the context) by the rule of
    /// <see cref="HoldStacking"/>: each on the smallest hold it sits on, and existing holds without a base on a fresh
    /// hold they sit on.
    /// </summary>
    /// <param name="db">The context the holds live in.</param>
    /// <param name="fresh">The new holds.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many holds got a base.</returns>
    public static async Task<int> StackNewAsync(BlocwerkDbContext db, IReadOnlyCollection<Hold> fresh, CancellationToken ct = default)
    {
        // A detection the enrichment dropped (on a marker) and a hold deleted in this same save are not there.
        bool Gone(Hold hold) => db.Entry(hold).State is EntityState.Deleted or EntityState.Detached;
        var stacked = 0;
        foreach (var photo in fresh.Where(h => HoldStacking.CanStackAutomatically(h) && !Gone(h)).GroupBy(h => (h.WallId, h.WallPanelId, Generation: h.WallPanelId is null ? h.Generation : 0)))
        {
            var first = photo.First();
            var stored = await db.Holds.Where(h => h.WallId == first.WallId && h.WallPanelId == first.WallPanelId).ToListAsync(ct);
            var holds = stored.Concat(photo).Where(h => SamePhoto(h, first) && !Gone(h)).DistinctBy(h => h.Id).ToList();
            stacked += Apply(HoldStacking.Plan(holds, photo.Select(h => h.Id).ToHashSet(), await SizeAsync(db, first.WallPanelId, ct)));
        }

        return stacked;
    }

    /// <summary>Stacks every hold of some photos that has no base yet (the one-time backfill, and tests).</summary>
    /// <param name="db">The context (for the photos' sizes).</param>
    /// <param name="holds">Tracked holds, of any number of photos.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many holds got a base.</returns>
    public static async Task<int> StackAllAsync(BlocwerkDbContext db, IReadOnlyCollection<Hold> holds, CancellationToken ct = default)
    {
        var stacked = 0;
        foreach (var photo in holds.GroupBy(h => (h.WallPanelId, Generation: h.WallPanelId is null ? h.Generation : 0)))
        {
            stacked += Apply(HoldStacking.Plan([.. photo], null, await SizeAsync(db, photo.Key.WallPanelId, ct)));
        }

        return stacked;
    }

    /// <summary>
    /// A wall update carried old holds into a new generation: a successor sits on the successor of what its old hold
    /// sat on. Without such a successor on the same photo, a copy of the old hold sits on nothing, and a detection
    /// keeps what the detections were stacked on among themselves.
    /// </summary>
    /// <param name="successors">Old hold to the new-generation hold that carries it on (a copy, or a detection).</param>
    public static void CarryForward(IReadOnlyCollection<(Hold Old, Hold Successor)> successors)
    {
        var byOld = successors.GroupBy(s => s.Old.Id).ToDictionary(g => g.Key, g => g.First().Successor);
        var on = successors.Select(s => s.Successor).DistinctBy(h => h.Id).ToDictionary(h => h.Id, h => h.StackedOnHoldId);
        foreach (var (old, successor) in successors)
        {
            var carried = old.StackedOnHoldId is { } oldBase && byOld.TryGetValue(oldBase, out var lower) && SamePhoto(successor, lower)
                && !HoldStackOrder.WouldLoop(on, successor.Id, lower.Id)
                    ? lower.Id
                    : (Guid?)null;
            if (carried is not null || successor.StackedOnHoldId == old.StackedOnHoldId)
            {
                on[successor.Id] = successor.StackedOnHoldId = carried;
            }
        }
    }

    /// <summary>
    /// Two holds become one: what sat on the removed hold now sits on the kept one, and a kept hold that sat on the
    /// removed one takes that hold's base.
    /// </summary>
    /// <param name="db">The context.</param>
    /// <param name="kept">The hold that stays.</param>
    /// <param name="removed">The hold that is deleted (by the caller, after this).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task MergeAsync(BlocwerkDbContext db, Hold kept, Hold removed, CancellationToken ct = default)
    {
        if (kept.StackedOnHoldId == removed.Id)
        {
            kept.StackedOnHoldId = removed.StackedOnHoldId == kept.Id ? null : removed.StackedOnHoldId;
        }

        var moved = await db.Holds.Where(h => h.StackedOnHoldId == removed.Id && h.Id != kept.Id).ToListAsync(ct);
        var on = await db.Holds.Where(h => h.WallId == kept.WallId && h.WallPanelId == kept.WallPanelId)
            .Select(h => new { h.Id, h.StackedOnHoldId }).ToDictionaryAsync(h => h.Id, h => h.StackedOnHoldId, ct);
        on[kept.Id] = kept.StackedOnHoldId;
        foreach (var upper in moved)
        {
            on[upper.Id] = upper.StackedOnHoldId = HoldStackOrder.WouldLoop(on, upper.Id, kept.Id) ? null : kept.Id;
        }
    }

    /// <summary>Puts a hold on another hold of its photo, or (null) back on the wall, as a person asked for it.</summary>
    /// <param name="db">The context.</param>
    /// <param name="hold">The tracked hold.</param>
    /// <param name="baseId">Its new base, or null.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    /// <exception cref="UserFacingException">The base is on another photo, or sits on this hold itself.</exception>
    public static async Task SetBaseAsync(BlocwerkDbContext db, Hold hold, Guid? baseId, CancellationToken ct = default)
    {
        if (baseId is not { } id)
        {
            hold.StackedOnHoldId = null;
            return;
        }

        var photo = await db.Holds.AsNoTracking().Where(h => h.WallId == hold.WallId && h.WallPanelId == hold.WallPanelId)
            .Select(h => new { h.Id, h.StackedOnHoldId, h.Generation }).ToListAsync(ct);
        if (photo.FirstOrDefault(h => h.Id == id) is not { } lower || (hold.WallPanelId is null && lower.Generation != hold.Generation))
        {
            throw new UserFacingException("A hold can only sit on another hold of the same photo.");
        }

        if (HoldStackOrder.WouldLoop(photo.ToDictionary(h => h.Id, h => h.StackedOnHoldId), hold.Id, id))
        {
            throw new UserFacingException("That hold already sits on this one, so this one cannot sit on it.");
        }

        hold.StackedOnHoldId = id;
    }

    private static int Apply(List<(Hold Upper, Hold Base)> plan)
    {
        plan.ForEach(p => p.Upper.StackedOnHoldId = p.Base.Id);
        return plan.Count;
    }

    private static async Task<(int Width, int Height)?> SizeAsync(BlocwerkDbContext db, Guid? panelId, CancellationToken ct) =>
        panelId is { } id && (await LookOverFrameSource.SizesAsync(db, [id], ct)).TryGetValue(id, out var size) ? size : null;
}
