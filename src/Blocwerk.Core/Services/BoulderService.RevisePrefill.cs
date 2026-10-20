// <copyright file="BoulderService.RevisePrefill.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>The Revise editor's pre-selection, translated through hold lineage.</summary>
public partial interface IBoulderService
{
    /// <summary>
    /// The current holds the Revise editor opens with ticked for this boulder: its own holds carried by
    /// identity, else followed forward through the wall's hold lineage, else (only for a hold with no lineage)
    /// guessed on the same panel position. See <see cref="RevisePrefillMapper"/>.
    /// </summary>
    /// <param name="boulderId">The boulder.</param>
    /// <param name="liveHolds">The current holds the editor offers (the wall's live set).</param>
    /// <param name="livePanels">The wall's live panels.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The pre-selection, or null when the boulder is not readable.</returns>
    Task<RevisePrefill?> GetRevisePrefillAsync(
        Guid boulderId,
        IReadOnlyCollection<Hold> liveHolds,
        IReadOnlyCollection<WallPanelInfo> livePanels,
        CancellationToken ct = default);
}

/// <summary>Loads what <see cref="RevisePrefillMapper"/> needs in three reads.</summary>
public partial class BoulderService
{
    /// <inheritdoc />
    public async Task<RevisePrefill?> GetRevisePrefillAsync(
        Guid boulderId,
        IReadOnlyCollection<Hold> liveHolds,
        IReadOnlyCollection<WallPanelInfo> livePanels,
        CancellationToken ct = default)
    {
        using var op = BlocwerkMetrics.TimeOperation("Boulder.GetRevisePrefill");
        try
        {
            var viewerId = await ResolveViewerIdAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            db.CurrentUserId = viewerId;

            // Boulder has no query filter of its own; the reach-through to db.Walls applies the wall's.
            var gated = db.Boulders.Where(b => b.Id == boulderId && db.Walls.Any(w => w.Id == b.WallId));
            if (await gated.Select(b => (Guid?)b.WallId).FirstOrDefaultAsync(ct) is not { } wallId)
            {
                return null;
            }

            var marks = await (
                from b in gated
                from bh in b.BoulderHolds
                select new ReviseMark(bh.HoldId, bh.Type, bh.Usage, bh.Hold.WallPanelId, bh.Hold.X, bh.Hold.Y))
                .ToListAsync(ct);

            // The whole wall's lineage, tombstones included, walked in memory: a wall's link count is in the low
            // thousands, and neither provider translates a recursive walk portably (see LoadPredecessorMapAsync).
            var lineage = await db.HoldGenerationLinks
                .AsNoTracking()
                .Where(l => l.WallId == wallId)
                .Select(l => new ReviseLineageStep(l.OldHoldId, l.NewHoldId, l.Kind, l.FromGeneration, l.ToGeneration))
                .ToListAsync(ct);

            var positions = (await db.WallPanels
                    .AsNoTracking()
                    .Where(p => p.WallId == wallId)
                    .Select(p => new { p.Id, p.Col, p.Row })
                    .ToListAsync(ct))
                .ToDictionary(p => p.Id, p => (p.Col, p.Row));
            foreach (var panel in livePanels)
            {
                positions[panel.Id] = (panel.Col, panel.Row);
            }

            return RevisePrefillMapper.Map(marks, lineage, liveHolds, positions, livePanels.Select(p => p.Id).ToList());
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }
}
