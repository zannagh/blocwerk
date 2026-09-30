// <copyright file="HoldTexturePlacementService.Edited.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Diagnostics;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.TextureRegistration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Holds a user just moved, reshaped or added, placed again from their panel photo's registration (the 3D placement
/// follows the panel; the panel is never changed). A photo's registration is cached per photo and model
/// (<see cref="PanelRegistrationCache"/>), so an edit costs one registration per photo and model at most, then
/// only the mapping of the hold's centre. Writing: <see cref="WriteEditedAsync"/>.
/// </summary>
public sealed partial class HoldTexturePlacementService
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<Guid>> PlaceEditedAsync(Guid wallId, IReadOnlyCollection<Guid> holdIds, CancellationToken ct = default)
    {
        if (!Enabled || holdIds.Count == 0)
        {
            return [];
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        if (await ActiveModelTextures.FindAsync(db, wallId, ct) is not { } found
            || !await db.WallGeometryTextures.AnyAsync(t => t.GeometryModelId == found.Id, ct))
        {
            return [];
        }

        var watch = Stopwatch.StartNew();
        var live = await (await LiveHoldsQueryAsync(db, wallId, ct)).AsNoTracking().ToListAsync(ct);
        var written = await WrittenOnModelAsync(db, wallId, found.Id, ct);
        var edited = live.Where(h => holdIds.Contains(h.Id) && HoldTexturePlacer.IsEligibleAfterEdit(h) && !IsSettled(h, written)).ToList();
        if (edited.Count == 0)
        {
            return [];
        }

        var source = new EditedPlacementSource(found.Id, () => ActiveModelTextures.LoadAsync(db, files!, found.Id, found.Json, logger, ct));
        var planned = new List<PlannedPlacement>();
        foreach (var panel in edited.GroupBy(h => h.WallPanelId!.Value))
        {
            var anchors = live.Where(h => h.WallPanelId == panel.Key && IsSettled(h, written)).ToList();
            if (await PanelRegistrationsAsync(db, panel.Key, anchors, source, ct) is not { } registrations)
            {
                continue;
            }

            planned.AddRange(panel
                .Select(h => HoldTexturePlacer.Place(h, registrations) is { } fit ? new PlannedPlacement(h, fit, HoldTexturePlacer.Measure(h, fit)) : null)
                .OfType<PlannedPlacement>());
        }

        var placed = planned.Count == 0 ? [] : await WriteEditedAsync(db, wallId, found.Id, planned, Facets(found.Json).Extents, ct);
        logger.LogInformation(
            "Wall {WallId}: {Placed} of {Edited} edited holds placed again from their panel photos on model {ModelId} ({Registered} photos registered, {Ms} ms)",
            wallId, placed.Count, edited.Count, found.Id, source.Registered, watch.ElapsedMilliseconds);
        return placed;
    }

    /// <summary>A run's photo registrations, kept for the edits that follow it (under the photo stamp read with the bytes).</summary>
    private static void RememberRegistrations(Guid modelId, List<PanelPlan> plans)
    {
        foreach (var plan in plans.Where(p => p.Registrations is not null && p.Photo is not null))
        {
            PanelRegistrationCache.Put(new PanelRegistrationKey(plan.Summary.PanelId, plan.Photo!.Value, modelId), plan.Registrations!);
        }
    }

    /// <summary>The entries of every unreverted run on <paramref name="modelId"/>, by hold.</summary>
    private static async Task<ILookup<Guid, HoldPlacementEntry>> WrittenOnModelAsync(
        BlocwerkDbContext db, Guid wallId, Guid modelId, CancellationToken ct)
    {
        var runs = await db.HoldPlacementRuns.AsNoTracking()
            .Where(r => r.WallId == wallId && r.GeometryModelId == modelId && r.RevertedAt == null)
            .Select(r => r.HoldsJson)
            .ToListAsync(ct);
        return runs.SelectMany(HoldPlacementEntry.FromJson).ToLookup(e => e.HoldId);
    }

    /// <summary>A run on the model wrote the hold's current placement, and (when it recorded it) for its current panel geometry.</summary>
    private static bool IsSettled(Hold hold, ILookup<Guid, HoldPlacementEntry> written)
    {
        var placement = HoldPlacementEntry.HashPlacement(hold);
        var geometry = HoldPlacementEntry.HashGeometry(hold);
        return written[hold.Id].Any(e => e.PlacementHash == placement && (e.GeometryHash is null || e.GeometryHash == geometry));
    }

    /// <summary>
    /// The panel photo's registrations onto the active model: cached, else registered now with the panel's settled holds as
    /// anchors. Null when the panel has no photo, its photo changed between the two reads (a panel update landed), a photo
    /// that does not decode, or the model has no readable textures.
    /// </summary>
    private async Task<IReadOnlyList<FacetRegistration>?> PanelRegistrationsAsync(
        BlocwerkDbContext db, Guid panelId, List<Hold> anchorHolds, EditedPlacementSource source, CancellationToken ct)
    {
        var stamp = await db.WallPanels.AsNoTracking().Where(p => p.Id == panelId && p.Photo != null)
            .Select(p => new { p.Generation, p.Photo!.Length })
            .FirstOrDefaultAsync(ct);
        if (stamp is null)
        {
            return null;
        }

        if (PanelRegistrationCache.Get(new PanelRegistrationKey(panelId, new PanelPhotoStamp(stamp.Generation, stamp.Length), source.ModelId)) is { } cached)
        {
            return cached;
        }

        var textures = await source.TexturesAsync();
        var panel = await db.WallPanels.AsNoTracking().Where(p => p.Id == panelId)
            .Select(p => new { p.Col, p.Row, p.Generation, p.Photo }).FirstAsync(ct);
        if (textures.Count == 0 || panel.Photo is null || panel.Generation != stamp.Generation || panel.Photo.Length != stamp.Length)
        {
            return null;
        }

        var anchors = anchorHolds
            .Where(h => h is { FacetId: not null, PlaneAMm: not null, PlaneBMm: not null } && !HoldTexturePlacer.IsRejected(h))
            .Select(h => new PlaneAnchor(h.X, h.Y, h.FacetId!, h.PlaneAMm!.Value, h.PlaneBMm!.Value))
            .ToList();
        try
        {
            var registrations = await Task.Run(() => Register(panel.Photo, textures, $"c{panel.Col} r{panel.Row}", anchors, null, ct), ct);
            source.Registered++;
            PanelRegistrationCache.Put(new PanelRegistrationKey(panelId, new PanelPhotoStamp(panel.Generation, panel.Photo.Length), source.ModelId), registrations);
            return registrations;
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Panel {PanelId}: the photo could not be decoded; its edited holds keep their estimated placement", panelId);
            return null;
        }
    }
}
