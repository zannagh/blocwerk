// <copyright file="MarkerRevisionCandidates.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// The plan revisions a photo of a wall may show, for <see cref="MarkerRevisionInference"/>: the newest
/// <see cref="MaxCandidates"/> saved revisions, oldest first. Query filters are bypassed: callers have
/// authorised the wall.
/// </summary>
public static class MarkerRevisionCandidates
{
    /// <summary>Older revisions than this many back are assumed long gone from the wall.</summary>
    public const int MaxCandidates = 12;

    /// <summary>The wall's candidate revisions (empty without a plan); unparseable rows are skipped.</summary>
    public static async Task<IReadOnlyList<RevisionCandidate>> LoadAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct = default)
    {
        var rows = await db.WallMarkerPlans.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.WallId == wallId)
            .OrderByDescending(p => p.Revision)
            .ThenByDescending(p => p.CreatedAt)
            .Select(p => new { p.Revision, p.Json, p.EffectiveFrom })
            .Take(MaxCandidates * 2)
            .ToListAsync(ct);
        var candidates = new List<RevisionCandidate>();
        foreach (var row in rows.DistinctBy(r => r.Revision).Take(MaxCandidates))
        {
            if (MarkerPlanJson.FromJson(row.Json, out _) is { } plan)
            {
                candidates.Add(new RevisionCandidate(row.Revision, plan, row.EffectiveFrom));
            }
        }

        return candidates.OrderBy(c => c.Revision).ToList();
    }

    /// <summary>Every id any candidate plans: what detection must accept so an old or new sheet is not rejected.</summary>
    public static IReadOnlySet<int> AllIds(IEnumerable<RevisionCandidate> candidates) =>
        candidates.SelectMany(c => c.Plan.Markers.Select(m => m.Id)).ToHashSet();
}
