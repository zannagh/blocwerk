// <copyright file="HoldProposalService.Merge.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry.Proposals;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Storing a search's proposals NEXT TO the wall's pending ones. A search used to delete every pending proposal first,
/// which threw away what a 3D runner had found and left a review that pointed at them with nothing to act on. Now a
/// pending proposal is never removed by a search: a candidate on the same spot (same model, within its footprint)
/// updates that proposal in place and keeps its id; anything else is added.
/// </summary>
public sealed partial class HoldProposalService
{
    /// <summary>The smallest distance (mm) within which a candidate counts as the same spot as a pending proposal.</summary>
    public const double SameSpotMinMm = 25;

    /// <summary>
    /// True while a hold search runs somewhere on this server (it holds the one search gate). The queued 3D refinements
    /// wait for it rather than compete for the same CPU (<see cref="HoldRefinementQueue"/>).
    /// </summary>
    internal static bool IsSearching => Gate.CurrentCount == 0;

    /// <summary>Whether the active model already had a proposal pass (a stored proposal, or the capture chain's recorded search).</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when a pass exists and a new search would only repeat it.</returns>
    public async Task<bool> HasProposalPassAsync(Guid wallId, CancellationToken ct = default)
    {
        await EnsureAdminAsync(wallId, ct);
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var modelId = await db.WallGeometryModels.AsNoTracking()
            .Where(m => m.WallId == wallId && m.IsActive).Select(m => (Guid?)m.Id).FirstOrDefaultAsync(ct);
        if (modelId is not { } active)
        {
            return false;
        }

        if (await db.HoldProposals.AnyAsync(p => p.WallId == wallId && p.GeometryModelId == active, ct))
        {
            return true;
        }

        var records = await db.WallCaptures.AsNoTracking()
            .Where(c => c.GeometryModelId == active && c.FollowUpJson != null)
            .Select(c => c.FollowUpJson)
            .ToListAsync(ct);
        return records.Any(json => Capture.FollowUp.CaptureFollowUpRecord.Parse(json).Find(Capture.FollowUp.FindHoldProposalsFollowUpStep.StepKey)
            is { Outcome: Capture.FollowUp.CaptureFollowUpOutcome.Done });
    }

    /// <summary>The pending proposal a candidate is the same spot as, or null.</summary>
    internal static HoldProposal? SameSpot(IEnumerable<HoldProposal> pending, HoldProposalCandidate c, Guid modelId)
    {
        double Distance(HoldProposal p) =>
            Math.Sqrt(Math.Pow(p.X - c.World[0], 2) + Math.Pow(p.Y - c.World[1], 2) + Math.Pow(p.Z - c.World[2], 2));
        return pending
            .Where(p => p.GeometryModelId == modelId && Distance(p) <= Math.Max(SameSpotMinMm, 0.5 * Math.Max(p.SizeMm, c.SizeMm)))
            .MinBy(Distance);
    }

    /// <summary>
    /// The merge itself: a candidate on the spot of a pending proposal updates it in place (keeping its id, so a review
    /// that points at it stays valid); the others become new rows, which are returned. Nothing pending is removed.
    /// </summary>
    /// <param name="pending">The wall's pending proposals (tracked: updated in place).</param>
    /// <param name="candidates">The search's candidates with their panel points.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="modelId">The model searched.</param>
    /// <returns>The new rows to add.</returns>
    internal static List<HoldProposal> Merge(
        IReadOnlyList<HoldProposal> pending, IEnumerable<(HoldProposalCandidate Candidate, PanelPoint? Panel)> candidates, Guid wallId, Guid modelId)
    {
        var free = pending.ToList();
        var added = new List<HoldProposal>();
        foreach (var (c, panel) in candidates)
        {
            var row = SameSpot(free, c, modelId);
            if (row is null)
            {
                row = new HoldProposal { WallId = wallId, GeometryModelId = modelId, FacetId = c.FacetId, BestPhoto = c.Best.Photo };
                added.Add(row);
            }
            else
            {
                free.Remove(row);
            }

            Fill(row, c, panel);
        }

        return added;
    }

    /// <summary>Merges the candidates into the wall's pending proposals; returns how many map onto a panel.</summary>
    private static async Task<int> MergePendingAsync(
        BlocwerkDbContext db, Guid wallId, Guid modelId, List<HoldProposalCandidate> candidates, ProposalInputs inputs, CancellationToken ct)
    {
        var pending = await db.HoldProposals.Where(p => p.WallId == wallId && p.Status == HoldProposalStatus.Pending).ToListAsync(ct);
        var frames = inputs.Facets.ToDictionary(f => f.Id, f => f.Frame, StringComparer.Ordinal);
        var mapped = candidates.Select(c => (c, PanelPointMapper.Map(c.World, c.SizeMm, frames[c.FacetId], c.FacetId, inputs.Panels))).ToList();
        var added = Merge(pending, mapped, wallId, modelId);
        db.HoldProposals.AddRange(added);
        await HoldProposalRetirement.ApplyAsync(db, HoldProposalRetirement.Plan([.. pending, .. added], candidates, modelId), ct);
        await db.SaveChangesAsync(ct);
        return mapped.Count(m => m.Item2 is not null);
    }

    private static void Fill(HoldProposal row, HoldProposalCandidate c, PanelPoint? panel)
    {
        (row.FacetId, row.A, row.B, row.H) = (c.FacetId, c.A, c.B, c.H);
        (row.X, row.Y, row.Z) = (Math.Round(c.World[0], 1), Math.Round(c.World[1], 1), Math.Round(c.World[2], 1));
        (row.SizeMm, row.Views, row.Confidence) = (c.SizeMm, c.Views, c.Confidence);
        (row.BestPhoto, row.BestPx, row.BestPy, row.BestRadiusPx) =
            (c.Best.Photo, Math.Round(c.Best.Px, 1), Math.Round(c.Best.Py, 1), Math.Round(c.Best.RadiusPx, 1));
        (row.PanelId, row.PanelX, row.PanelY, row.PanelRadius) = (panel?.PanelId, panel?.X, panel?.Y, panel?.Radius);
    }
}
