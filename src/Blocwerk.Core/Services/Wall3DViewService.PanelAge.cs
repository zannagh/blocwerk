// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>How old the active 3D model's photos are next to the wall's live panel photos.</summary>
public sealed partial class Wall3DViewService
{
    /// <summary>
    /// Panels staged within this long after the capture's start still count as that visit's photos: a refresh stages its
    /// panels from the same drop that opens the capture.
    /// </summary>
    internal static readonly TimeSpan SameVisit = TimeSpan.FromHours(12);

    private const int MaxLineageDepth = 50;

    /// <summary>
    /// The live panels (per cell the newest with a photo) added clearly after the photos behind the active model. That date
    /// is the model's source capture, followed through corrections (<c>DerivedFromModelId</c>) to the original capture, so a
    /// correction's new model row does not make old photos look new. Compared in memory because SQLite cannot compare a
    /// DateTimeOffset in SQL.
    /// </summary>
    private async Task<int> NewerPanelCountAsync(Guid wallId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var active = await db.WallGeometryModels.AsNoTracking()
            .Where(m => m.WallId == wallId && m.IsActive).Select(m => new { m.Id, m.CreatedAt, m.DerivedFromModelId }).ToListAsync(ct);
        if (active.Count == 0)
        {
            return 0;
        }

        var photosAt = await PhotosDateAsync(db, wallId, active.OrderByDescending(m => m.CreatedAt).First().Id, ct);
        var live = await LiveWallHolds.LoadPanelIdsAsync(db, wallId, ct);
        var created = await db.WallPanels.AsNoTracking().Where(p => live.Contains(p.Id)).Select(p => p.CreatedAt).ToListAsync(ct);
        return created.Count(at => at > photosAt + SameVisit);
    }

    /// <summary>The start of the capture the model (or the model it was corrected from) was solved from; else the model's own date.</summary>
    private static async Task<DateTimeOffset> PhotosDateAsync(BlocwerkDbContext db, Guid wallId, Guid modelId, CancellationToken ct)
    {
        var models = await db.WallGeometryModels.AsNoTracking().Where(m => m.WallId == wallId)
            .Select(m => new { m.Id, m.CreatedAt, m.DerivedFromModelId }).ToListAsync(ct);
        var byId = models.ToDictionary(m => m.Id);
        var captures = await db.WallCaptures.AsNoTracking().Where(c => c.WallId == wallId && c.GeometryModelId != null)
            .Select(c => new { ModelId = c.GeometryModelId!.Value, c.CreatedAt }).ToListAsync(ct);

        var current = modelId;
        var date = byId.TryGetValue(modelId, out var start) ? start.CreatedAt : DateTimeOffset.UtcNow;
        for (var depth = 0; depth < MaxLineageDepth && byId.TryGetValue(current, out var model); depth++)
        {
            date = model.CreatedAt;
            if (captures.Where(c => c.ModelId == current).Select(c => (DateTimeOffset?)c.CreatedAt).Min() is { } captureAt)
            {
                return captureAt;
            }

            if (model.DerivedFromModelId is not { } parent)
            {
                break;
            }

            current = parent;
        }

        return date;
    }
}
