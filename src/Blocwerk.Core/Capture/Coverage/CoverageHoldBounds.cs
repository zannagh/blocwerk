// <copyright file="CoverageHoldBounds.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using Blocwerk.Core.Data;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>
/// The placed holds' bounds per facet that widen a coverage report's facet regions. A hold's facet position is in the
/// frame of the wall's active model, so only a report on that model uses them: an older capture's model, or a re-solve
/// that moved or renumbered its facets, gets none rather than another model's positions.
/// </summary>
internal static class CoverageHoldBounds
{
    /// <summary>The bounds of the holds placed on each facet; empty when <paramref name="modelId"/> is not its wall's active model.</summary>
    /// <param name="db">The context.</param>
    /// <param name="modelId">The model the report is computed for.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Per facet id, the bounds.</returns>
    public static async Task<IReadOnlyDictionary<string, PlaneRectMm>> LoadAsync(BlocwerkDbContext db, Guid modelId, CancellationToken ct)
    {
        var model = await db.WallGeometryModels.AsNoTracking()
            .Where(m => m.Id == modelId)
            .Select(m => new { m.WallId, m.IsActive })
            .FirstOrDefaultAsync(ct);
        if (model is not { IsActive: true })
        {
            return new Dictionary<string, PlaneRectMm>(StringComparer.Ordinal);
        }

        var live = await LiveWallHolds.QueryAsync(db, model.WallId, ct);
        var points = await live.AsNoTracking()
            .Where(h => h.FacetId != null && h.PlaneAMm != null && h.PlaneBMm != null)
            .Select(h => new { h.FacetId, h.PlaneAMm, h.PlaneBMm })
            .ToListAsync(ct);
        return points.GroupBy(p => p.FacetId!)
            .Select(g => (g.Key, Bounds: PlaneRectMm.Bounds(g.Select(p => (p.PlaneAMm!.Value, p.PlaneBMm!.Value)))))
            .Where(x => x.Bounds is not null)
            .ToDictionary(x => x.Key, x => x.Bounds!.Value, StringComparer.Ordinal);
    }

    /// <summary>The bounds as stable text lines (whole millimetres), for the report's fingerprint.</summary>
    /// <param name="bounds">The bounds.</param>
    /// <returns>One line per facet, ordered.</returns>
    public static IEnumerable<string> Lines(IReadOnlyDictionary<string, PlaneRectMm> bounds) =>
        bounds.Select(kv => string.Create(
                CultureInfo.InvariantCulture,
                $"hold\t{kv.Key}\t{kv.Value.AMin:0}\t{kv.Value.AMax:0}\t{kv.Value.BMin:0}\t{kv.Value.BMax:0}"))
            .Order(StringComparer.Ordinal);
}
