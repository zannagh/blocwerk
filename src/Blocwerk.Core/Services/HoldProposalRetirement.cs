// <copyright file="HoldProposalRetirement.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry.Proposals;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>A pending proposal a search takes out of the review, and the one that now stands for its spot (if any).</summary>
/// <param name="Retired">The proposal retired.</param>
/// <param name="Survivor">The pending proposal on the same spot, or null when the spot is not proposed any more.</param>
internal sealed record ProposalRetirement(HoldProposal Retired, HoldProposal? Survivor);

/// <summary>
/// Keeping the pending proposals tidy now that a search no longer deletes them: after a model's pass, a pending proposal
/// of a SUPERSEDED model that the new pass does not see on its spot is retired, and of two pending proposals on the same
/// spot the weaker one is retired. A retirement is a status (<see cref="HoldProposalStatus.Retired"/>, with its time), not a
/// delete: nothing is lost and the spot can be proposed again. A wall enhancement's review item that pointed at a
/// retired proposal is re-pointed at the survivor, or marked as no longer proposed — never silently left dangling.
/// </summary>
internal static class HoldProposalRetirement
{
    /// <summary>What to retire after a pass over <paramref name="modelId"/>.</summary>
    /// <param name="pending">The wall's pending proposals after the merge (the new rows included).</param>
    /// <param name="candidates">The pass's candidates.</param>
    /// <param name="modelId">The model the pass searched.</param>
    /// <returns>The retirements.</returns>
    public static List<ProposalRetirement> Plan(IReadOnlyList<HoldProposal> pending, IReadOnlyList<HoldProposalCandidate> candidates, Guid modelId)
    {
        var result = new List<ProposalRetirement>();
        foreach (var old in pending.Where(p => p.GeometryModelId != modelId))
        {
            if (!candidates.Any(c => Near(old.X, old.Y, old.Z, old.SizeMm, c.World[0], c.World[1], c.World[2], c.SizeMm)))
            {
                result.Add(new ProposalRetirement(old, null));
            }
        }

        var kept = new List<HoldProposal>();
        foreach (var p in pending.Where(p => p.GeometryModelId == modelId).OrderByDescending(p => p.Views).ThenByDescending(p => p.Confidence).ThenBy(p => p.Id))
        {
            var twin = kept.FirstOrDefault(k => Near(k.X, k.Y, k.Z, k.SizeMm, p.X, p.Y, p.Z, p.SizeMm));
            if (twin is null)
            {
                kept.Add(p);
            }
            else
            {
                result.Add(new ProposalRetirement(p, twin));
            }
        }

        return result;
    }

    /// <summary>Retires the proposals (tracked rows) and re-points or closes the enhancement review items that named them.</summary>
    /// <param name="db">The context the proposals are tracked in.</param>
    /// <param name="retirements">What to retire.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>When done (saved by the caller).</returns>
    public static async Task ApplyAsync(BlocwerkDbContext db, IReadOnlyList<ProposalRetirement> retirements, CancellationToken ct)
    {
        if (retirements.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var r in retirements)
        {
            r.Retired.Status = HoldProposalStatus.Retired;
            r.Retired.ReviewedAt = now;
            r.Retired.ReviewedByUserId = null;
        }

        var ids = retirements.Select(r => r.Retired.Id).ToList();
        var items = await db.WallEnhancementItems
            .Where(i => i.Kind == WallEnhancementItemKind.NewHold && i.Decision == WallEnhancementDecision.Pending && i.ReferenceId != null && ids.Contains(i.ReferenceId.Value))
            .ToListAsync(ct);
        foreach (var item in items)
        {
            var survivor = retirements.First(r => r.Retired.Id == item.ReferenceId).Survivor;
            if (survivor is not null)
            {
                item.ReferenceId = survivor.Id;
                continue;
            }

            item.Decision = WallEnhancementDecision.Skipped;
            item.Note = "The new 3D model no longer sees a hold here.";
            item.DecidedAt = now;
        }
    }

    private static bool Near(double ax, double ay, double az, double aSize, double bx, double by, double bz, double bSize) =>
        Math.Sqrt(Math.Pow(ax - bx, 2) + Math.Pow(ay - by, 2) + Math.Pow(az - bz, 2))
        <= Math.Max(HoldProposalService.SameSpotMinMm, 0.5 * Math.Max(aSize, bSize));
}
