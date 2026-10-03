// <copyright file="ProposalCoverage.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry.Proposals;
using Blocwerk.Core.Geometry.Volumes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Pending hold proposals that a live hold now covers (added or moved there since the search), for hiding them from the
/// list: the hold's stored position on the proposal's facet (its volume point while current, else its flat position) lies
/// within max(<see cref="HoldProposalFinder.MatchMm"/>, half its size) of the proposal. Nothing is persisted: a proposal
/// stays pending and shows again when the hold moves away or is deleted. Only proposals of the active model are compared
/// (another model's facet frames may differ). One narrow query over the live holds; no model parsing.
/// </summary>
internal static class ProposalCoverage
{
    /// <summary>Largest hold size trusted for the tolerance, mm (as <see cref="KnownHoldReferences.MaxSizeMm"/>).</summary>
    private const double MaxSizeMm = KnownHoldReferences.MaxSizeMm;

    /// <summary>The ids of the proposals of <paramref name="pending"/> a live hold covers; empty on any failure.</summary>
    /// <param name="db">The context.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="pending">Its pending proposals.</param>
    /// <param name="logger">Where a failure is logged.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The covered proposal ids.</returns>
    public static async Task<HashSet<Guid>> CoveredAsync(
        BlocwerkDbContext db, Guid wallId, IReadOnlyList<HoldProposal> pending, ILogger logger, CancellationToken ct)
    {
        try
        {
            var modelId = pending.Count == 0
                ? null
                : await db.WallGeometryModels.AsNoTracking().Where(m => m.WallId == wallId && m.IsActive).Select(m => (Guid?)m.Id).FirstOrDefaultAsync(ct);
            var mine = pending.Where(p => p.GeometryModelId == modelId).ToList();
            if (mine.Count == 0)
            {
                return [];
            }

            var spots = (await (await LiveWallHolds.QueryAsync(db, wallId, ct)).AsNoTracking()
                    .Where(h => !h.IsVirtual && h.FacetId != null && h.PlaneAMm != null && h.PlaneBMm != null)
                    .Select(h => new { h.FacetId, h.PlaneAMm, h.PlaneBMm, h.WidthMm, h.HeightMm, h.VolumePlacementJson })
                    .ToListAsync(ct))
                .Select(h => Spot(h.FacetId!, h.PlaneAMm!.Value, h.PlaneBMm!.Value, h.WidthMm, h.HeightMm, h.VolumePlacementJson))
                .ToLookup(s => s.FacetId, StringComparer.Ordinal);
            return mine
                .Where(p => spots[p.FacetId].Any(s => Math.Sqrt(((s.A - p.A) * (s.A - p.A)) + ((s.B - p.B) * (s.B - p.B))) <= s.ToleranceMm))
                .Select(p => p.Id)
                .ToHashSet();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not compare the hold proposals of wall {WallId} with its holds; all are listed", wallId);
            return [];
        }
    }

    /// <summary>
    /// How many of the wall's pending proposals of <paramref name="modelId"/> the review list shows now (those no live hold
    /// covers), for the capture history's "possible new holds" line. Reads only the fields the check needs.
    /// </summary>
    /// <param name="db">The context.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="modelId">The model the proposals were searched on (the active one).</param>
    /// <param name="logger">Where a failure of the coverage check is logged.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The pending, uncovered proposal count.</returns>
    public static async Task<int> ListedCountAsync(BlocwerkDbContext db, Guid wallId, Guid modelId, ILogger logger, CancellationToken ct)
    {
        var pending = await db.HoldProposals.AsNoTracking()
            .Where(p => p.WallId == wallId && p.GeometryModelId == modelId && p.Status == HoldProposalStatus.Pending)
            .Select(p => new HoldProposal
            {
                Id = p.Id, WallId = p.WallId, GeometryModelId = p.GeometryModelId, FacetId = p.FacetId, A = p.A, B = p.B, BestPhoto = string.Empty,
            })
            .ToListAsync(ct);
        return pending.Count == 0 ? 0 : pending.Count - (await CoveredAsync(db, wallId, pending, logger, ct)).Count;
    }

    private static (string FacetId, double A, double B, double ToleranceMm) Spot(
        string facetId, double a, double b, double? widthMm, double? heightMm, string? volumeJson)
    {
        var size = Math.Min(MaxSizeMm, Math.Max(widthMm ?? 0, heightMm ?? 0));
        var tolerance = Math.Max(HoldProposalFinder.MatchMm, size / 2);
        return HoldVolumePlacement.FromJson(volumeJson) is { } v && v.Matches(a, b)
            ? (facetId, v.A, v.B, tolerance)
            : (facetId, a, b, tolerance);
    }
}
