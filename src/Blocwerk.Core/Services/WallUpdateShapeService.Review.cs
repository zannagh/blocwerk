// <copyright file="WallUpdateShapeService.Review.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>The review half: reading the proposals and recording verdicts. Writes only proposal rows.</summary>
public sealed partial class WallUpdateShapeService
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<ShapeProposalInfo>> GetProposalsAsync(
        Guid wallId, double? belowConfidence = null, CancellationToken ct = default)
    {
        var (db, _, session) = await OpenAsync(wallId, null, ct);
        await using (db)
        {
            var query = db.WallUpdateShapeProposals.AsNoTracking().Where(p => p.SessionId == session.Id);
            if (belowConfidence is { } max)
            {
                query = query.Where(p => p.Confidence < max);
            }

            var rows = await query
                .Select(p => new { Proposal = p, p.Hold.Radius })
                .ToListAsync(ct);
            var panelIds = rows.Select(r => r.Proposal.PanelId).Distinct().ToList();
            var panels = await db.WallPanels.AsNoTracking()
                .Where(p => panelIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => (p.Col, p.Row), ct);

            return rows
                .OrderBy(r => r.Proposal.Confidence)
                .ThenBy(r => r.Proposal.HoldId)
                .Select(r => Describe(r.Proposal, r.Radius, panels.GetValueOrDefault(r.Proposal.PanelId)))
                .ToList();
        }
    }

    /// <inheritdoc/>
    public async Task<int> DecideAsync(
        Guid wallId, IReadOnlyList<ShapeDecisionRequest> decisions, Guid? expectedSessionId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        var (db, userId, session) = await OpenAsync(wallId, expectedSessionId, ct);
        await using (db)
        {
            EnsureReviewable(session);
            var ids = decisions.Select(d => d.HoldId).Distinct().ToList();
            var rows = await db.WallUpdateShapeProposals
                .Where(p => p.SessionId == session.Id && ids.Contains(p.HoldId))
                .ToDictionaryAsync(p => p.HoldId, ct);
            var unknown = ids.Where(id => !rows.ContainsKey(id)).ToList();
            if (unknown.Count > 0)
            {
                throw new ArgumentException($"No recognised shape for hold(s) {string.Join(", ", unknown)} in this update.");
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var decision in decisions)
            {
                Record(rows[decision.HoldId], decision, userId, now);
            }

            WallUpdateSessions.MovePhase(session, WallUpdatePhase.ShapeReview, 0, userId);
            await db.SaveChangesAsync(ct);
            return decisions.Count;
        }
    }

    /// <inheritdoc/>
    public async Task<int> AcceptAboveAsync(
        Guid wallId, double minConfidence, Guid? expectedSessionId = null, CancellationToken ct = default)
    {
        var (db, userId, session) = await OpenAsync(wallId, expectedSessionId, ct);
        await using (db)
        {
            EnsureReviewable(session);

            // Only still-pending proposals with a real outline: a bulk accept never overrides a verdict a
            // person already gave, and "accepting" a circle fallback would mean nothing.
            var rows = await db.WallUpdateShapeProposals
                .Where(p => p.SessionId == session.Id && p.Decision == ShapeReviewDecision.Pending
                    && p.ShapeJson != null && p.Confidence >= minConfidence)
                .ToListAsync(ct);
            var now = DateTimeOffset.UtcNow;
            foreach (var row in rows)
            {
                Record(row, new ShapeDecisionRequest(row.HoldId, ShapeReviewDecision.Accepted), userId, now);
            }

            WallUpdateSessions.MovePhase(session, WallUpdatePhase.ShapeReview, 0, userId);
            await db.SaveChangesAsync(ct);
            return rows.Count;
        }
    }

    private static void EnsureReviewable(WallUpdateSession session)
    {
        if (session.ShapeStatus != ShapeRecognitionStatus.Completed)
        {
            throw new InvalidOperationException(
                $"The shape recognition is {session.ShapeStatus}; shapes can only be reviewed once it has completed.");
        }
    }

    private static void Record(WallUpdateShapeProposal row, ShapeDecisionRequest decision, Guid userId, DateTimeOffset now)
    {
        if (decision.Decision == ShapeReviewDecision.Adjusted && !ShapeJson.IsPlausible(decision.Shape))
        {
            throw new ArgumentException(
                $"An adjusted shape for hold {decision.HoldId} needs 3–256 points, each within 0.5 of the hold centre.");
        }

        row.Decision = decision.Decision;
        row.AdjustedShapeJson = decision.Decision == ShapeReviewDecision.Adjusted ? ShapeJson.Write(decision.Shape) : null;
        row.DecidedByUserId = decision.Decision == ShapeReviewDecision.Pending ? null : userId;
        row.DecidedAt = decision.Decision == ShapeReviewDecision.Pending ? null : now;
    }

    private static ShapeProposalInfo Describe(WallUpdateShapeProposal p, double radius, (int Col, int Row) panel) =>
        new(
            p.HoldId,
            p.PanelId,
            panel.Col,
            panel.Row,
            p.AnchorX,
            p.AnchorY,
            radius,
            p.Reason,
            p.Method,
            p.Confidence,
            p.ImageWidth,
            p.ImageHeight,
            ShapeJson.Read(p.ShapeJson),
            ShapeJson.ReadRings(p.HolesJson),
            ShapeJson.Read(p.PreviousShapeJson),
            p.Decision,
            ShapeJson.Read(p.AdjustedShapeJson));
}
