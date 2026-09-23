// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture;

/// <summary>Routes and lookups of the photo-real (Gaussian splat) scene of a wall's geometry model.</summary>
public static class WallGeometrySplats
{
    /// <summary>The splat byte route (served by the web layer under the wall-media policy).</summary>
    public static string Url(Guid wallId, Guid modelId, string? shareToken = null)
    {
        var query = string.IsNullOrEmpty(shareToken) ? string.Empty : $"?token={Uri.EscapeDataString(shareToken)}";
        return $"/api/walls/{wallId}/geometry/{modelId}/splat{query}";
    }

    /// <summary>
    /// The splat row behind the byte route. The CALLER has already passed the wall-view gate for
    /// <paramref name="wallId"/>; this only enforces that model and wall belong together.
    /// </summary>
    public static Task<WallGeometrySplat?> FindAsync(BlocwerkDbContext db, Guid wallId, Guid modelId, CancellationToken ct) =>
        db.WallGeometrySplats.AsNoTracking()
            .Where(s => s.GeometryModelId == modelId && s.GeometryModel.WallId == wallId)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The splat of the wall's ACTIVE model, or null. Like <see cref="FindAsync"/> this assumes the
    /// caller already established that the viewer may see the wall.
    /// </summary>
    public static Task<WallGeometrySplat?> FindActiveAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct) =>
        db.WallGeometrySplats.AsNoTracking()
            .Where(s => s.GeometryModel.WallId == wallId && s.GeometryModel.IsActive)
            .FirstOrDefaultAsync(ct);
}
