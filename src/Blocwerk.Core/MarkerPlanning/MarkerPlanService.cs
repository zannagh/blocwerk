// <copyright file="MarkerPlanService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// The marker planner. The pure parts (generate, validate, net, JSON, PDF) delegate to their static
/// helpers; persistence follows <see cref="WallGlyphService"/>: reads go through the Wall query filter
/// (anyone who can see the wall), writes need <see cref="WallAdminGuard"/> and are refused from any
/// kiosk (<see cref="KioskGuard"/>) — planning markers is an owner-desk task.
/// </summary>
public sealed class MarkerPlanService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUserService,
    ILogger<MarkerPlanService> logger,
    IKioskContext? kioskContext = null) : IMarkerPlanService
{
    public async Task<MarkerPlan?> GetPlanAsync(Guid wallId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;

        // The Wall query filter is the visibility gate: a wall the user cannot see is "not found".
        if (!await db.Walls.AnyAsync(w => w.Id == wallId))
        {
            throw new InvalidOperationException("Wall not found");
        }

        var json = await db.WallMarkerPlans
            .Where(p => p.WallId == wallId && p.IsCurrent)
            .Select(p => p.Json)
            .FirstOrDefaultAsync();
        if (json is null)
        {
            return null;
        }

        var plan = MarkerPlanJson.FromJson(json, out var errors);
        if (plan is null)
        {
            // Validated on save, so this is a stored row gone bad: behave as "no plan", don't crash.
            logger.LogWarning("Current marker plan of wall {WallId} no longer parses: {Errors}", wallId, string.Join(" ", errors));
        }

        return plan;
    }

    public async Task<MarkerPlanSaveResult> SavePlanAsync(Guid wallId, MarkerPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        KioskGuard.EnsureNotKiosk(kioskContext, db, "Saving a marker plan");
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

        var json = MarkerPlanJson.ToJson(plan);
        if (MarkerPlanJson.FromJson(json, out var shapeErrors) is null)
        {
            var refused = shapeErrors.Select(e => new PlanIssue(PlanIssueSeverity.Error, "plan-shape", e, null, null)).ToList();
            return new MarkerPlanSaveResult(false, refused);
        }

        var issues = MarkerPlanValidator.Validate(plan);
        if (issues.Any(i => i.Severity == PlanIssueSeverity.Error))
        {
            return new MarkerPlanSaveResult(false, issues);
        }

        var row = new WallMarkerPlan
        {
            WallId = wallId,
            Json = json,
            SchemaVersion = plan.SchemaVersion,
            CreatedByUserId = user.Id,
            IsCurrent = true,
        };
        await SwapCurrentAsync(db, wallId, row);
        logger.LogInformation(
            "Wall {WallId} marker plan {PlanId} saved by {UserId} ({Segments} segments, {Markers} markers)",
            wallId, row.Id, user.Id, plan.Segments.Count, plan.Markers.Count);
        return new MarkerPlanSaveResult(true, issues);
    }

    public async Task<MarkerPlan?> BuildFromMeasuredGeometryAsync(Guid wallId, PhotoSetup photo)
    {
        ArgumentNullException.ThrowIfNull(photo);
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        KioskGuard.EnsureNotKiosk(kioskContext, db, "Building a marker plan from the measured wall");
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

        var json = await db.WallGeometryModels
            .Where(m => m.WallId == wallId && m.IsActive)
            .Select(m => m.Json)
            .FirstOrDefaultAsync();
        if (json is null)
        {
            return null;
        }

        try
        {
            return MarkerPlanFromGeometry.Build(WallGeometryDocument.Parse(json), photo);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Active geometry of wall {WallId} does not parse; no plan built from it", wallId);
            return null;
        }
    }

    public MarkerPlan GenerateMarkers(MarkerPlan plan, MarkerGenerationOptions options) => MarkerGenerator.Generate(plan, options);

    public IReadOnlyList<PlanIssue> Validate(MarkerPlan plan) => MarkerPlanValidator.Validate(plan);

    public NetGeometry ComputeNet(MarkerPlan plan) => NetLayout.Compute(plan).Net;

    public string ToJson(MarkerPlan plan) => MarkerPlanJson.ToJson(plan);

    public MarkerPlan? FromJson(string json, out IReadOnlyList<string> errors) => MarkerPlanJson.FromJson(json, out errors);

    public byte[] RenderPdf(MarkerPlan plan, string wallName) => MarkerPlanPdf.Render(plan, wallName);

    /// <summary>
    /// Retires the current plan and adds the new one in ONE transaction, as two saves: the filtered
    /// unique index is checked per statement and EF orders same-table statements by key, not by the
    /// index filter, so the retirement is flushed first (same pattern as the geometry model swap).
    /// </summary>
    private static async Task SwapCurrentAsync(BlocwerkDbContext db, Guid wallId, WallMarkerPlan row)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var current = await db.WallMarkerPlans.Where(p => p.WallId == wallId && p.IsCurrent).ToListAsync();
        foreach (var previous in current)
        {
            previous.IsCurrent = false;
        }

        await db.SaveChangesAsync();
        db.WallMarkerPlans.Add(row);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }
}
