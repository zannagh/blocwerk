// <copyright file="WallBigUpdateService.Alignment.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// What happens when the matcher cannot line a new photo up with the one it replaces (or with the staged
/// centre). The old holds on such a panel are still carried — a failed alignment never loses a hold — but
/// at their OLD coordinates, which on a reframed photo can be hundreds of pixels off. That must never be
/// silent: the panel is recorded on the session (sticky, so a resume still shows it), the review shows a
/// banner, the carries stay unconfirmed, and the promote flags the blind carries <see cref="Hold.NeedsReview"/>.
/// Boulders are deliberately NOT flagged: a failed alignment says nothing about whether a hold changed.
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>
    /// Runs one neighbour panel's OWN carryover match — its old holds (left, on the retained live panel
    /// image) against its fresh staged detections (right) — and folds the result into the same session
    /// buckets as the centre. With no old holds there is nothing to carry; with no reachable old panel
    /// image (or a matcher failure) every old hold is offered as a removal candidate so the review layer
    /// carries it by default (clone), never silently losing it.
    /// </summary>
    /// <returns>False when the panel could not be aligned, i.e. its old holds carry at their old positions.</returns>
    private bool MatchNeighbourCarryover(
        List<Hold>? neighbourOldHolds,
        IReadOnlyDictionary<Guid, byte[]> oldPanelPhotosById,
        byte[] stagedPhoto,
        List<MatcherHold> stagedMatcher,
        Guid[] stagedIndex,
        List<CarryoverProposal> carryover,
        List<Guid> removedCandidates,
        Dictionary<Guid, HoldPositionNorm> carriedWarp,
        Dictionary<Guid, IReadOnlyList<HoldPositionNorm>> carriedShapes,
        (Guid WallId, int Col, int Row) at,
        HoldOverlapSeed? seed,
        bool recordedUnaligned)
    {
        if (neighbourOldHolds is null || neighbourOldHolds.Count == 0)
        {
            return true;
        }

        if (neighbourOldHolds[0].WallPanelId is not { } oldPanelId
            || !oldPanelPhotosById.TryGetValue(oldPanelId, out var oldPhoto))
        {
            LogUnaligned(at, "the previous photo of this panel is missing", neighbourOldHolds.Count, null);
            removedCandidates.AddRange(neighbourOldHolds.Select(h => h.Id));
            return false;
        }

        var (oldMatcher, oldIndex) = BuildMatcherHolds(neighbourOldHolds);
        try
        {
            var carry = MatchUnlessUnaligned(
                recordedUnaligned, oldPhoto, oldMatcher, stagedPhoto, stagedMatcher, HoldOverlapDirection.Right, logger, seed);
            CollectCarryover(carry, oldIndex, stagedIndex, carryover, removedCandidates, carriedWarp, carriedShapes);
            return true;
        }
        catch (Exception ex)
        {
            LogUnaligned(at, ex.Message, neighbourOldHolds.Count, ex);
            removedCandidates.AddRange(neighbourOldHolds.Select(h => h.Id));
            return false;
        }
    }

    /// <summary>
    /// Runs the matcher, unless an earlier run of this session already recorded the pair as unaligned: then it
    /// fails the same way without running, so a resume shows exactly what the user reviewed before (a
    /// borderline pair can pass a later RANSAC run and would otherwise swap blind carries for new guesses).
    /// </summary>
    private HoldOverlapResult MatchUnlessUnaligned(
        bool recordedUnaligned,
        byte[] leftImage,
        IReadOnlyList<MatcherHold> leftHolds,
        byte[] rightImage,
        IReadOnlyList<MatcherHold> rightHolds,
        HoldOverlapDirection direction,
        ILogger? diag,
        HoldOverlapSeed? seed)
    {
        if (recordedUnaligned)
        {
            throw new InvalidOperationException("An earlier run of this update could not line these photos up.");
        }

        return overlapMatcher.Match(leftImage, leftHolds, rightImage, rightHolds, direction, diag, seed);
    }

    /// <summary>The panels this open session has already recorded as unaligned (empty when none is open).</summary>
    private static async Task<(IReadOnlySet<Guid> Carry, IReadOnlySet<Guid> Overlap)> RecordedUnalignedAsync(
        BlocwerkDbContext db, Guid wallId)
    {
        var open = await WallUpdateSessions.FindOpenAsync(db, wallId);
        return (open?.UnalignedCarryPanelIds.ToHashSet() ?? [], open?.UnalignedOverlapPanelIds.ToHashSet() ?? []);
    }

    private void LogUnaligned((Guid WallId, int Col, int Row) at, string reason, int holdCount, Exception? ex)
    {
        logger.LogWarning(
            ex,
            "Carryover alignment failed for panel ({Col},{Row}) on wall {WallId}: {Reason}. Its {Count} old holds are carried at their old positions, unconfirmed",
            at.Col, at.Row, at.WallId, reason, holdCount);
    }

    /// <summary>
    /// Records this run's alignment failures on the open session. Sticky: a panel once recorded stays recorded
    /// until the session closes (<see cref="MatchUnlessUnaligned"/> keeps later runs consistent with it), so
    /// the banner survives a resume and the promote can flag that panel's blind carries. Saves only when
    /// something new was recorded.
    /// </summary>
    private static async Task PersistAlignmentFailuresAsync(BlocwerkDbContext db, Guid wallId, BigUpdateSession session)
    {
        var open = await WallUpdateSessions.FindOpenAsync(db, wallId);
        if (open is null)
        {
            return;
        }

        var carryNow = (session.CarriedPanels ?? []).Where(p => p.AlignmentFailed).Select(p => p.StagedPanelId);
        var overlapNow = session.Neighbours.Where(n => n.AlignmentFailed).Select(n => n.PanelId);
        var carry = open.UnalignedCarryPanelIds.Union(carryNow).ToList();
        var overlap = open.UnalignedOverlapPanelIds.Union(overlapNow).ToList();
        if (carry.Count != open.UnalignedCarryPanelIds.Count || overlap.Count != open.UnalignedOverlapPanelIds.Count)
        {
            open.UnalignedCarryPanelIds = carry;
            open.UnalignedOverlapPanelIds = overlap;
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Promote half: every hold CLONED forward (no staged twin) onto a panel whose alignment failed was
    /// placed without a reliable position, so it is flagged <see cref="Hold.NeedsReview"/> for the owner to
    /// check. Promoted twins are not touched — a twin is a fresh detection on the new photo, positioned by
    /// it — and no boulder is flagged. Call after the carry has added its clones, before the session closes.
    /// </summary>
    private static async Task FlagUnalignedCarriesAsync(BlocwerkDbContext db, Guid wallId)
    {
        var open = await WallUpdateSessions.FindOpenAsync(db, wallId);
        if (open is null || open.UnalignedCarryPanelIds.Count == 0)
        {
            return;
        }

        var unaligned = open.UnalignedCarryPanelIds.ToHashSet();
        foreach (var entry in db.ChangeTracker.Entries<Hold>())
        {
            if (entry.State == EntityState.Added
                && entry.Entity.WallPanelId is { } panelId
                && unaligned.Contains(panelId))
            {
                entry.Entity.NeedsReview = true;
            }
        }
    }

    /// <summary>
    /// Classifies a carryover auto-match exception into a fail-soft status: a native/library-load
    /// failure means the matcher could not run at all (<see cref="AutoMatchStatus.Unavailable"/>);
    /// anything else is a matching failure the matcher itself raised (<see cref="AutoMatchStatus.Failed"/>),
    /// e.g. the "too few texture matches" homography path or an undecodable image.
    /// </summary>
    private static AutoMatchStatus ClassifyAutoMatchFailure(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is DllNotFoundException or TypeInitializationException or BadImageFormatException)
            {
                return AutoMatchStatus.Unavailable;
            }
        }

        return AutoMatchStatus.Failed;
    }
}
