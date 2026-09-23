// <copyright file="WallMarkerLayoutResolver.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// Finds a wall's effective <see cref="WallMarkerLayout"/>: its current marker plan when it has a
/// valid one, else the legacy convention with the wall's <c>MarkerSizeMm</c>. The caller has already
/// authorised access to the wall (it holds the wall row, or runs as the pipeline), so query filters
/// are bypassed here — as the capture pipeline does for the wall's marker size.
/// </summary>
public static class WallMarkerLayoutResolver
{
    /// <summary>The layout of <paramref name="wallId"/>.</summary>
    public static async Task<WallMarkerLayout> ResolveAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct = default)
    {
        var size = await db.Walls.IgnoreQueryFilters()
            .Where(w => w.Id == wallId)
            .Select(w => w.MarkerSizeMm)
            .FirstOrDefaultAsync(ct);
        return Resolve(await CurrentPlanJsonAsync(db, wallId, ct), size);
    }

    /// <summary>The wall's current plan JSON as stored, or null.</summary>
    public static Task<string?> CurrentPlanJsonAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct = default) =>
        db.WallMarkerPlans.IgnoreQueryFilters()
            .Where(p => p.WallId == wallId && p.IsCurrent)
            .Select(p => p.Json)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The layout of a stored plan JSON (a wall's, or a capture's snapshot); the legacy convention with
    /// <paramref name="legacyMarkerSizeMm"/> when there is none or it no longer parses.
    /// </summary>
    public static WallMarkerLayout Resolve(string? planJson, double? legacyMarkerSizeMm)
    {
        var plan = string.IsNullOrWhiteSpace(planJson) ? null : MarkerPlanJson.FromJson(planJson, out _);
        return plan is { Markers.Count: > 0 } ? WallMarkerLayout.FromPlan(plan) : WallMarkerLayout.Legacy(legacyMarkerSizeMm);
    }
}
