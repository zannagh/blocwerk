// <copyright file="MarkerPlanService.Revisions.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>The plan's revision history and "what changed since the last capture".</summary>
public sealed partial class MarkerPlanService
{
    public async Task<IReadOnlyList<MarkerPlanRevisionInfo>> GetRevisionsAsync(Guid wallId)
    {
        await using var db = await OpenAdminReadAsync(wallId, "Reading the marker plan history");
        var modelRevision = await ActiveModelRevisionAsync(db, wallId);
        var rows = await db.WallMarkerPlans.AsNoTracking()
            .Where(p => p.WallId == wallId)
            .OrderByDescending(p => p.Revision)
            .ThenByDescending(p => p.CreatedAt)
            .Select(p => new { p.Revision, p.CreatedAt, p.CreatedByUserId, p.IsCurrent, p.Json })
            .ToListAsync();
        var userIds = rows.Where(r => r.CreatedByUserId is not null).Select(r => r.CreatedByUserId!.Value).Distinct().ToList();
        var names = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Name);
        return rows
            .Select(r => new MarkerPlanRevisionInfo(
                r.Revision,
                r.CreatedAt,
                r.CreatedByUserId is { } id && names.TryGetValue(id, out var name) ? name : null,
                MarkerPlanJson.FromJson(r.Json, out _)?.Markers.Count ?? 0,
                r.IsCurrent,
                modelRevision.HasModel && modelRevision.Revision == r.Revision))
            .ToList();
    }

    public async Task<MarkerPlan?> GetRevisionAsync(Guid wallId, int revision)
    {
        await using var db = await OpenAdminReadAsync(wallId, "Reading a marker plan revision");
        var json = await db.WallMarkerPlans.AsNoTracking()
            .Where(p => p.WallId == wallId && p.Revision == revision)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => p.Json)
            .FirstOrDefaultAsync();
        return json is null ? null : MarkerPlanJson.FromJson(json, out _);
    }

    public async Task<MarkerPlanChanges?> GetChangesSinceLastCaptureAsync(Guid wallId, MarkerPlan? plan = null)
    {
        var baseline = await GetCaptureBaselineAsync(wallId);
        if (baseline is null)
        {
            return null;
        }

        IReadOnlyList<PlanMarker>? after = plan?.Markers;
        if (after is null)
        {
            await using var db = await dbContextFactory.CreateDbContextAsync();
            after = await CurrentMarkersAsync(db, wallId);
        }

        return after is null ? null : baseline.CompareWith(after);
    }

    public async Task<MarkerCaptureBaseline?> GetCaptureBaselineAsync(Guid wallId)
    {
        await using var db = await OpenAdminReadAsync(wallId, "Comparing marker plan revisions");
        var (hasModel, modelRevision) = await ActiveModelRevisionAsync(db, wallId);
        if (!hasModel)
        {
            return null;
        }

        var markers = await new MarkerBaselines(db, wallId).MarkersAsync(modelRevision);
        return markers is null ? null : new MarkerCaptureBaseline(modelRevision, markers);
    }

    private static async Task<IReadOnlyList<PlanMarker>?> CurrentMarkersAsync(BlocwerkDbContext db, Guid wallId)
    {
        var json = await WallMarkerLayoutResolver.CurrentPlanJsonAsync(db, wallId);
        return json is null ? null : MarkerPlanJson.FromJson(json, out _)?.Markers;
    }

    private static async Task<(bool HasModel, int? Revision)> ActiveModelRevisionAsync(BlocwerkDbContext db, Guid wallId)
    {
        var model = await db.WallGeometryModels.AsNoTracking()
            .Where(m => m.WallId == wallId && m.IsActive)
            .Select(m => new { m.PlanRevision })
            .FirstOrDefaultAsync();
        return (model is not null, model?.PlanRevision);
    }

    /// <summary>A context for an admin-only read: the history belongs to the owner's desk, like saving.</summary>
    private async Task<BlocwerkDbContext> OpenAdminReadAsync(Guid wallId, string action)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        var db = await dbContextFactory.CreateDbContextAsync();
        try
        {
            db.CurrentUserId = user.Id;
            KioskGuard.EnsureNotKiosk(kioskContext, db, action);
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);
            return db;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }
}
