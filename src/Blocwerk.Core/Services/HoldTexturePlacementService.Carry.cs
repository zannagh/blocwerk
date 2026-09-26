// <copyright file="HoldTexturePlacementService.Carry.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Geometry.View3D;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Previous placements on the active model: a hold this action placed on an EARLIER model keeps a good position
/// even when a re-capture's textures cannot be registered to its photo (each capture renders new textures, and
/// the registration of an oblique photo is not stable across them). The position is known in the earlier model's
/// facet frame; <see cref="PlacementCarrier"/> moves it onto the active model through the shared wall frame.
/// </summary>
public sealed partial class HoldTexturePlacementService
{
    /// <summary>
    /// Every eligible hold with a texture placement on an earlier model, moved onto the active model. The earlier
    /// model is the one of the newest unreverted run that wrote the hold's current placement (its hash matches);
    /// a hold whose placement no run wrote, or one a run registered on the active model, is not carried. The position
    /// always moves through world space (<see cref="PlacementCarrier"/>), never as raw (a, b): a re-solved or rebased facet
    /// frame gives the same point other plane coordinates.
    /// </summary>
    private async Task<(Dictionary<Guid, CarriedPosition> Carried, HashSet<Guid> OtherFrame)> CarriedPositionsAsync(
        BlocwerkDbContext db, Guid wallId, ActiveModel active, List<Hold> holds, CancellationToken ct)
    {
        var candidates = holds
            .Where(h => HoldTexturePlacer.IsEligible(h) && HoldTexturePlacer.IsTexturePlaced(h))
            .Where(h => h.FacetId is not null && h.PlaneAMm is not null && h.PlaneBMm is not null)
            .ToDictionary(h => h.Id);
        var sourceModel = await PlacementModelsAsync(db, wallId, candidates, ct);

        // A placement a run wrote on the active model is kept as it is, except one that run only carried over: that is
        // carried again (in place) so it is checked against this run's evidence like any other carried placement.
        var settled = sourceModel
            .Where(kv => kv.Value == active.Id && candidates[kv.Key].MetricSource != HoldMetric.TextureRegistrationCarried)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var id in settled)
        {
            sourceModel.Remove(id);
        }

        var otherFrame = await OtherFrameAsync(db, wallId, active.Id, candidates.Keys, sourceModel, settled, ct);
        foreach (var id in otherFrame)
        {
            sourceModel.Remove(id);
        }

        var models = sourceModel.Values.Where(id => id != active.Id).Distinct().ToList();
        var frames = new Dictionary<Guid, Dictionary<string, FacetFrame>> { [active.Id] = active.Frames };
        foreach (var model in await db.WallGeometryModels.AsNoTracking().Where(m => models.Contains(m.Id)).Select(m => new { m.Id, m.Json }).ToListAsync(ct))
        {
            frames[model.Id] = Facets(model.Json).Frames;
        }

        var carried = new Dictionary<Guid, CarriedPosition>();
        foreach (var (holdId, modelId) in sourceModel)
        {
            var hold = candidates[holdId];
            if (frames.TryGetValue(modelId, out var previous) && Carry(hold, previous, active) is { } position)
            {
                carried[holdId] = position;
            }
        }

        return (carried, otherFrame);
    }

    /// <summary>
    /// After a frame reset (<see cref="FrameLineage"/>): the candidates whose placement was written in an earlier frame (by a run on
    /// a model outside the active one's frame, or by no known run). Their coordinates mean nothing on the active model, so they
    /// are never carried, and lose their placement unless this run places them again. Empty without a reset.
    /// </summary>
    private static async Task<HashSet<Guid>> OtherFrameAsync(
        BlocwerkDbContext db,
        Guid wallId,
        Guid activeId,
        IEnumerable<Guid> candidates,
        Dictionary<Guid, Guid> sourceModel,
        List<Guid> settled,
        CancellationToken ct)
    {
        if (await FrameLineage.SameFrameAsync(db, wallId, activeId, ct) is not { } sameFrame)
        {
            return [];
        }

        bool Other(Guid id) => sourceModel.TryGetValue(id, out var model) ? !sameFrame.Contains(model) : !settled.Contains(id);
        return candidates.Where(Other).ToHashSet();
    }

    /// <summary>The previous placement of <paramref name="hold"/> on the active model's facet with the same id, or null.</summary>
    private static CarriedPosition? Carry(Hold hold, Dictionary<string, FacetFrame> previous, ActiveModel active)
    {
        var facet = hold.FacetId!;
        if (!previous.TryGetValue(facet, out var from) || !active.Frames.TryGetValue(facet, out var to)
            || !active.Extents.TryGetValue(facet, out var extent))
        {
            return null;
        }

        return PlacementCarrier.Carry(from, to, extent, hold.PlaneAMm!.Value, hold.PlaneBMm!.Value) is { } p
            ? new CarriedPosition(facet, p.A, p.B)
            : null;
    }

    /// <summary>For each candidate, the model of the newest unreverted run that wrote its current placement.</summary>
    private static async Task<Dictionary<Guid, Guid>> PlacementModelsAsync(
        BlocwerkDbContext db, Guid wallId, Dictionary<Guid, Hold> candidates, CancellationToken ct)
    {
        var result = new Dictionary<Guid, Guid>();
        if (candidates.Count == 0)
        {
            return result;
        }

        // Few rows per wall; ordered in memory because SQLite cannot ORDER BY a DateTimeOffset.
        var runs = await db.HoldPlacementRuns.AsNoTracking()
            .Where(r => r.WallId == wallId && r.RevertedAt == null)
            .Select(r => new { r.CreatedAt, r.GeometryModelId, r.HoldsJson })
            .ToListAsync(ct);
        var hashes = candidates.ToDictionary(kv => kv.Key, kv => HoldPlacementEntry.HashPlacement(kv.Value));
        foreach (var run in runs.OrderByDescending(r => r.CreatedAt))
        {
            foreach (var entry in HoldPlacementEntry.FromJson(run.HoldsJson))
            {
                if (!result.ContainsKey(entry.HoldId) && hashes.TryGetValue(entry.HoldId, out var hash) && hash == entry.PlacementHash)
                {
                    result[entry.HoldId] = run.GeometryModelId;
                }
            }
        }

        return result;
    }
}
