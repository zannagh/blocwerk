// <copyright file="HoldMergeSplitService.Split.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// The split tool: one hold row that is really two holds is cut along a drawn line. The LARGER part keeps the hold's id,
/// and with it its lineage and panel links; the smaller part is a new hold of the same colour and type. Each boulder on
/// the hold picks a part (the larger by default), and the holds stacked on it go to the part that holds their centre.
/// The panel links go with one part, as chosen (by default the part most of the boulders use): a link says "this is the
/// same physical hold as that one on the neighbouring photo", which can be true of one part only.
/// </summary>
/// <remarks>
/// The new part gets NO lineage row (<see cref="HoldGenerationLink"/>) to the original's predecessor, deliberately. A
/// lineage row reads "this hold of the earlier photo became that hold of this one", and the readers rely on an old hold
/// having at most one successor per step: the move re-measure (<c>DifferentialDisplacement.Measure</c>) keys its pairs
/// on the old hold and would throw on a second one, the kept-hold backfill would take the part for a carried copy nobody
/// confirmed, and the boulder history walks would see a fork. The original keeps its lineage (it is the part the old hold
/// continues as); the part is recognised as a split part through the journal instead (<see cref="SplitPartIdsAsync"/>).
/// </remarks>
public sealed partial class HoldMergeSplitService
{
    /// <inheritdoc/>
    public async Task<HoldSplitPreview> PreviewSplitAsync(Guid wallId, Guid holdId, IReadOnlyList<NormalizedPoint> stroke, CancellationToken ct = default)
    {
        var (db, _) = await OpenAsync(wallId, "Splitting a hold", ct);
        await using (db)
        {
            var hold = await LoadLiveAsync(db, wallId, holdId, ct);
            var cut = HoldOutlineBoolean.Split(OutlineRings.Of(hold), ToP2(stroke), await AspectAsync(db, hold, ct));
            var boulders = await db.BoulderHolds.AsNoTracking()
                .Where(bh => bh.HoldId == holdId)
                .Select(bh => new HoldSplitBoulder(bh.BoulderId, bh.Boulder.Name, bh.Boulder.IsArchived || bh.Boulder.IsHistoric))
                .ToListAsync(ct);
            return cut.Refusal is { } refusal
                ? new HoldSplitPreview(refusal, null, null, boulders)
                : new HoldSplitPreview(
                    null, ToOutline(cut.Larger!), ToOutline(cut.Smaller!), [.. boulders.OrderBy(b => b.IsHistoric).ThenBy(b => b.Name)],
                    await db.HoldLinks.CountAsync(l => l.HoldAId == holdId || l.HoldBId == holdId, ct));
        }
    }

    /// <inheritdoc/>
    public async Task<HoldToolResult> SplitAsync(
        Guid wallId,
        Guid holdId,
        IReadOnlyList<NormalizedPoint> stroke,
        IReadOnlyCollection<Guid> bouldersOnSmallerPart,
        bool? panelLinksToSmaller = null,
        CancellationToken ct = default)
    {
        var (db, userId) = await OpenAsync(wallId, "Splitting a hold", ct);
        await using (db)
        {
            using var wallLock = WallHoldWriteLock.TryAcquire(wallId, BusyMessage);
            var hold = await LoadLiveAsync(db, wallId, holdId, ct);
            var cut = HoldOutlineBoolean.Split(OutlineRings.Of(hold), ToP2(stroke), await AspectAsync(db, hold, ct));
            if (cut.Refusal is { } refusal)
            {
                throw new UserFacingException(refusal);
            }

            var part = NewPart(hold);
            cut.Larger!.WriteTo(hold);
            cut.Smaller!.WriteTo(part);
            db.Holds.Add(part);
            var (moved, stayed) = await MoveBouldersToPartAsync(db, hold, part, bouldersOnSmallerPart.ToHashSet(), ct);
            await RestackOntoPartAsync(db, hold, part, cut.Smaller, ct);
            if (panelLinksToSmaller ?? moved > stayed)
            {
                await HoldRelinking.RepointHoldLinksAsync(db, part.Id, hold.Id, ct);
            }

            ct.ThrowIfCancellationRequested();
            var batchId = await SaveInBatchAsync(db, SplitBatchLabel, wallId, null, ct);

            QueueFootprints(wallId, hold.Id, part.Id);
            BlocwerkMetrics.RecordHoldUpdated(wallId, "split");
            logger.LogInformation(
                "Split tool: hold {HoldId} on wall {WallId} split by {UserId} into itself and {PartId}: {Moved} boulders moved to the new part, batch {BatchId}",
                hold.Id, wallId, userId, part.Id, moved, batchId);
            return new HoldToolResult(batchId, hold.Id, part.Id, moved, 0);
        }
    }

    /// <summary>The new hold for the smaller part: the same physical kind of hold, a hand-made outline, nothing to review.</summary>
    private static Hold NewPart(Hold source) => new()
    {
        WallId = source.WallId,
        WallPanelId = source.WallPanelId,
        Generation = source.Generation,
        Color = source.Color,
        ProposedColor = source.ProposedColor,
        Material = source.Material,
        Category = source.Category,
        HandType = source.HandType,
        IsOnKickboard = source.IsOnKickboard,
        IsAutoDetected = false,
        Confidence = source.Confidence,
        NeedsReview = false,
        StackedOnHoldId = source.StackedOnHoldId,
        FacetId = source.FacetId,
        PlaneAMm = source.PlaneAMm,
        PlaneBMm = source.PlaneBMm,
    };

    /// <summary>Moves the chosen boulders' memberships onto the new part (tracked, not saved); says how many moved and stayed.</summary>
    private static async Task<(int Moved, int Stayed)> MoveBouldersToPartAsync(BlocwerkDbContext db, Hold hold, Hold part, HashSet<Guid> chosen, CancellationToken ct)
    {
        var links = await db.BoulderHolds.Where(bh => bh.HoldId == hold.Id).ToListAsync(ct);
        int moved = 0;
        foreach (var link in links.Where(l => chosen.Contains(l.BoulderId)))
        {
            // The hold id is part of the key, so a move is a delete plus an insert.
            db.BoulderHolds.Remove(link);
            db.BoulderHolds.Add(new BoulderHold { BoulderId = link.BoulderId, HoldId = part.Id, Type = link.Type, Usage = link.Usage });
            moved++;
        }

        return (moved, links.Count - moved);
    }

    /// <summary>A hold that sat on the split hold sits on whichever part holds its centre.</summary>
    private static async Task RestackOntoPartAsync(BlocwerkDbContext db, Hold hold, Hold part, OutlineRings smaller, CancellationToken ct)
    {
        foreach (var upper in await db.Holds.Where(h => h.StackedOnHoldId == hold.Id).ToListAsync(ct))
        {
            if (smaller.Covers(new P2(upper.X, upper.Y)))
            {
                upper.StackedOnHoldId = part.Id;
            }
        }
    }

    private static List<P2> ToP2(IReadOnlyList<NormalizedPoint> stroke) => [.. stroke.Select(p => new P2(p.X, p.Y))];
}
