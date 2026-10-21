// <copyright file="BoulderService.RevisePrefill.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
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

            var splits = await LoadSplitPartsAsync(db, wallId, ct);
            return RevisePrefillMapper.Map(marks, lineage, liveHolds, positions, livePanels.Select(p => p.Id).ToList(), splits);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    /// <summary>
    /// The split tool's cuts on this wall, from the journal (a split part deliberately gets no lineage row): per
    /// recorded split batch, the hold whose outline was cut (the Hold update that changed ShapePoints, as opposed to
    /// a hold merely re-stacked onto the part) -&gt; the parts inserted. Undone splits are not Recorded and drop out.
    /// </summary>
    private static async Task<Dictionary<Guid, IReadOnlyList<Guid>>> LoadSplitPartsAsync(
        BlocwerkDbContext db,
        Guid wallId,
        CancellationToken ct)
    {
        var entries = await db.ChangeJournalEntries
            .AsNoTracking()
            .Where(e => e.EntityType == nameof(Hold)
                && (e.Op == ChangeJournalOp.Insert || e.Op == ChangeJournalOp.Update)
                && db.ChangeJournalBatches.Any(b => b.Id == e.BatchId
                    && b.Label == HoldMergeSplitService.SplitBatchLabel
                    && b.ScopeId == wallId
                    && b.Status == ChangeJournalStatus.Recorded))
            .Select(e => new { e.BatchId, e.Op, e.KeyJson, e.AfterJson })
            .ToListAsync(ct);

        var result = new Dictionary<Guid, IReadOnlyList<Guid>>();
        foreach (var batch in entries.GroupBy(e => e.BatchId))
        {
            var parts = batch.Where(e => e.Op == ChangeJournalOp.Insert).SelectMany(e => GuidsIn(e.KeyJson)).ToList();
            var cut = batch.Where(e => e.Op == ChangeJournalOp.Update && e.AfterJson?.Contains(nameof(Hold.ShapePoints)) == true);
            foreach (var original in cut.SelectMany(e => GuidsIn(e.KeyJson)))
            {
                result[original] = result.TryGetValue(original, out var before) ? [.. before, .. parts] : parts;
            }
        }

        return result;
    }

    private static IEnumerable<Guid> GuidsIn(string json) =>
        GuidInText().Matches(json).Select(m => Guid.Parse(m.Value));

    [System.Text.RegularExpressions.GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial System.Text.RegularExpressions.Regex GuidInText();
}
