// <copyright file="WallBigUpdateService.Relocation.cs" company="Blocwerk">
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
/// The "this hold moved" half of the big update: computing the relocation suggestions once the matcher's
/// outcome is known, and folding the accepted ones into the promote. See <see cref="HoldRelocationProposer"/>
/// for what is suggested and <see cref="RelocationFold"/> for what accepting one means.
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>
    /// Computes and persists the session's relocation suggestions, ONCE: the disappeared old holds of the
    /// reviewed (centre) panel × the staged centre holds that matched no old hold. A session that already
    /// has them (<see cref="WallUpdateSession.RelocationsProposedAt"/>) keeps them, so a resume shows the
    /// same list. Skipped while the auto-match did not run cleanly (its outcome is not known yet — a later
    /// resume may succeed). Fail-soft: a failure here never breaks the resume.
    /// </summary>
    private async Task EnsureRelocationProposalsAsync(BlocwerkDbContext db, Wall wall, BigUpdateSession session)
    {
        var open = await WallUpdateSessions.FindOpenAsync(db, wall.Id);
        if (open is null || open.RelocationsProposedAt is not null || session.AutoMatchStatus != AutoMatchStatus.Ok)
        {
            return;
        }

        try
        {
            var pairs = await ComputeRelocationsAsync(db, wall, session);
            foreach (var pair in pairs)
            {
                db.WallUpdateRelocationProposals.Add(new WallUpdateRelocationProposal
                {
                    SessionId = open.Id,
                    OldHoldId = pair.OldHoldId,
                    NewHoldId = pair.NewHoldId,
                    Score = pair.Score,
                    Margin = pair.Margin,
                    Metric = pair.Metric,
                });
            }

            open.RelocationsProposedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            logger.LogInformation(
                "Relocation suggestions on wall {WallId} session {SessionId}: {Count}", wall.Id, open.Id, pairs.Count);
        }
        catch (Exception ex)
        {
            // The context is the caller's: leave nothing half-added in it for a later SaveChanges.
            DiscardPendingProposals(db, open);
            logger.LogWarning(ex, "Relocation suggestions failed on wall {WallId}; the review simply shows none", wall.Id);
        }
    }

    private static void DiscardPendingProposals(BlocwerkDbContext db, WallUpdateSession open)
    {
        foreach (var entry in db.ChangeTracker.Entries<WallUpdateRelocationProposal>()
                     .Where(e => e.State == EntityState.Added).ToList())
        {
            entry.State = EntityState.Detached;
        }

        var proposedAt = db.Entry(open).Property(s => s.RelocationsProposedAt);
        if (proposedAt.IsModified)
        {
            proposedAt.CurrentValue = proposedAt.OriginalValue;
            proposedAt.IsModified = false;
        }
    }

    private async Task<IReadOnlyList<RelocationPair>> ComputeRelocationsAsync(
        BlocwerkDbContext db, Wall wall, BigUpdateSession session)
    {
        var reviewable = CarryoverScope.ReviewableOldHoldIds(session) ?? [];
        var disappearedIds = session.RemovedCandidateHoldIds.Where(reviewable.Contains).Distinct().ToList();
        var appearedIds = session.NewCenterHoldIds.Distinct().ToList();
        if (disappearedIds.Count == 0 || appearedIds.Count == 0)
        {
            return [];
        }

        var disappeared = await db.Holds.Where(h => disappearedIds.Contains(h.Id)).ToListAsync();
        var appeared = await db.Holds.Where(h => appearedIds.Contains(h.Id)).ToListAsync();
        var stagedPhoto = await db.WallPanels
            .Where(p => p.Id == session.CenterPanelId)
            .Select(p => p.StagedPhoto)
            .FirstOrDefaultAsync();

        // The old centre photo is the one the carryover matcher read the old holds from (wall.Photo).
        var fingerprints = Fingerprints(disappeared, wall.Photo);
        foreach (var (id, fingerprint) in Fingerprints(appeared, stagedPhoto))
        {
            fingerprints[id] = fingerprint;
        }

        return HoldRelocationProposer.Propose(disappeared, appeared, fingerprints, wall.GlyphsEnabled);
    }

    /// <summary>
    /// The stored fingerprint of every hold that has one, plus a freshly measured one for the rest when an
    /// outliner is wired — ONE decode of <paramref name="photo"/> for all of them. Nothing is written back:
    /// the holds stay as they are; a hold that cannot be fingerprinted simply takes no part.
    /// </summary>
    private Dictionary<Guid, HoldFingerprint> Fingerprints(IReadOnlyList<Hold> holds, byte[]? photo)
    {
        var result = new Dictionary<Guid, HoldFingerprint>();
        var missing = new List<Hold>();
        foreach (var hold in holds)
        {
            if (HoldFingerprint.FromJson(hold.FingerprintJson) is { } stored)
            {
                result[hold.Id] = WithHoldMetrics(stored, hold);
            }
            else if (!hold.IsVirtual)
            {
                missing.Add(hold);
            }
        }

        if (missing.Count == 0 || outlineService is null || photo is null)
        {
            return result;
        }

        try
        {
            using var outlines = outlineService.OpenSession(photo);
            foreach (var hold in missing)
            {
                result[hold.Id] = WithHoldMetrics(outlines.Outline(new HoldSeed(hold.X, hold.Y, hold.Radius)).Fingerprint, hold);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not fingerprint {Count} holds for relocation suggestions", missing.Count);
        }

        return result;
    }

    /// <summary>
    /// A freshly measured fingerprint knows no millimetres, but the hold may: a marker wall measured its
    /// sizes when it was ingested. They are copied in the rotation-free way enrichment stores them (long /
    /// short side), so a measured hold still compares on size — the tier that separates look-alikes.
    /// </summary>
    private static HoldFingerprint WithHoldMetrics(HoldFingerprint fingerprint, Hold hold)
    {
        if (fingerprint.WidthMm is not null || hold is not { WidthMm: > 0, HeightMm: > 0, AreaMm2: > 0 })
        {
            return fingerprint;
        }

        return fingerprint with
        {
            WidthMm = Math.Max(hold.WidthMm.Value, hold.HeightMm.Value),
            HeightMm = Math.Min(hold.WidthMm.Value, hold.HeightMm.Value),
            AreaMm2 = hold.AreaMm2,
        };
    }

    /// <summary>
    /// The promote's view of the accepted suggestions: folds them into the payload (see
    /// <see cref="RelocationFold"/>). Reads the wall's open session, which <see cref="PromoteAsync"/> has
    /// already verified is the caller's, so a stale circuit can never apply another session's accepts.
    /// </summary>
    private static async Task<BigUpdateConfirmation> FoldAcceptedRelocationsAsync(
        BlocwerkDbContext db, Guid wallId, BigUpdateConfirmation confirmation)
    {
        var open = await WallUpdateSessions.FindOpenAsync(db, wallId);
        if (open is null)
        {
            return confirmation;
        }

        var accepted = (await db.WallUpdateRelocationProposals
                .Where(p => p.SessionId == open.Id
                    && (p.Status == RelocationProposalStatus.Accepted || p.Status == RelocationProposalStatus.AcceptedAsSame))
                .Select(p => new { p.OldHoldId, p.NewHoldId, p.Status })
                .ToListAsync())
            .Select(p => (p.OldHoldId, p.NewHoldId, RelocationFold.VerdictOf(p.Status)!.Value))
            .ToList();
        return RelocationFold.Apply(confirmation, accepted);
    }
}
