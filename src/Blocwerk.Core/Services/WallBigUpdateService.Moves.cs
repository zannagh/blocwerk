// <copyright file="WallBigUpdateService.Moves.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldMoves;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Which carried holds physically moved, and what that does to their boulders (see <see cref="HoldMovePolicy"/>). The
/// plan is a pure function of the confirmed decisions, the old and staged holds and the matcher's warp, so the confirm
/// screen and the promote derive the same one.
/// </summary>
public partial class WallBigUpdateService
{
    /// <inheritdoc/>
    public async Task<HoldMovePlan> PreviewHoldMovesAsync(Guid wallId, BigUpdateConfirmation confirmation)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);
        return await PlanHoldMovesAsync(db, await AsPromotedAsync(db, wallId, confirmation));
    }

    private async Task<HoldMovePlan> PlanHoldMovesAsync(BlocwerkDbContext db, BigUpdateConfirmation confirmation)
    {
        var skip = confirmation.HandPlacedMergeOldIds?.ToHashSet() ?? [];
        var decisions = confirmation.Carryover
            .Where(d => d.Kind != CarryKind.Removed && d.NewHoldId is not null && !skip.Contains(d.OldHoldId))
            .ToList();
        var notFound = NotFoundToMark(confirmation);
        if (decisions.Count == 0)
        {
            return notFound.Count == 0 ? HoldMovePlan.Empty : new HoldMovePlan([], notFound);
        }

        var ids = decisions.Select(d => d.OldHoldId).Concat(decisions.Select(d => d.NewHoldId!.Value)).Distinct().ToList();
        var holds = await db.Holds.AsNoTracking().Where(h => ids.Contains(h.Id)).ToDictionaryAsync(h => h.Id);
        var pairs = decisions
            .Where(d => holds.ContainsKey(d.OldHoldId) && holds.ContainsKey(d.NewHoldId!.Value))
            .Select(d => (Old: holds[d.OldHoldId], Twin: holds[d.NewHoldId!.Value]))
            .Where(p => !p.Old.IsVirtual && p.Twin.WallPanelId is not null)
            .ToList();
        var panelIds = pairs.Select(p => p.Twin.WallPanelId!.Value).ToHashSet();
        var sizes = await LoadStagedPhotoSizesAsync(db, panelIds);
        var provisional = await ProvisionalPlacementsAsync(db, pairs);
        pairs = pairs.Select(p => (p.Old, WithPlacement(p.Twin, provisional))).ToList();
        var confirmed = decisions.Where(d => d.Kind == CarryKind.Changed).Select(d => d.OldHoldId).ToHashSet();
        return new HoldMovePlan(PlanPairs(pairs, confirmation, sizes, confirmed, moveOptions), notFound);
    }

    /// <summary>The moves of carried pairs whose twins are placed (or not) as they will be measured. Pure.</summary>
    /// <param name="pairs">Old holds with their successors on the staged panels.</param>
    /// <param name="confirmation">The decisions (the matcher's warp spots and outlines).</param>
    /// <param name="sizes">Each staged panel photo's size.</param>
    /// <param name="confirmed">Old holds a person said moved.</param>
    /// <param name="options">The thresholds.</param>
    /// <returns>The measured pairs, by panel and old hold id.</returns>
    internal static List<PlannedMove> PlanPairs(
        IReadOnlyList<(Hold Old, Hold Twin)> pairs,
        BigUpdateConfirmation confirmation,
        IReadOnlyDictionary<Guid, (int Width, int Height)> sizes,
        IReadOnlySet<Guid> confirmed,
        HoldMoveOptions options)
    {
        // 3D relative to each hold's unmoved neighbourhood: the registration error of the new photo is regional and
        // systematic, so only what differs from the neighbours counts as the hold's own movement.
        var threeD = DifferentialDisplacement.Measure(pairs, options);
        var moves = new List<PlannedMove>();
        foreach (var byPanel in pairs.GroupBy(p => p.Twin.WallPanelId!.Value))
        {
            var panel = Scope(byPanel.ToList(), sizes.TryGetValue(byPanel.Key, out var s) ? s : null, confirmation);
            foreach (var (old, twin) in byPanel.OrderBy(p => p.Old.Id))
            {
                if (PlanMove(old, twin, confirmation, panel, threeD.GetValueOrDefault(old.Id), confirmed.Contains(old.Id), options) is { } move)
                {
                    moves.Add(move);
                }
            }
        }

        return moves;
    }

    /// <summary>
    /// The one measure used for a hold when 3D and the photo estimate may both exist. The photo estimate is independent of
    /// stored placements and of the new photo's registration; when it says the hold stayed, a 3D "move" is a stale placement or
    /// a registration artefact. When it says moved, the 3D number is used only if it agrees; otherwise the photo's. Agreeing
    /// corroborates nothing: both map the same detected centre flat onto the wall, so they share its errors (a hold standing
    /// out of the wall, an outline drawn differently).
    /// </summary>
    internal static HoldMoveMeasure? Reconcile(HoldMoveMeasure? threeD, HoldMoveMeasure? photo, HoldMoveOptions options)
    {
        if (threeD is null)
        {
            return photo;
        }

        if (!threeD.Confident && photo is not null)
        {
            return photo with { SpreadMm = threeD.SpreadMm, RawMm = threeD.RawMm, ThreeDMm = threeD.ThreeDMm };
        }

        if (photo is null)
        {
            return threeD;
        }

        var withDiagnostics = photo with { SpreadMm = threeD.SpreadMm, RawMm = threeD.RawMm, ThreeDMm = threeD.ThreeDMm };
        if (photo.DistanceMm < (photo.ToleranceMm ?? options.NoiseMm2D))
        {
            return withDiagnostics;
        }

        var agree = Math.Abs(threeD.DistanceMm - photo.DistanceMm) <= Math.Max(options.AgreeMm, 0.5 * photo.DistanceMm);
        return agree ? threeD : withDiagnostics;
    }

    /// <summary>
    /// The holds of <see cref="BigUpdateConfirmation.NotFoundAgain"/> that the promote carries in place without a twin,
    /// i.e. whose boulders it marks. Left out: a hold a person removed or paired with a detection, and a hold not found
    /// again that a person confirmed as kept (they looked). A replaced hold stays in even when kept: another hold is there.
    /// </summary>
    internal static IReadOnlyDictionary<Guid, HoldMoveOutcome> NotFoundToMark(BigUpdateConfirmation confirmation)
    {
        if (confirmation.NotFoundAgain is not { Count: > 0 } found)
        {
            return new Dictionary<Guid, HoldMoveOutcome>();
        }

        var settled = confirmation.Carryover
            .Where(d => d.Kind == CarryKind.Removed || d.NewHoldId is not null
                || (d.Confirmed && found.GetValueOrDefault(d.OldHoldId) == HoldMoveOutcome.NotFound))
            .Select(d => d.OldHoldId)
            .ToHashSet();
        return found.Where(f => !settled.Contains(f.Key)).ToDictionary(f => f.Key, f => f.Value);
    }

    /// <summary>
    /// The confirmation as the promote acts on it: the accepted "this hold moved" suggestions folded in and, in a full
    /// revision, no carry. The preview and the promote both plan the moves from this, so the same payload gives the same plan.
    /// </summary>
    private static async Task<BigUpdateConfirmation> AsPromotedAsync(BlocwerkDbContext db, Guid wallId, BigUpdateConfirmation confirmation) =>
        await WithoutCarryInFullRevisionAsync(db, wallId, await FoldAcceptedRelocationsAsync(db, wallId, confirmation));

    /// <summary>The panel's carried pairs, old centre → new centre in normalized units (for the local old → new scale).</summary>
    private static List<PointPair> NormalizedPairs(IEnumerable<(Hold Old, Hold Twin)> pairs) =>
        pairs.Select(p => new PointPair(p.Old.X, p.Old.Y, p.Twin.X, p.Twin.Y)).ToList();

    /// <summary>One staged panel's size, scale, pairs and typical photo shift (most carried holds did not move).</summary>
    private static PanelMoveScope Scope(IReadOnlyList<(Hold Old, Hold Twin)> pairs, (int Width, int Height)? size, BigUpdateConfirmation confirmation)
    {
        var scale = size is { } sz ? PanelScaleEstimator.Estimate(pairs, sz) : null;
        var shifts = pairs
            .Select(p => HoldMoveCalculator.Distance2D(p.Twin, confirmation.CarriedWarpPositions?.GetValueOrDefault(p.Old.Id), size, scale))
            .OfType<double>();
        return new PanelMoveScope(size, scale, NormalizedPairs(pairs), HoldMoveTolerance.PanelNoiseMm(shifts));
    }

    /// <summary>
    /// One carried pair's move. The rotation and the measure come first; a pair whose new outline explains the measured shift
    /// (<see cref="ReOutlineRule"/>: the outline changed, the hold did not turn, the shift is small) stayed. A move a person
    /// confirmed is always measured; a measured move nobody confirmed is only suspected (offered in the review, flags nothing).
    /// </summary>
    private static PlannedMove? PlanMove(
        Hold old, Hold twin, BigUpdateConfirmation confirmation, PanelMoveScope panel, HoldMoveMeasure? threeD, bool confirmedMove, HoldMoveOptions options)
    {
        var warp = confirmation.CarriedWarpPositions?.GetValueOrDefault(old.Id);
        var two = HoldMoveCalculator.Distance2D(twin, warp, panel.Size, panel.MmPerPx);
        var rotation = HoldMoveCalculator.Rotation(HoldFingerprint.FromJson(old.FingerprintJson), HoldFingerprint.FromJson(twin.FingerprintJson));
        var photo = two is { } d2
            ? new HoldMoveMeasure(d2, HoldMoveSource.TwoD, rotation, Corroborated: true, ToleranceMm: HoldMoveTolerance.Photo(panel.NoiseMm, old, twin, options))
            : null;
        if (Reconcile(threeD, photo, options) is not { } measure)
        {
            return null;
        }

        var footprint = warp is null
            ? (WarpedFootprint?)null
            : new WarpedFootprint(warp, confirmation.CarriedWarpShapes?.GetValueOrDefault(old.Id), panel.Size, LocalAffine.Scale(panel.Pairs, old.X, old.Y) ?? 1);
        if (!confirmedMove && ReOutlineRule.Explains(old, twin, footprint, measure.DistanceMm, rotation, options))
        {
            return new PlannedMove(old.Id, twin.Id, measure with { Source = HoldMoveSource.ReOutline }, HoldMoveOutcome.Stayed);
        }

        return new PlannedMove(old.Id, twin.Id, measure, HoldMovePolicy.Unconfirmed(HoldMovePolicy.Classify(measure, options, confirmedMove), confirmedMove));
    }

    /// <summary>
    /// The twin with its provisional 3D position when it has none of its own (an in-memory copy; the row is not touched).
    /// </summary>
    private static Hold WithPlacement(Hold twin, IReadOnlyDictionary<Guid, StagedPlacement> placements)
    {
        if (twin is { FacetId: not null, PlaneAMm: not null } || !placements.TryGetValue(twin.Id, out var p))
        {
            return twin;
        }

        var copy = twin.Clone();
        (copy.Id, copy.FacetId, copy.PlaneAMm, copy.PlaneBMm) = (twin.Id, p.FacetId, p.PlaneAMm, p.PlaneBMm);
        return copy;
    }

    /// <summary>Writes the measurement onto the lineage link of a carried pair.</summary>
    private static void RecordMove(HoldGenerationLink link, PlannedMove? move)
    {
        if (move is null)
        {
            return;
        }

        link.MoveDistanceMm = Math.Round(move.Measure.DistanceMm, 1);
        link.MoveSource = move.Measure.Source;
        link.MoveRotationDeg = move.Measure.RotationDeg is { } r ? Math.Round(r, 1) : null;
        link.MoveOutcome = move.Outcome;
    }
}
