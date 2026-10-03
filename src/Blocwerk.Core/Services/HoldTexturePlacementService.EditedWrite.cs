// <copyright file="HoldTexturePlacementService.EditedWrite.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.TextureRegistration;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Writing the edited holds' placements, never over a newer 2D edit: in one transaction each hold is first claimed with a
/// conditional no-op update (its panel geometry and metric fields still as read; the row stays locked until commit, so a
/// concurrent edit waits and then wins), and all writes go into ONE rolling <see cref="HoldPlacementTrigger.Edit"/> run per
/// model, which keeps one entry per hold (the latest), so reverting it or a wall-wide run composes: each restores only the
/// holds whose placement it wrote and that nobody changed since.
/// </summary>
public sealed partial class HoldTexturePlacementService
{
    /// <summary>Marks the rolling edit run's entries of these holds as restoring their volume placement from before.</summary>
    /// <param name="db">The context.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="modelId">The active model.</param>
    /// <param name="before">Each hold's volume placement before the refinement changed it.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    internal static async Task RecordVolumePlacementsAsync(
        BlocwerkDbContext db, Guid wallId, Guid modelId, IReadOnlyDictionary<Guid, string?> before, CancellationToken ct)
    {
        if (before.Count == 0 || await RollingRunAsync(db, wallId, modelId, create: false, ct) is not { } run)
        {
            return;
        }

        var entries = HoldPlacementEntry.FromJson(run.HoldsJson);
        var marked = entries
            .Select(e => !e.RestoresVolumePlacement && before.TryGetValue(e.HoldId, out var prev)
                ? e with { RestoresVolumePlacement = true, PrevVolumePlacementJson = prev }
                : e)
            .ToList();
        if (!marked.SequenceEqual(entries))
        {
            run.HoldsJson = HoldPlacementEntry.ToJson(marked);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Writes the placements of the holds that are still as they were read, when the model is still the active one, and records
    /// them in the model's rolling edit run. Returns the holds placed.
    /// </summary>
    private static async Task<IReadOnlyList<Guid>> WriteEditedAsync(
        BlocwerkDbContext db,
        Guid wallId,
        Guid modelId,
        List<PlannedPlacement> planned,
        IReadOnlyDictionary<string, PlaneRectMm> extents,
        CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if ((await ActiveModelTextures.FindAsync(db, wallId, ct))?.Id != modelId)
        {
            return [];
        }

        var claimed = new List<PlannedPlacement>();
        foreach (var p in planned.Where(p => OnFacet(p.Fit, extents, null)))
        {
            if (await ClaimAsync(db, p.Hold, ct))
            {
                claimed.Add(p);
            }
        }

        var ids = claimed.Select(p => p.Hold.Id).ToList();
        var holds = await db.Holds.Where(h => ids.Contains(h.Id)).ToDictionaryAsync(h => h.Id, ct);
        var entries = claimed
            .Where(p => holds.TryGetValue(p.Hold.Id, out var hold) && !ChangedSincePlanned(hold, p.Hold, HoldTexturePlacer.IsEligibleAfterEdit))
            .Select(p => Write(holds[p.Hold.Id], p, extents) with { GeometryHash = HoldPlacementEntry.HashGeometry(holds[p.Hold.Id]) })
            .ToList();
        if (entries.Count == 0)
        {
            return [];
        }

        var run = (await RollingRunAsync(db, wallId, modelId, create: true, ct))!;
        var replaced = entries.Select(e => e.HoldId).ToHashSet();
        var kept = HoldPlacementEntry.FromJson(run.HoldsJson).Where(e => !replaced.Contains(e.HoldId)).Concat(entries).ToList();
        (run.HoldsJson, run.PlacedCount) = (HoldPlacementEntry.ToJson(kept), kept.Count);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return entries.Select(e => e.HoldId).ToList();
    }

    /// <summary>
    /// Locks the hold's row with a no-op update that only matches while its panel geometry and metric fields are exactly as
    /// read; false when a newer edit changed them.
    /// </summary>
    private static async Task<bool> ClaimAsync(BlocwerkDbContext db, Hold read, CancellationToken ct)
    {
        var (id, panel, x, y, radius) = (read.Id, read.WallPanelId, read.X, read.Y, read.Radius);
        var (facet, a, b, source, width, height) = (read.FacetId, read.PlaneAMm, read.PlaneBMm, read.MetricSource, read.WidthMm, read.HeightMm);
        var rows = await db.Holds
            .Where(h => h.Id == id && h.WallPanelId == panel && h.X == x && h.Y == y && h.Radius == radius
                && h.FacetId == facet && h.PlaneAMm == a && h.PlaneBMm == b && h.MetricSource == source
                && h.WidthMm == width && h.HeightMm == height)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.FacetId, h => h.FacetId), ct);
        return rows == 1;
    }

    /// <summary>
    /// The model's unreverted rolling edit run on its current textures (tracked), created when asked; null when there is none.
    /// After "Render wall textures again" a new one starts, so the earlier one's entries keep their textures.
    /// </summary>
    private static async Task<HoldPlacementRun?> RollingRunAsync(BlocwerkDbContext db, Guid wallId, Guid modelId, bool create, CancellationToken ct)
    {
        var key = (await TextureSetStamp.OfModelAsync(db, modelId, ct))?.Key;
        var run = await db.HoldPlacementRuns.FirstOrDefaultAsync(
            r => r.WallId == wallId && r.GeometryModelId == modelId && r.TextureSetKey == key
                && r.Trigger == HoldPlacementTrigger.Edit && r.RevertedAt == null,
            ct);
        if (run is null && create)
        {
            run = new HoldPlacementRun
            {
                WallId = wallId, GeometryModelId = modelId, TextureSetKey = key, CreatedByUserId = Guid.Empty, Trigger = HoldPlacementTrigger.Edit,
            };
            db.HoldPlacementRuns.Add(run);
        }

        return run;
    }
}
