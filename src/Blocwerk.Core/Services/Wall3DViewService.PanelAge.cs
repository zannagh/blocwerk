// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>How old the active 3D model's photos are next to the wall's live panel photos.</summary>
public sealed partial class Wall3DViewService
{
    /// <summary>
    /// The live panels (per cell the newest with a photo) that were added after the active model was made. Compared in
    /// memory because SQLite cannot compare a DateTimeOffset in SQL.
    /// </summary>
    private async Task<int> NewerPanelCountAsync(Guid wallId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var models = await db.WallGeometryModels.AsNoTracking()
            .Where(m => m.WallId == wallId && m.IsActive).Select(m => m.CreatedAt).ToListAsync(ct);
        if (models.Count == 0)
        {
            return 0;
        }

        var modelAt = models.Max();
        var live = await LiveWallHolds.LoadPanelIdsAsync(db, wallId, ct);
        var created = await db.WallPanels.AsNoTracking().Where(p => live.Contains(p.Id)).Select(p => p.CreatedAt).ToListAsync(ct);
        return created.Count(at => at > modelAt);
    }
}
