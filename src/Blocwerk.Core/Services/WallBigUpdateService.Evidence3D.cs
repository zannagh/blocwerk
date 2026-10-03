// <copyright file="WallBigUpdateService.Evidence3D.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry.TextureRegistration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// 3D evidence for the new-hold triage: each staged panel photo is registered onto the active model's facet textures
/// (seeded by the matched old holds' placements), and every unpaired detection is judged by where it lands
/// (<see cref="NewHoldEvidence3D"/>). Optional throughout: no matcher, no active model, no textures or a photo that
/// does not register means no evidence, and the triage behaves exactly as without 3D.
/// </summary>
public partial class WallBigUpdateService
{
    private const int MaxCachedRegistrations = 32;

    /// <summary>Registrations per staged photo and model: a resume re-runs the triage, the photo and model are the same.</summary>
    private static readonly ConcurrentDictionary<(Guid PanelId, DateTimeOffset? StagedAt, Guid ModelId), StagedRegistration> Registered = new();

    /// <summary>What the active model knows, loaded once per triage; null when there is no 3D evidence to use.</summary>
    private async Task<Evidence3DModel?> LoadEvidence3DAsync(
        BlocwerkDbContext db, Guid wallId, IReadOnlyList<CarryoverProposal> carryover, IReadOnlyList<Hold> oldHolds)
    {
        if (textureMatcher is null || captureFiles is null)
        {
            return null;
        }

        try
        {
            if (await ActiveModelTextures.FindAsync(db, wallId, CancellationToken.None) is not { } model
                || !await db.WallGeometryTextures.AnyAsync(t => t.GeometryModelId == model.Id))
            {
                return null;
            }

            // Only old holds found again on the new photos (a hold not found again may have been replaced on the same
            // spot), placed by a run on THIS model (a re-solve moves the planes, so an older model's spots may be off).
            var paired = carryover.Select(p => p.OldHoldId).ToHashSet();
            var runs = await db.HoldPlacementRuns.AsNoTracking()
                .Where(r => r.WallId == wallId && r.GeometryModelId == model.Id && r.RevertedAt == null)
                .Select(r => r.HoldsJson)
                .ToListAsync();
            var placedHere = runs.SelectMany(HoldPlacementEntry.FromJson).Select(e => e.HoldId).ToHashSet();
            var known = oldHolds
                .Where(h => h is { FacetId: not null, PlaneAMm: not null, PlaneBMm: not null, VolumePlacementJson: null, IsVirtual: false }
                    && !HoldTexturePlacer.IsRejected(h) && placedHere.Contains(h.Id) && paired.Contains(h.Id))
                .Select(h => new FacetSpot(h.FacetId!, h.PlaneAMm!.Value, h.PlaneBMm!.Value, Math.Max(h.WidthMm ?? 0, h.HeightMm ?? 0) / 2))
                .ToList();
            var seen = await db.HoldProposals.AsNoTracking()
                .Where(p => p.WallId == wallId && p.GeometryModelId == model.Id && p.Status == HoldProposalStatus.Pending)
                .Select(p => new FacetSpot(p.FacetId, p.A, p.B, p.SizeMm / 2))
                .ToListAsync();
            var (extents, frames) = ActiveModelTextures.Facets(model.Json, logger);
            var facets = frames.Where(f => extents.ContainsKey(f.Key))
                .ToDictionary(f => f.Key, f => new ModelFacet(f.Value, extents[f.Key]), StringComparer.Ordinal);
            return new Evidence3DModel(model.Id, model.Json, model.CreatedAt, known, seen, facets);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "3D evidence for the panel update on wall {WallId} is unavailable; the triage runs without it", wallId);
            return null;
        }
    }

    /// <summary>The 3D verdict per candidate of one staged panel, or null without usable evidence.</summary>
    private async Task<Dictionary<Guid, Evidence3DVerdict>?> Verdicts3DAsync(
        BlocwerkDbContext db, Evidence3DModel? model, Guid panelId, IReadOnlyList<Hold> candidates, IReadOnlyList<(Hold Old, Hold New)> twins)
    {
        if (model is null || candidates.Count == 0)
        {
            return null;
        }

        try
        {
            var staged = await db.WallPanels.AsNoTracking().Where(p => p.Id == panelId)
                .Select(p => new { p.StagedAt, p.Col, p.Row }).FirstAsync();
            var registered = await RegisterStagedAsync(db, model, panelId, staged.StagedAt, twins, $"staged c{staged.Col} r{staged.Row}");
            var seen = staged.StagedAt is { } at && model.CreatedAt >= at ? model.SeenIn3D : [];
            var evidence = new Panel3DEvidence(
                registered.Registrations, model.KnownHolds, seen, model.Facets, registered.Width, registered.Height, registered.FocalPx);
            if (!evidence.IsUsable)
            {
                return null;
            }

            return candidates.ToDictionary(h => h.Id, h => NewHoldEvidence3D.Judge(evidence, h.X, h.Y));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Panel {PanelId}: the new photo could not be matched to the 3D model; its triage runs without 3D", panelId);
            return null;
        }
    }

    private async Task<StagedRegistration> RegisterStagedAsync(
        BlocwerkDbContext db, Evidence3DModel model, Guid panelId, DateTimeOffset? stagedAt, IReadOnlyList<(Hold Old, Hold New)> twins, string label)
    {
        var key = (panelId, stagedAt, model.Id);
        if (Registered.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var photo = await db.WallPanels.AsNoTracking().Where(p => p.Id == panelId).Select(p => p.StagedPhoto).FirstAsync();
        model.Textures ??= (await ActiveModelTextures.LoadAsync(db, captureFiles!, model.Id, model.Json, logger, CancellationToken.None)).Textures;
        if (photo is null || model.Textures.Count == 0)
        {
            return StagedRegistration.None;
        }

        var anchors = twins
            .Where(t => t.Old is { FacetId: not null, PlaneAMm: not null, PlaneBMm: not null } && !HoldTexturePlacer.IsRejected(t.Old))
            .Select(t => new PlaneAnchor(t.New.X, t.New.Y, t.Old.FacetId!, t.Old.PlaneAMm!.Value, t.Old.PlaneBMm!.Value))
            .ToList();
        var textures = model.Textures;
        var registered = await Task.Run(() => Register(photo, textures, anchors, label));
        if (Registered.Count >= MaxCachedRegistrations)
        {
            Registered.Clear();
        }

        Registered[key] = registered;
        logger.LogInformation(
            "Panel {PanelId}: new photo matched to {Accepted} of {Facets} facets of model {ModelId} ({Anchors} anchors)",
            panelId, registered.Registrations.Count(r => r.Accepted), registered.Registrations.Count, model.Id, anchors.Count);
        return registered;
    }

    private StagedRegistration Register(byte[] photo, List<RegistrationTexture> textures, List<PlaneAnchor> anchors, string label)
    {
        using var session = textureMatcher!.OpenPhoto(photo);
        var focal = ExifCameraReader.Read(photo).Focal35mm is { } f35 ? f35 / 36.0 * Math.Max(session.Width, session.Height) : (double?)null;
        var registrations = new PhotoRegistrar(session, logger, label, focal, anchors).RegisterAll(textures);
        return new StagedRegistration(registrations, session.Width, session.Height, focal);
    }
}

