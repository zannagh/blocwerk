using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// The cross-panel <see cref="HoldLink"/> half of the big-wall promote. A link says "these two holds,
/// on overlapping panels, are the same physical hold"; under immutable generations a promote creates a
/// NEW row for every carried hold, so a link left pointing at the retained gen-N rows stops describing
/// anything the live wall shows. Promote used to create links only BETWEEN freshly promoted neighbour
/// detections and never projected the existing ones forward, so every big update silently wiped a wall's
/// whole link set — contradicting the wall page's own promise that a link "survives the next wall update
/// as one hold". This re-states each surviving link between the successors.
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>
    /// Re-states every existing cross-panel link on the wall between the successors of its two ends, so
    /// links survive the generation bump. Run AFTER the carry and the neighbour promote, while the new
    /// <see cref="HoldGenerationLink"/> rows and the neighbours' fresh links are still pending in the
    /// change tracker, and BEFORE the promote's single SaveChanges — it commits nothing of its own.
    /// <para>
    /// Per end, in order: a carried hold maps to its successor; an end OUTSIDE this promote's scope (a hold
    /// on a panel this subset promote did not re-shoot) stays exactly where it is, which is what keeps a
    /// link spanning an updated and a non-updated panel alive; and an end that was in scope but has no
    /// successor is gone at the new generation — removed by the user, or dropped as a crash-mat false hold
    /// before the carry — so the link is dropped rather than re-pointed at a retired row.
    /// </para>
    /// <para>
    /// <paramref name="inScopeOldHoldIds"/> is therefore the promote's WHOLE old-generation scope, not just
    /// the holds that reached the carry: the mat/floor filter removes rows from the carry set that still go
    /// historic here, and reading those as out-of-scope would resurrect a link onto a retired hold.
    /// </para>
    /// <para>
    /// The superseded rows are DELETED, not kept as history. A link carries no narrative of its own beyond
    /// its kind and creator — both copied onto the successor row — while every reader (the appearance sync,
    /// the startup backfill, the link tool) walks the wall's whole link set as one undirected graph. Left
    /// behind, the old rows would fuse retired gen-N holds into the live components and let a historic row
    /// win a source-of-truth vote; they would also pin the retained holds under the links' Restrict FK.
    /// </para>
    /// </summary>
    private static async Task CarryHoldLinksAsync(
        BlocwerkDbContext db,
        Guid wallId,
        IReadOnlySet<Guid> inScopeOldHoldIds)
    {
        var successors = CollectSuccessors(db, wallId);

        // Load the committed rows into the tracker, then work off Local so the links this promote has
        // already ADDED (PromoteNeighboursAsync) and REMOVED (a discarded hold's cleanup) are seen too.
        await db.HoldLinks.Where(l => l.WallId == wallId).LoadAsync();
        var live = db.HoldLinks.Local
            .Where(l => l.WallId == wallId && db.Entry(l).State != EntityState.Deleted)
            .ToList();

        // Seed the dedupe set with every pair that will exist after this promote but is not ours to
        // rewrite: the links PromoteNeighboursAsync just added between freshly promoted detections.
        var taken = live
            .Where(l => db.Entry(l).State == EntityState.Added)
            .Select(l => Unordered(l.HoldAId, l.HoldBId))
            .ToHashSet();

        foreach (var link in live.Where(l => db.Entry(l).State != EntityState.Added))
        {
            RemapLink(db, wallId, link, successors, inScopeOldHoldIds, taken);
        }
    }

    /// <summary>
    /// Re-states ONE existing link at the new generation: leaves it alone when neither end advanced,
    /// otherwise retires the row and — unless an end was removed, the two ends merged onto a single
    /// successor, or the resulting pair already exists — re-inserts it between the successors, carrying
    /// the original kind, creator and creation time so the link keeps its provenance.
    /// </summary>
    private static void RemapLink(
        BlocwerkDbContext db,
        Guid wallId,
        HoldLink link,
        IReadOnlyDictionary<Guid, Guid> successors,
        IReadOnlySet<Guid> inScopeOldHoldIds,
        HashSet<(Guid, Guid)> taken)
    {
        var endA = ResolveEnd(link.HoldAId, successors, inScopeOldHoldIds);
        var endB = ResolveEnd(link.HoldBId, successors, inScopeOldHoldIds);
        if (endA == link.HoldAId && endB == link.HoldBId)
        {
            // Neither end advanced: a link wholly outside this promote's scope, left untouched.
            taken.Add(Unordered(link.HoldAId, link.HoldBId));
            return;
        }

        db.HoldLinks.Remove(link);
        if (endA is not { } newA || endB is not { } newB || newA == newB)
        {
            // An end was removed by this promote, or both ends merged onto ONE successor — in which case
            // the two panel copies are now a single row and there is nothing left to link.
            return;
        }

        if (!taken.Add(Unordered(newA, newB)))
        {
            return;
        }

        db.HoldLinks.Add(new HoldLink
        {
            WallId = wallId,
            HoldAId = newA,
            HoldBId = newB,
            Kind = link.Kind,
            CreatedAt = link.CreatedAt,
            CreatedByUserId = link.CreatedByUserId,
        });
    }

    /// <summary>
    /// Where one end of a link lands at the new generation: its successor when the carry made one, itself
    /// when the hold is outside this promote's scope (still live on a panel that was not re-shot), or null
    /// when it was in scope without a successor (removed by the user, or dropped as a mat false hold).
    /// </summary>
    private static Guid? ResolveEnd(
        Guid holdId,
        IReadOnlyDictionary<Guid, Guid> successors,
        IReadOnlySet<Guid> inScopeOldHoldIds)
    {
        if (successors.TryGetValue(holdId, out var successor))
        {
            return successor;
        }

        return inScopeOldHoldIds.Contains(holdId) ? null : holdId;
    }

    /// <summary>
    /// The old→new hold map this promote just recorded, read from the <see cref="HoldGenerationLink"/> rows
    /// pending in the change tracker (nothing is saved until the promote's single commit). Only the rows
    /// added during THIS promote are considered, so an earlier generation's lineage can never re-map a link.
    /// </summary>
    private static Dictionary<Guid, Guid> CollectSuccessors(BlocwerkDbContext db, Guid wallId)
    {
        var successors = new Dictionary<Guid, Guid>();
        foreach (var lineage in db.HoldGenerationLinks.Local)
        {
            if (db.Entry(lineage).State != EntityState.Added || lineage.WallId != wallId)
            {
                continue;
            }

            if (lineage.OldHoldId is { } oldId && lineage.NewHoldId is { } newId)
            {
                successors[oldId] = newId;
            }
        }

        return successors;
    }

    private static (Guid, Guid) Unordered(Guid a, Guid b) => a.CompareTo(b) <= 0 ? (a, b) : (b, a);
}
