// <copyright file="HoldProposalRetirement.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
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
    /// <remarks>
    /// The merge matches spots only within one model, so every pending proposal of another (superseded) model is retired:
    /// left pending it would be a twin of the new pass's row on the same hold. Where the two models share a frame
    /// (<paramref name="sharesFrame"/>) a same-spot new row is its survivor, so review items follow it; elsewhere world
    /// coordinates of two models mean nothing to each other, and it is retired without a survivor.
    /// </remarks>
    /// <param name="pending">The wall's pending proposals after the merge (the new rows included).</param>
    /// <param name="modelId">The model the pass searched.</param>
    /// <param name="sharesFrame">Whether a model's coordinates are in the searched model's frame.</param>
    /// <returns>The retirements.</returns>
    public static List<ProposalRetirement> Plan(IReadOnlyList<HoldProposal> pending, Guid modelId, Func<Guid, bool> sharesFrame)
    {
        var result = new List<ProposalRetirement>();
        var kept = new List<HoldProposal>();
        foreach (var p in pending.Where(p => p.GeometryModelId == modelId).OrderByDescending(p => p.Views).ThenByDescending(p => p.Confidence).ThenBy(p => p.Id))
        {
            var twin = kept.FirstOrDefault(k => Near(k, p));
            if (twin is null)
            {
                kept.Add(p);
            }
            else
            {
                result.Add(new ProposalRetirement(p, twin));
            }
        }

        foreach (var old in pending.Where(p => p.GeometryModelId != modelId))
        {
            var survivor = sharesFrame(old.GeometryModelId) ? kept.FirstOrDefault(k => Near(k, old)) : null;
            result.Add(new ProposalRetirement(old, survivor));
        }

        return result;
    }

    /// <summary>
    /// Whether two models' coordinates are in one frame: both solved from the printed markers of the same, known plan
    /// revision (the markers define the frame). A feature reconstruction has a frame of its own; a model derived by a
    /// correction gets its proposals carried over (re-pointed to it) by the correction itself.
    /// </summary>
    /// <param name="a">One model's frame source and plan revision.</param>
    /// <param name="b">The other's.</param>
    /// <returns>True when they share a frame.</returns>
    public static bool SameFrame((WallGeometryFrameSource Source, int? Plan) a, (WallGeometryFrameSource Source, int? Plan) b) =>
        a.Source == WallGeometryFrameSource.Markers && b.Source == WallGeometryFrameSource.Markers && a.Plan is not null && a.Plan == b.Plan;

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
        var survivors = retirements.Where(r => r.Survivor is not null).Select(r => r.Survivor!.Id).Distinct().ToList();
        var taken = (await db.WallEnhancementItems.AsNoTracking()
                .Where(i => i.Kind == WallEnhancementItemKind.NewHold && i.Decision == WallEnhancementDecision.Pending && i.ReferenceId != null && survivors.Contains(i.ReferenceId.Value))
                .Select(i => new { i.RunId, i.ReferenceId })
                .ToListAsync(ct))
            .Select(i => (i.RunId, i.ReferenceId!.Value))
            .ToHashSet();
        foreach (var item in items)
        {
            var survivor = retirements.First(r => r.Retired.Id == item.ReferenceId).Survivor;
            if (survivor is not null && taken.Add((item.RunId, survivor.Id)))
            {
                item.ReferenceId = survivor.Id;
                continue;
            }

            item.Decision = WallEnhancementDecision.Skipped;
            item.Note = survivor is null
                ? "The new 3D model no longer sees a hold here."
                : "The same spot is already in this review as another possible new hold.";
            item.DecidedAt = now;
        }
    }

    private static bool Near(HoldProposal a, HoldProposal b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2))
        <= Math.Max(HoldProposalService.SameSpotMinMm, 0.5 * Math.Max(a.SizeMm, b.SizeMm));
}
