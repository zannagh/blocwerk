// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture;

/// <summary>Routes and lookups of the photo-real (Gaussian splat) scene of a wall's geometry model.</summary>
public static class WallGeometrySplats
{
    /// <summary>Query value of the byte route that selects the mobile level of detail.</summary>
    public const string MobileLod = "mobile";

    /// <summary>
    /// The splat byte route (served by the web layer under the wall-media policy); with
    /// <paramref name="mobile"/> the pruned level of detail for phones (<see cref="WallGeometrySplat.MobileStoredPath"/>).
    /// </summary>
    public static string Url(Guid wallId, Guid modelId, string? shareToken = null, bool mobile = false)
    {
        var query = new List<string>(2);
        if (!string.IsNullOrEmpty(shareToken))
        {
            query.Add($"token={Uri.EscapeDataString(shareToken)}");
        }

        if (mobile)
        {
            query.Add($"lod={MobileLod}");
        }

        var suffix = query.Count == 0 ? string.Empty : "?" + string.Join('&', query);
        return $"/api/walls/{wallId}/geometry/{modelId}/splat{suffix}";
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
