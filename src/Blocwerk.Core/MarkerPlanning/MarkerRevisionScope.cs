// <copyright file="MarkerRevisionScope.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// Keeps photos and the wall model from mixing marker revisions. A photo's markers are as they were
/// when it was taken (its <see cref="WallMarkerObservation.PlanRevision"/>); the active model measured
/// the markers of ITS revision (<see cref="WallGeometryModel.PlanRevision"/>). Only markers unchanged
/// between the two may map the photo onto the model: a moved, resized or replaced marker — even one that
/// kept its id — is dropped, so an old photo is never placed with a marker's new pose.
/// </summary>
public sealed class MarkerRevisionScope
{
    private readonly MarkerBaselines baselines;

    private MarkerRevisionScope(MarkerBaselines store, bool hasModel, int? modelRevision, int? currentRevision)
    {
        baselines = store;
        HasModel = hasModel;
        ModelRevision = modelRevision;
        CurrentRevision = currentRevision;
    }

    /// <summary>True when the wall has an active model.</summary>
    public bool HasModel { get; }

    /// <summary>The plan revision the active model was solved with (null: legacy or unknown).</summary>
    public int? ModelRevision { get; }

    /// <summary>The wall's current plan revision (null: no plan) — what a photo detected now is tagged with.</summary>
    public int? CurrentRevision { get; }

    /// <summary>The revision baselines behind this scope, for comparisons beyond the active model.</summary>
    public MarkerBaselines Baselines => baselines;

    /// <summary>Loads the active model's and the current plan's revisions of <paramref name="wallId"/>.</summary>
    public static async Task<MarkerRevisionScope> LoadAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct = default)
    {
        var model = await db.WallGeometryModels.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.WallId == wallId && m.IsActive)
            .Select(m => new { m.PlanRevision })
            .FirstOrDefaultAsync(ct);
        var current = await MarkerBaselines.CurrentRevisionAsync(db, wallId, ct);
        return new MarkerRevisionScope(new MarkerBaselines(db, wallId), model is not null, model?.PlanRevision, current);
    }

    /// <summary>
    /// The ids a photo of <paramref name="photoRevision"/> may use against the active model; null = all
    /// (same revision, or no model to mix with).
    /// </summary>
    public Task<IReadOnlySet<int>?> UsableIdsAsync(int? photoRevision, CancellationToken ct = default) =>
        HasModel ? baselines.StableIdsAsync(photoRevision, ModelRevision, ct) : Task.FromResult<IReadOnlySet<int>?>(null);

    /// <summary>The markers of a photo of <paramref name="photoRevision"/> that may be mapped with the active model.</summary>
    public async Task<List<DetectedMarker>> FilterAsync(
        IEnumerable<DetectedMarker> markers, int? photoRevision, CancellationToken ct = default)
    {
        var usable = await UsableIdsAsync(photoRevision, ct);
        return usable is null ? markers.ToList() : markers.Where(m => usable.Contains(m.Id)).ToList();
    }

    /// <summary>
    /// A photo's observation rows that may be mapped with the active model. Rows of one photo share a
    /// revision; a mix (never written, but possible in old data) is filtered row by row.
    /// </summary>
    public async Task<List<WallMarkerObservation>> FilterAsync(
        IEnumerable<WallMarkerObservation> rows, CancellationToken ct = default)
    {
        var kept = new List<WallMarkerObservation>();
        foreach (var group in rows.GroupBy(r => r.PlanRevision))
        {
            var usable = await UsableIdsAsync(group.Key, ct);
            kept.AddRange(usable is null ? group : group.Where(r => usable.Contains(r.MarkerId)));
        }

        return kept;
    }
}
