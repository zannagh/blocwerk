// <copyright file="BoulderService.Moves.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldMoves;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>A hold of a boulder that moved when the panels were updated, as the boulder page lists it.</summary>
/// <param name="OldHoldId">The hold before (may be gone).</param>
/// <param name="NewHoldId">The hold after (may be gone).</param>
/// <param name="HoldName">The hold's name, when it has one.</param>
/// <param name="DistanceMm">How far it moved.</param>
/// <param name="Source">3D or photo estimate.</param>
/// <param name="Outcome">Kept on the boulder or removed from it.</param>
/// <param name="Text">Plain language, for example "moved 6 cm, kept".</param>
/// <param name="Type">The mark the boulder had on it.</param>
public sealed record BoulderMoveView(
    Guid? OldHoldId, Guid? NewHoldId, string? HoldName, double DistanceMm, HoldMoveSource Source, HoldMoveOutcome Outcome, string Text, HoldType Type);

public partial class BoulderService
{
    /// <summary>
    /// The holds of this boulder that moved at the update that put it where it is now, while it still needs review:
    /// removed ones first, then the farthest. Empty once the setter signed the boulder off or when nothing moved.
    /// </summary>
    /// <param name="boulderId">The boulder.</param>
    /// <param name="shareToken">The wall's share token, for the read-only share view.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The moves.</returns>
    public async Task<IReadOnlyList<BoulderMoveView>> GetBoulderMovesAsync(
        Guid boulderId, string? shareToken = null, CancellationToken ct = default)
    {
        var isShare = !string.IsNullOrEmpty(shareToken);
        var viewerId = isShare ? Guid.Empty : await ResolveViewerIdAsync();
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = viewerId;
        var gated = isShare
            ? db.Boulders.Where(b => b.Id == boulderId && b.Wall.ShareToken == shareToken && !b.IsDraft)
            : db.Boulders.Where(b => b.Id == boulderId && db.Walls.Any(w => w.Id == b.WallId));
        var rows = await (
            from b in gated
            where b.NeedsReview
            from m in db.BoulderHoldMoves
            where m.BoulderId == b.Id && m.ToGeneration == b.Generation
            select new
            {
                m.OldHoldId, m.NewHoldId, m.DistanceMm, m.Source, m.RotationDeg, m.Outcome, m.Type,
                OldName = m.OldHold == null ? null : m.OldHold.Name,
                NewName = m.NewHold == null ? null : m.NewHold.Name,
            })
            .AsNoTracking()
            .ToListAsync(ct);
        return rows
            .OrderByDescending(r => r.Outcome)
            .ThenByDescending(r => r.DistanceMm)
            .Select(r => new BoulderMoveView(
                r.OldHoldId, r.NewHoldId, r.NewName ?? r.OldName, r.DistanceMm, r.Source, r.Outcome,
                HoldMovePolicy.Describe(r.DistanceMm, r.RotationDeg, r.Outcome), r.Type))
            .ToList();
    }

    /// <summary>
    /// The memberships a boulder lost to a move, as marks at the generation they belonged to, so the "Then" view still
    /// draws the hold where it was.
    /// </summary>
    private static async Task<List<(Guid HoldId, HoldType Type, HoldUsage Usage, int OwnGeneration)>> LoadRemovedMovesAsync(
        BlocwerkDbContext db, IQueryable<Entities.Boulder> gated, CancellationToken ct)
    {
        var rows = await (
            from b in gated
            from m in db.BoulderHoldMoves
            where m.BoulderId == b.Id && m.Outcome == HoldMoveOutcome.Removed && m.OldHoldId != null
            select new { HoldId = m.OldHoldId!.Value, m.Type, m.Usage, m.FromGeneration })
            .AsNoTracking()
            .ToListAsync(ct);
        return rows.Select(r => (r.HoldId, r.Type, r.Usage, r.FromGeneration)).ToList();
    }
}
