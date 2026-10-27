// <copyright file="UpdateReviewService.Facts.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Refresh.Review;

/// <summary>Reading the session's facts for the review: decisions, cards, detections, old holds, boulders, answers.</summary>
public sealed partial class UpdateReviewService
{
    /// <summary>A staged photo never changes for its panel row, so its size is read once per process.</summary>
    private static readonly ConcurrentDictionary<Guid, (int Width, int Height)> StagedSizes = new();

    private async Task<UpdateReviewFacts?> ReadFactsAsync(BlocwerkDbContext db, Guid wallId, Guid sessionId, IReadOnlyList<MovedHoldLine>? moves)
    {
        var stagedGen = await db.Walls.IgnoreQueryFilters().Where(w => w.Id == wallId).Select(w => w.CurrentGeneration + 1).FirstAsync();
        var panels = await StagedPanelsAsync(db, wallId, stagedGen);
        if (panels.FirstOrDefault(p => p is { Col: 0, Row: 0 }) is not { } centre)
        {
            return null;
        }

        var cards = await db.WallUpdateExceptions.AsNoTracking().Where(e => e.SessionId == sessionId).ToListAsync();
        var rows = await db.WallUpdateHoldDecisions.AsNoTracking().Where(d => d.SessionId == sessionId).ToListAsync();
        var neighbour = await db.WallUpdateNeighbourDecisions.AsNoTracking().Where(d => d.SessionId == sessionId).ToListAsync();
        var answers = await db.WallUpdateReviewAnswers.AsNoTracking().Where(a => a.SessionId == sessionId).ToListAsync();
        var relocations = await sessions.GetRelocationSuggestionsAsync(wallId);
        var carry = rows.Where(r => r.Kind == WallUpdateHoldDecisionKind.Carry)
            .ToDictionary(r => r.HoldId, r => new CarryFact(r.CarryKind, r.PairedHoldId, r.Confirmed, r.ConfirmedByUserId));
        var oldIds = carry.Keys.Concat(cards.Select(c => c.OldHoldId).OfType<Guid>()).Concat(relocations.Select(r => r.OldHoldId))
            .Concat((moves ?? []).Select(m => m.OldHoldId)).ToHashSet();
        var users = answers.Select(a => a.ByUserId).Concat(cards.Select(c => c.DecidedByUserId)).Concat(carry.Values.Select(c => c.ConfirmedByUserId));
        return new UpdateReviewFacts(
            panels,
            centre.PanelId,
            cards,
            carry,
            rows.Where(r => r.Kind == WallUpdateHoldDecisionKind.NewCentreHold).GroupBy(r => r.HoldId).ToDictionary(g => g.Key, g => g.First().Discarded),
            neighbour.Where(n => n.Kind == WallUpdateNeighbourDecisionKind.Removed).Select(n => n.HoldId).ToHashSet(),
            neighbour.Where(n => n.Kind is WallUpdateNeighbourDecisionKind.Link or WallUpdateNeighbourDecisionKind.UnsureLink).Select(n => n.HoldId).ToHashSet(),
            await StagedHoldsAsync(db, wallId, stagedGen),
            await OldHoldsAsync(db, wallId, oldIds, panels),
            await BouldersAsync(db, oldIds),
            answers.GroupBy(a => (a.HoldId, a.Subject)).ToDictionary(g => g.Key, g => g.First()),
            relocations,
            await PhotoSizesAsync(db, panels),
            await UserNamesAsync(db, users),
            matchCache is null ? null : (await matchCache.PeekAsync(db, wallId))?.CarriedWarpPositions,
            moves);
    }

    private static async Task<List<ReviewPanel>> StagedPanelsAsync(BlocwerkDbContext db, Guid wallId, int stagedGen)
    {
        var rows = await db.WallPanels.AsNoTracking()
            .Where(p => p.WallId == wallId && p.Generation == stagedGen && p.StagedPhoto != null)
            .Select(p => new { p.Id, p.Col, p.Row })
            .ToListAsync();
        return rows
            .OrderBy(p => Math.Abs(p.Col) + Math.Abs(p.Row))
            .ThenBy(p => p.Row)
            .ThenBy(p => p.Col)
            .Select(p => new ReviewPanel(p.Id, p.Col, p.Row, PanelPositionName.Describe(p.Col, p.Row)))
            .ToList();
    }

    private static async Task<List<StagedFact>> StagedHoldsAsync(BlocwerkDbContext db, Guid wallId, int stagedGen)
    {
        var holds = await db.Holds.AsNoTracking()
            .Where(h => h.WallId == wallId && h.Generation == stagedGen && h.WallPanelId != null)
            .Select(h => new { h.Id, PanelId = h.WallPanelId!.Value, h.X, h.Y, h.Radius, h.Confidence, h.DiscardReason, h.ShapePoints, h.IsVirtual })
            .ToListAsync();
        return holds.Select(h => new StagedFact(h.Id, h.PanelId, h.X, h.Y, h.Radius, h.Confidence, h.DiscardReason, h.ShapePoints, h.IsVirtual)).ToList();
    }

    /// <summary>The old holds with their names and the staged panel at their grid position (a legacy hold without one is the centre's).</summary>
    private static async Task<Dictionary<Guid, OldHoldFact>> OldHoldsAsync(
        BlocwerkDbContext db, Guid wallId, HashSet<Guid> ids, IReadOnlyList<ReviewPanel> staged)
    {
        var holds = await db.Holds.AsNoTracking().Where(h => ids.Contains(h.Id))
            .Select(h => new { h.Id, h.Name, h.Color, h.WallPanelId })
            .ToListAsync();
        var positions = await db.WallPanels.AsNoTracking().Where(p => p.WallId == wallId)
            .Select(p => new { p.Id, p.Col, p.Row })
            .ToDictionaryAsync(p => p.Id, p => (p.Col, p.Row));
        var byPosition = staged.ToDictionary(p => (p.Col, p.Row), p => p.PanelId);
        return holds.ToDictionary(
            h => h.Id,
            h =>
            {
                var at = h.WallPanelId is { } panel && positions.TryGetValue(panel, out var p) ? p : (0, 0);
                var name = string.IsNullOrWhiteSpace(h.Name) ? h.Color : h.Name;
                return new OldHoldFact(h.Id, name, byPosition.TryGetValue(at, out var s) ? s : null, at == (0, 0));
            });
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<string>>> BouldersAsync(BlocwerkDbContext db, HashSet<Guid> oldIds)
    {
        var rows = await db.BoulderHolds.AsNoTracking()
            .Where(bh => oldIds.Contains(bh.HoldId) && !bh.Boulder.IsHistoric && !bh.Boulder.IsArchived)
            .Select(bh => new { bh.HoldId, bh.Boulder.Name })
            .ToListAsync();
        return rows.GroupBy(r => r.HoldId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => r.Name).Distinct().Order().ToList());
    }

    private static async Task<Dictionary<Guid, (int Width, int Height)>> PhotoSizesAsync(BlocwerkDbContext db, IReadOnlyList<ReviewPanel> panels)
    {
        foreach (var panel in panels.Where(p => !StagedSizes.ContainsKey(p.PanelId)))
        {
            var photo = await db.WallPanels.AsNoTracking().Where(p => p.Id == panel.PanelId).Select(p => p.StagedPhoto).FirstOrDefaultAsync();
            if (photo is not null && OverlapSeedLoader.RawSize(photo) is { } size)
            {
                StagedSizes[panel.PanelId] = size;
            }
        }

        return panels.Where(p => StagedSizes.ContainsKey(p.PanelId)).ToDictionary(p => p.PanelId, p => StagedSizes[p.PanelId]);
    }

    private static async Task<Dictionary<Guid, string>> UserNamesAsync(BlocwerkDbContext db, IEnumerable<Guid?> ids)
    {
        var wanted = ids.OfType<Guid>().Distinct().ToList();
        var users = await db.Users.AsNoTracking().IgnoreQueryFilters().Where(u => wanted.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.CustomDisplayName })
            .ToListAsync();
        return users.ToDictionary(u => u.Id, u => string.IsNullOrWhiteSpace(u.CustomDisplayName) ? u.DisplayName : u.CustomDisplayName);
    }
}
