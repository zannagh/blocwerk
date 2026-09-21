using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// The immutable-generation carryover half of the big-wall promote. Old gen-N rows are RETAINED as
/// history; every carried hold gets a DISTINCT gen-N+1 row (the promoted staged twin, or a clone when
/// the hold fell outside the new frame); a <see cref="HoldGenerationLink"/> ties old to new; and the
/// memberships on each re-shot hold are re-pointed onto its new row so no committed boulder ever loses a
/// hold. In a subset promote a boulder spanning an updated and a non-updated panel is PARTIALLY repointed
/// — its updated-panel membership advances to the successor while its non-updated membership stays on the
/// retained gen-N row — so the live read resolves each hold at ITS panel's live generation.
/// <para>
/// Boulder disposition is resolved in a single UP-FRONT pass BEFORE any advance, so the outcome does
/// not depend on decision order: Pass 0 freezes (marks historic) every live boulder that uses any
/// removed hold — without deleting a membership or repointing it, so its gen-N schematic still renders;
/// Pass 1 then advances the carried holds, and the repoint guard skips the already-frozen boulders.
/// Nothing here calls SaveChanges — <see cref="PromoteAsync"/> commits the whole thing in one transaction.
/// </para>
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>
    /// How many hold radii a warp-predicted outline's vertex mean may sit from the successor's centre and
    /// still be accepted on the radius half of the test. Two radii is a whole hold-width of slack — ample
    /// for registration jitter — while a physically moved hold misses by many multiples of it. Note this
    /// is only the FLOOR of the window: the outline's own extent widens it (see
    /// <see cref="BelongsToSuccessor"/>), because a traced polygon's radius is stale by construction.
    /// </summary>
    private const double WarpedShapeRadiusFactor = 2.0;

    /// <summary>
    /// Floor for the radius used in the tolerance above, so a hold with a zero/degenerate radius still
    /// gets a workable window instead of demanding an exact vertex-mean hit.
    /// </summary>
    private const double MinWarpedShapeRadius = 0.005;

    /// <summary>
    /// Reconciles the carried old holds against the staged detections per the user's carryover decisions,
    /// then keeps or discards the remaining staged CENTRE detections as new holds. Twin lookup spans EVERY
    /// updated panel (<paramref name="stagedTwins"/>), so a co-updated neighbour's old hold promotes its
    /// SAME-panel twin in place exactly as a centre hold does — never cloning a duplicate alongside the
    /// kept detection; only <paramref name="centerStaged"/> (centre-only) drives the new-centre reconcile.
    /// Returns the ids of the staged holds that went live (an identity set now that a promoted staged twin
    /// keeps its own id) — centre twins AND neighbour twins — so the neighbour step can resolve links whose
    /// centre end survived and skip re-promoting a consumed twin.
    /// </summary>
    private async Task<HashSet<Guid>> CarryCentreHoldsAsync(
        BlocwerkDbContext db,
        Guid wallId,
        WallPanel centerPanel,
        int oldGen,
        int newGen,
        IReadOnlyDictionary<Guid, Hold> oldHolds,
        IReadOnlyDictionary<Guid, Hold> stagedTwins,
        IReadOnlyDictionary<Guid, Hold> centerStaged,
        BigUpdateConfirmation confirmation,
        IReadOnlyDictionary<Guid, HoldPositionNorm>? warpPositions,
        IReadOnlyDictionary<Guid, IReadOnlyList<HoldPositionNorm>>? warpShapes,
        IReadOnlyDictionary<Guid, (int Col, int Row)> panelPositions,
        IReadOnlyDictionary<(int Col, int Row), Guid> newGenPanelByPosition,
        Guid userId)
    {
        var survivingCenterStaged = new HashSet<Guid>();
        var claimedTwins = new HashSet<Guid>();

        // PASS 0 — disposition: freeze every live boulder using a removed hold BEFORE any mutation, so
        // the result is order-independent and frozen boulders keep all memberships on retained gen-N rows.
        var removedOldHoldIds = confirmation.Carryover
            .Where(d => d.Kind == CarryKind.Removed)
            .Select(d => d.OldHoldId)
            .ToHashSet();
        await FreezeRemovedBouldersAsync(db, removedOldHoldIds);

        // PASS 1 — advance: carried/changed decisions create successors and repoint STILL-advancing
        // boulders. A Removed decision now means only "no successor" — Pass 0 already did the freeze.
        var decided = new HashSet<Guid>();
        foreach (var decision in confirmation.Carryover)
        {
            if (!oldHolds.TryGetValue(decision.OldHoldId, out var oldHold))
            {
                continue;
            }

            decided.Add(decision.OldHoldId);
            if (decision.Kind == CarryKind.Removed)
            {
                continue;
            }

            var destinationPanelId = ResolveDestinationPanelId(
                oldHold, centerPanel.Id, panelPositions, newGenPanelByPosition);
            await AdvanceCarriedHoldAsync(
                db, wallId, destinationPanelId, oldGen, newGen, oldHold, decision.Kind, decision.NewHoldId,
                stagedTwins, claimedTwins, survivingCenterStaged, warpPositions, warpShapes, userId);
        }

        // Reconcile: any gen-N hold the outcome never mentions is default-carried (clone forward, link
        // Same). The UI seeds carry-all, but this keeps a dropped decision from silently losing a hold.
        foreach (var (id, oldHold) in oldHolds)
        {
            if (decided.Contains(id))
            {
                continue;
            }

            var destinationPanelId = ResolveDestinationPanelId(
                oldHold, centerPanel.Id, panelPositions, newGenPanelByPosition);
            await AdvanceCarriedHoldAsync(
                db, wallId, destinationPanelId, oldGen, newGen, oldHold, CarryKind.Carried, null,
                stagedTwins, claimedTwins, survivingCenterStaged, warpPositions, warpShapes, userId);
        }

        await ReconcileNewCentreHolds(db, centerStaged, confirmation, newGen, survivingCenterStaged);
        return survivingCenterStaged;
    }

    /// <summary>
    /// Advances one carried/changed old hold: promotes its staged twin in place (or clones the old row
    /// forward when there is no twin), copies curation, records the lineage link, and re-points the
    /// still-advancing boulders. When several old holds claim the SAME twin (a physical merge) only the
    /// first writer copies curation (first-writer-wins) and each old hold still gets its own link; the
    /// repoint is idempotent so the boulder ends with a single membership to the merged hold.
    /// </summary>
    private async Task AdvanceCarriedHoldAsync(
        BlocwerkDbContext db,
        Guid wallId,
        Guid destinationPanelId,
        int oldGen,
        int newGen,
        Hold oldHold,
        CarryKind kind,
        Guid? newHoldId,
        IReadOnlyDictionary<Guid, Hold> stagedTwins,
        HashSet<Guid> claimedTwins,
        HashSet<Guid> survivingCenterStaged,
        IReadOnlyDictionary<Guid, HoldPositionNorm>? warpPositions,
        IReadOnlyDictionary<Guid, IReadOnlyList<HoldPositionNorm>>? warpShapes,
        Guid userId)
    {
        var changed = kind == CarryKind.Changed;
        var linkKind = changed ? HoldGenerationLinkKind.Changed : HoldGenerationLinkKind.Same;

        if (newHoldId is { } twinId && stagedTwins.TryGetValue(twinId, out var staged))
        {
            if (claimedTwins.Add(twinId))
            {
                staged.Generation = newGen;
                CopyCuratedFields(oldHold, staged);
                staged.NeedsReview = changed;

                // Warp-carry (shapes): a matched twin is a fresh detection with NO custom outline. If the
                // old hold carried one, transform it onto the twin (using the twin's OWN detected centre)
                // so a custom-shaped hold that also matched keeps its warped outline, not a plain circle.
                ApplyWarpedShape(staged, oldHold.Id, warpShapes);
                survivingCenterStaged.Add(twinId);
            }
            else if (changed)
            {
                // A later claimant asserting "changed" flags the twin, but never clears the first writer's curation.
                staged.NeedsReview = true;
            }

            await RepointBouldersAsync(db, oldHold.Id, staged.Id, newGen, changed);
            AddGenerationLink(db, wallId, oldHold.Id, staged.Id, linkKind, oldGen, newGen, userId);
        }
        else
        {
            var clone = CloneToNewGeneration(oldHold, destinationPanelId, newGen, changed);

            // Warp-carry: an unmatched old hold has no staged twin to snap to, so instead of keeping
            // its stale old coordinates, reposition it to the matcher's warp-predicted new-image
            // position when one is available. Radius and all curation stay as the clone already copied
            // them. No warp position (matcher absent, or unpredictable) falls back to the old position.
            if (warpPositions is not null && warpPositions.TryGetValue(oldHold.Id, out var warped))
            {
                clone.X = warped.X;
                clone.Y = warped.Y;
            }

            // Warp-carry (shapes): transform the custom outline onto the new image too. Applied AFTER the
            // centre is repositioned above, since ShapePoints are stored relative to the (final) centre.
            ApplyWarpedShape(clone, oldHold.Id, warpShapes);

            db.Holds.Add(clone);
            await RepointBouldersAsync(db, oldHold.Id, clone.Id, newGen, changed);
            AddGenerationLink(db, wallId, oldHold.Id, clone.Id, linkKind, oldGen, newGen, userId);
        }
    }

    /// <summary>
    /// PASS 0. Marks every live boulder that references any removed hold historic (frozen), leaving all
    /// its <see cref="BoulderHold"/>s pointing at the retained gen-N rows so its schematic still renders.
    /// No membership is deleted and no boulder is advanced — the later repoint guard excludes these.
    /// Archived and already-historic boulders are left untouched (already frozen).
    /// </summary>
    private static async Task FreezeRemovedBouldersAsync(BlocwerkDbContext db, IReadOnlyCollection<Guid> removedOldHoldIds)
    {
        if (removedOldHoldIds.Count == 0)
        {
            return;
        }

        var boulderIds = await db.BoulderHolds
            .Where(bh => removedOldHoldIds.Contains(bh.HoldId))
            .Select(bh => bh.BoulderId)
            .Distinct()
            .ToListAsync();

        var boulders = await db.Boulders
            .Where(b => boulderIds.Contains(b.Id) && !b.IsArchived && !b.IsHistoric)
            .ToListAsync();

        foreach (var b in boulders)
        {
            b.IsHistoric = true;
            b.NeedsReview = false;
        }
    }

    /// <summary>
    /// After the carryover, every staged centre hold that a carry did not consume is either kept (goes
    /// live at the new generation, no lineage link) or an unreviewed detection that is hard-deleted.
    /// Consumed twins are already in <paramref name="survivingCenterStaged"/> and skipped. The rule, in
    /// order:
    /// <list type="number">
    /// <item>An EXPLICIT discard wins: a hold in <see cref="BigUpdateConfirmation.RemovedNewCenterHoldIds"/>
    /// is deleted, auto-detected or not. Since the pre-match (<c>Detected</c>) review lets the user ADD
    /// holds before matching, a manual addition can now be shown in — and discarded from — the new-holds
    /// review queue, and that decision must be honoured.</item>
    /// <item>A hold in <see cref="BigUpdateConfirmation.AcceptedNewCenterHoldIds"/> is kept.</item>
    /// <item>A hold the confirmation mentions NEITHER way was outside the review's scope. A MANUAL
    /// (non-auto-detected) one is kept — the user added it late (the touch-up step, after the carryover
    /// review computed its lists) and it must never be silently dropped. An auto-detected one is an
    /// unreviewed detection and is deleted ("not accepted == discarded").</item>
    /// </list>
    /// So the discriminator is membership of the reviewed set (accepted ∪ removed), not
    /// <see cref="Hold.IsAutoDetected"/>; the auto-detected flag only decides the fate of holds the
    /// review never saw.
    /// </summary>
    private static async Task ReconcileNewCentreHolds(
        BlocwerkDbContext db,
        IReadOnlyDictionary<Guid, Hold> centerStaged,
        BigUpdateConfirmation confirmation,
        int newGen,
        HashSet<Guid> survivingCenterStaged)
    {
        var accepted = confirmation.AcceptedNewCenterHoldIds.ToHashSet();
        var removed = confirmation.RemovedNewCenterHoldIds.ToHashSet();
        var rejected = new List<Hold>();
        foreach (var (stagedId, staged) in centerStaged)
        {
            if (survivingCenterStaged.Contains(stagedId))
            {
                continue;
            }

            // Explicitly discarded beats everything; otherwise accepted keeps it, and a hold the review
            // never saw (in neither list) survives only if the user created it by hand.
            var keep = !removed.Contains(stagedId)
                && (accepted.Contains(stagedId) || !staged.IsAutoDetected);
            if (keep)
            {
                staged.Generation = newGen;
                survivingCenterStaged.Add(stagedId);
            }
            else
            {
                rejected.Add(staged);
            }
        }

        // Rejected staged detections: no memberships by construction, but clear the Restrict FKs
        // anyway so a rejection can never roll the promote back. Prepared as ONE set — a redetect can
        // reject hundreds of detections, and this runs inside the promote transaction.
        await HoldDeletion.PrepareHoldsForDeleteAsync(
            db, rejected.Select(x => x.Id).ToList(), HoldDeleteBoulderPolicy.LeaveUntouched);
        db.Holds.RemoveRange(rejected);
    }

    /// <summary>
    /// Re-points the memberships on THIS old hold onto its new-generation successor row, for every active
    /// (non-archived, non-historic) boulder that used it. Only ever called for an old hold on a
    /// re-photographed panel (the carry scopes <c>oldHolds</c> to updated panels), so it advances exactly
    /// the memberships whose hold is being re-shot and never touches a membership on a non-updated panel.
    /// <para>
    /// Subset promote (decision D-B, corrected): a boulder WHOLLY on updated panels has every membership
    /// repointed here (full advance); a boulder SPANNING an updated and a non-updated panel is PARTIALLY
    /// repointed — this call advances its updated-panel membership to the successor while its non-updated
    /// membership, whose hold is never passed to this method, stays on the retained gen-N row. That is the
    /// invariant the live read needs: each membership resolves at ITS panel's live generation, so no hold
    /// vanishes from a spanning boulder after a partial promote. A boulder wholly on non-updated panels is
    /// never reached (none of its holds is in the carry set) and stays entirely untouched.
    /// </para>
    /// <para>
    /// Because <see cref="BoulderHold.HoldId"/> is part of the key it cannot be mutated on a tracked row —
    /// the re-point is a delete + insert of the join row, and the insert is idempotent
    /// (<see cref="BoulderHoldExists"/>) so a physical merge (two old holds → one new) yields one membership.
    /// Historic (incl. the Pass-0-frozen) and archived boulders keep their <see cref="BoulderHold"/> on the
    /// retained old row, so their older-gen schematics still render. <paramref name="changed"/> only ever
    /// comes from a Changed carry decision, so a plain carried correction never forces review.
    /// </para>
    /// </summary>
    private static async Task RepointBouldersAsync(
        BlocwerkDbContext db, Guid oldHoldId, Guid newHoldId, int newGen, bool changed)
    {
        var boulderLinks = await db.BoulderHolds
            .Where(bh => bh.HoldId == oldHoldId)
            .Include(bh => bh.Boulder)
            .ToListAsync();

        foreach (var link in boulderLinks)
        {
            if (link.Boulder is not { IsArchived: false, IsHistoric: false })
            {
                continue;
            }

            db.BoulderHolds.Remove(link);
            if (!BoulderHoldExists(db, link.BoulderId, newHoldId))
            {
                db.BoulderHolds.Add(new BoulderHold
                {
                    BoulderId = link.BoulderId,
                    HoldId = newHoldId,
                    Type = link.Type,
                    Usage = link.Usage,
                });
            }

            link.Boulder.Generation = newGen;
            if (changed)
            {
                link.Boulder.NeedsReview = true;
            }
        }
    }

    /// <summary>
    /// True when a <see cref="BoulderHold"/> for this pair is already present — as a pending insert in
    /// the change tracker (an earlier repoint this transaction) or a committed row. Guards the idempotent
    /// repoint insert against a duplicate-key crash when two old holds merge onto one successor.
    /// </summary>
    private static bool BoulderHoldExists(BlocwerkDbContext db, Guid boulderId, Guid holdId)
    {
        var pending = db.BoulderHolds.Local.Any(bh =>
            bh.BoulderId == boulderId && bh.HoldId == holdId
            && db.Entry(bh).State != EntityState.Deleted);
        if (pending)
        {
            return true;
        }

        return db.BoulderHolds.Any(bh => bh.BoulderId == boulderId && bh.HoldId == holdId);
    }

    /// <summary>
    /// Copies the curated (user-set) fields from the old hold onto its successor row, leaving the
    /// successor's own detected position and shape untouched. Virtual only carries forward from a
    /// virtual predecessor; a real detection is never demoted to virtual.
    /// </summary>
    private static void CopyCuratedFields(Hold from, Hold to)
    {
        to.Name = from.Name;
        to.Color = from.Color;
        to.Material = from.Material;
        to.Category = from.Category;
        to.HandType = from.HandType;
        to.IsOnKickboard = from.IsOnKickboard;
        if (from.IsVirtual)
        {
            to.IsVirtual = true;
        }
    }

    /// <summary>
    /// Deep-copies an old hold into a fresh next-generation row on its destination panel, keeping its
    /// position (the hold fell outside the new capture, so there is no better one) and a new identity.
    /// </summary>
    private static Hold CloneToNewGeneration(Hold oldHold, Guid destinationPanelId, int newGen, bool changed)
    {
        var clone = oldHold.Clone();
        clone.Id = Guid.NewGuid();
        clone.WallPanelId = destinationPanelId;
        clone.Generation = newGen;
        clone.NeedsReview = changed;
        clone.AlignmentSourceHoldId = null;
        return clone;
    }

    /// <summary>
    /// The NEW-generation panel row a carried old hold's successor must live on: the staged panel at the
    /// old hold's OWN grid position (Col,Row). A centre old hold resolves to the new centre panel; a
    /// co-updated NEIGHBOUR's old hold resolves to that neighbour's new-generation panel — never the
    /// centre — which is the bug fix (immutable-generation invariant: the successor stays at the same
    /// position). A null panel id is a legacy centre-photo hold, which the centre (0,0) subsumes. Falls
    /// back to <paramref name="centerPanelId"/> only if the position has no staged row (never in practice,
    /// since the hold is on a re-photographed panel).
    /// </summary>
    private static Guid ResolveDestinationPanelId(
        Hold oldHold,
        Guid centerPanelId,
        IReadOnlyDictionary<Guid, (int Col, int Row)> panelPositions,
        IReadOnlyDictionary<(int Col, int Row), Guid> newGenPanelByPosition)
    {
        var position = oldHold.WallPanelId is { } id && panelPositions.TryGetValue(id, out var pos)
            ? pos
            : (0, 0);
        return newGenPanelByPosition.TryGetValue(position, out var panelId) ? panelId : centerPanelId;
    }

    /// <summary>
    /// Sets a successor hold's custom outline to the matcher's warp-predicted polygon when one exists for
    /// the old hold. The warped vertices are ABSOLUTE new-image normalized points; ShapePoints are stored
    /// as centre-relative offsets, so each vertex is rebased against the successor's OWN (already-final)
    /// centre. A degenerate polygon (&lt; 3 vertices) or no entry leaves the successor's shape untouched —
    /// i.e. whatever the clone copied (the old outline), or, for a matched twin, NOTHING: a staged
    /// detection is created without ShapePoints, so a twin that never gets a warped outline written has
    /// none at all.
    /// <para>
    /// A warped polygon that does not sit ON the successor is REFUSED (see <see cref="BelongsToSuccessor"/>).
    /// The warp is a prediction of where the OLD hold's pixels land; for a hold that physically MOVED the
    /// prediction stays near the old spot while the matched twin sits at the new one, and rebasing those
    /// absolute vertices on the twin's centre produced offsets of roughly (old − new) — a polygon drawn
    /// far from its own hold, which then left the viewBox entirely on the first drag.
    /// </para>
    /// <para>
    /// A refusal on the MATCHED-TWIN path therefore COSTS the outline: the predecessor may have carried a
    /// hand-traced polygon and the twin has none, so the hold comes out of the promote as a plain circle.
    /// That is still the honest answer — translating the old photo's polygon onto a hold that moved would
    /// assert a trace the new photo never supports — but it must not be silent, so the successor is flagged
    /// <see cref="Hold.NeedsReview"/>. The owner then finds it in the review queue and re-traces it, instead
    /// of discovering a lost outline months later. A clone (no twin) keeps the copied old outline and is
    /// not flagged: nothing was lost there.
    /// </para>
    /// </summary>
    private static void ApplyWarpedShape(
        Hold successor,
        Guid oldHoldId,
        IReadOnlyDictionary<Guid, IReadOnlyList<HoldPositionNorm>>? warpShapes)
    {
        if (warpShapes is null || !warpShapes.TryGetValue(oldHoldId, out var polygon) || polygon.Count < 3)
        {
            return;
        }

        if (!BelongsToSuccessor(successor, polygon))
        {
            // Nothing is written, and the successor may now be left with no outline at all. Flag it so the
            // loss surfaces in the review queue rather than passing as a hold that never had one.
            if (successor.ShapePoints is not { Count: >= 3 })
            {
                successor.NeedsReview = true;
            }

            return;
        }

        successor.ShapePoints = polygon
            .Select(v => new ShapePoint { Dx = v.X - successor.X, Dy = v.Y - successor.Y })
            .ToList();
    }

    /// <summary>
    /// True when the warped polygon sits ON the successor — i.e. it can plausibly be that hold's outline.
    /// The window is the LARGER of two things: <see cref="WarpedShapeRadiusFactor"/> hold radii, and the
    /// polygon's own extent (the half-diagonal of its bounding box around its vertex mean).
    /// <para>
    /// The extent half is what makes this safe on traced outlines. A polygon is hand-drawn vertex by vertex
    /// and never updates the hold's radius — changing the radius RESETS the polygon to a default octagon —
    /// so a rail or volume traced out of a small detection keeps that small radius for ever, and its vertex
    /// mean legitimately sits many radii out. A radius-only test calls such an outline detached. An outline
    /// of a hold contains that hold's centre, though, so its mean offset never exceeds its own extent,
    /// while an (old − new) translation carries the whole polygon clear of the hold and does exceed it.
    /// </para>
    /// <para>
    /// Deliberately a refusal rather than a re-centre on the polygon's own vertex mean: for a moved hold the
    /// warped polygon is the shape the OLD photo saw at the OLD place, and translating it onto the twin
    /// would assert a traced outline the new photo never supports. Keeping what the successor already has
    /// is the honest answer (see <see cref="ApplyWarpedShape"/> for the review flag that makes the loss
    /// visible), and it leaves every legitimate warp-carry bit-for-bit unchanged.
    /// </para>
    /// </summary>
    private static bool BelongsToSuccessor(Hold successor, IReadOnlyList<HoldPositionNorm> polygon)
    {
        var meanX = polygon.Average(v => v.X);
        var meanY = polygon.Average(v => v.Y);
        var dx = meanX - successor.X;
        var dy = meanY - successor.Y;

        var extentX = polygon.Max(v => Math.Abs(v.X - meanX));
        var extentY = polygon.Max(v => Math.Abs(v.Y - meanY));
        var extent = Math.Sqrt((extentX * extentX) + (extentY * extentY));

        var radiusTolerance = Math.Max(successor.Radius, MinWarpedShapeRadius) * WarpedShapeRadiusFactor;
        var tolerance = Math.Max(radiusTolerance, extent);
        return (dx * dx) + (dy * dy) <= tolerance * tolerance;
    }

    private static void AddGenerationLink(
        BlocwerkDbContext db,
        Guid wallId,
        Guid oldHoldId,
        Guid newHoldId,
        HoldGenerationLinkKind kind,
        int fromGen,
        int toGen,
        Guid userId)
    {
        db.HoldGenerationLinks.Add(new HoldGenerationLink
        {
            WallId = wallId,
            OldHoldId = oldHoldId,
            NewHoldId = newHoldId,
            Kind = kind,
            FromGeneration = fromGen,
            ToGeneration = toGen,
            CreatedByUserId = userId,
        });
    }
}
