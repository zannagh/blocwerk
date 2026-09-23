// <copyright file="ShapeRecognitionTargets.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>One staged hold the shape recognition has to outline.</summary>
/// <param name="Hold">The staged hold (untracked snapshot).</param>
/// <param name="Reason">Why it is in scope.</param>
/// <param name="PreviousShape">What it promotes with if the step does nothing, or null (a circle).</param>
internal sealed record ShapeTarget(Hold Hold, ShapeProposalReason Reason, List<ShapePoint>? PreviousShape);

/// <summary>
/// Which staged holds of a session the recognition outlines, from the decisions already on the session:
/// the carry verdicts say which staged hold continues which old one (and whether it changed), the
/// discarded new holds and removed neighbour holds are left out, and a hold whose outline a person drew —
/// or which inherits a hand-drawn outline from its old hold — is skipped unless overwriting is allowed.
/// </summary>
internal static class ShapeRecognitionTargets
{
    public static async Task<(List<ShapeTarget> Targets, int SkippedManual)> LoadAsync(
        BlocwerkDbContext db, WallUpdateSession session, CancellationToken ct)
    {
        var panelIds = await db.WallPanels.AsNoTracking()
            .Where(p => p.WallId == session.WallId && p.Generation == session.StagedGeneration && p.StagedPhoto != null)
            .Select(p => p.Id)
            .ToListAsync(ct);
        var holds = await db.Holds.AsNoTracking()
            .Where(h => h.Generation == session.StagedGeneration && h.WallPanelId != null
                && panelIds.Contains(h.WallPanelId.Value) && !h.IsVirtual)
            .ToListAsync(ct);

        var pairing = await LoadPairingAsync(db, session.Id, ct);
        var excluded = await LoadExcludedAsync(db, session.Id, ct);
        var oldIds = pairing.Values.Select(v => v.OldHoldId).Distinct().ToList();
        var oldHolds = await db.Holds.AsNoTracking()
            .Where(h => oldIds.Contains(h.Id))
            .ToDictionaryAsync(h => h.Id, ct);

        var targets = new List<ShapeTarget>();
        var skippedManual = 0;
        foreach (var hold in holds.OrderBy(h => h.WallPanelId).ThenBy(h => h.Id))
        {
            if (excluded.Contains(hold.Id))
            {
                continue;
            }

            var paired = pairing.TryGetValue(hold.Id, out var p) ? p : ((Guid OldHoldId, ShapeProposalReason Reason)?)null;
            var reason = paired?.Reason ?? ShapeProposalReason.New;
            if (!InScope(session.ShapeScope, reason))
            {
                continue;
            }

            var old = paired is { } pair && oldHolds.TryGetValue(pair.OldHoldId, out var o) ? o : null;
            if (!session.ShapeOverwriteManual && IsHandDrawn(hold, old))
            {
                skippedManual++;
                continue;
            }

            targets.Add(new ShapeTarget(hold, reason, PreviousShapeOf(hold, old)));
        }

        return (targets, skippedManual);
    }

    public static bool InScope(ShapeRecognitionScope scope, ShapeProposalReason reason) => scope switch
    {
        ShapeRecognitionScope.All => true,
        ShapeRecognitionScope.New => reason == ShapeProposalReason.New,
        ShapeRecognitionScope.Changed => reason == ShapeProposalReason.Changed,
        _ => reason is ShapeProposalReason.New or ShapeProposalReason.Changed,
    };

    /// <summary>
    /// The hold's own outline was drawn by a person, or the old hold carried onto it has a hand-drawn one
    /// (the promote warps that onto the successor, so re-outlining would replace it all the same).
    /// </summary>
    public static bool IsHandDrawn(Hold staged, Hold? old) =>
        staged.OutlineSource == HoldOutlineSource.Manual
        || (old is not null && old.OutlineSource == HoldOutlineSource.Manual && old.ShapePoints is { Count: >= 3 });

    private static List<ShapePoint>? PreviousShapeOf(Hold staged, Hold? old)
    {
        if (staged.ShapePoints is { Count: >= 3 } own)
        {
            return own;
        }

        return old?.ShapePoints is { Count: >= 3 } inherited ? inherited : null;
    }

    /// <summary>Staged hold → the old hold carried onto it, from the carry verdicts and accepted relocations.</summary>
    private static async Task<Dictionary<Guid, (Guid OldHoldId, ShapeProposalReason Reason)>> LoadPairingAsync(
        BlocwerkDbContext db, Guid sessionId, CancellationToken ct)
    {
        var carries = await db.WallUpdateHoldDecisions.AsNoTracking()
            .Where(d => d.SessionId == sessionId && d.Kind == WallUpdateHoldDecisionKind.Carry
                && d.PairedHoldId != null && d.CarryKind != CarryKind.Removed)
            .Select(d => new { d.HoldId, NewId = d.PairedHoldId!.Value, d.CarryKind })
            .ToListAsync(ct);
        var relocations = await db.WallUpdateRelocationProposals.AsNoTracking()
            .Where(r => r.SessionId == sessionId
                && (r.Status == RelocationProposalStatus.Accepted || r.Status == RelocationProposalStatus.AcceptedAsSame))
            .Select(r => new { r.OldHoldId, r.NewHoldId, r.Status })
            .ToListAsync(ct);

        var map = new Dictionary<Guid, (Guid OldHoldId, ShapeProposalReason Reason)>();
        foreach (var c in carries)
        {
            Pair(map, c.NewId, c.HoldId, c.CarryKind == CarryKind.Changed);
        }

        foreach (var r in relocations)
        {
            Pair(map, r.NewHoldId, r.OldHoldId, r.Status == RelocationProposalStatus.Accepted);
        }

        return map;
    }

    // Several old holds may claim one staged hold (a physical merge): "changed" wins, as it does on promote.
    private static void Pair(Dictionary<Guid, (Guid OldHoldId, ShapeProposalReason Reason)> map, Guid newId, Guid oldId, bool changed)
    {
        var reason = changed ? ShapeProposalReason.Changed : ShapeProposalReason.Carried;
        if (!map.TryGetValue(newId, out var existing) || (changed && existing.Reason != ShapeProposalReason.Changed))
        {
            map[newId] = (oldId, reason);
        }
    }

    private static async Task<HashSet<Guid>> LoadExcludedAsync(BlocwerkDbContext db, Guid sessionId, CancellationToken ct)
    {
        var discarded = await db.WallUpdateHoldDecisions.AsNoTracking()
            .Where(d => d.SessionId == sessionId && d.Kind == WallUpdateHoldDecisionKind.NewCentreHold && d.Discarded)
            .Select(d => d.HoldId)
            .ToListAsync(ct);
        var removed = await db.WallUpdateNeighbourDecisions.AsNoTracking()
            .Where(d => d.SessionId == sessionId && d.Kind == WallUpdateNeighbourDecisionKind.Removed)
            .Select(d => d.HoldId)
            .ToListAsync(ct);
        var existing = await db.WallUpdateShapeProposals.AsNoTracking()
            .Where(p => p.SessionId == sessionId)
            .Select(p => p.HoldId)
            .ToListAsync(ct);
        return discarded.Concat(removed).Concat(existing).ToHashSet();
    }
}
