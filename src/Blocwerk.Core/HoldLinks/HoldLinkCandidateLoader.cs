// <copyright file="HoldLinkCandidateLoader.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.HoldLinks;

/// <summary>
/// Reads what the finder needs from the database: the wall's live panel holds placed on the ACTIVE model (at their
/// current volume point when they sit on a visible volume, as the 3D view draws them), and its stored links.
/// </summary>
internal static class HoldLinkCandidateLoader
{
    /// <summary>The placed live holds, or null when the wall has no usable active model.</summary>
    /// <param name="db">The context.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The inputs, or null.</returns>
    public static async Task<HoldLinkInputs?> LoadAsync(
        BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var model = await db.WallGeometryModels.AsNoTracking()
            .Where(m => m.WallId == wallId && m.IsActive)
            .Select(m => new { m.Id, m.Json })
            .FirstOrDefaultAsync(ct);
        var frames = model is null ? null : Frames(model.Json);
        if (model is null || frames is null || frames.Count == 0)
        {
            return null;
        }

        var volumes = await db.WallVolumes.AsNoTracking()
            .Where(v => v.GeometryModelId == model.Id && !v.IsHidden && !v.IsRemoved)
            .Select(v => v.Id)
            .ToListAsync(ct);
        var visible = volumes.ToHashSet();
        var holds = await (await LiveWallHolds.QueryAsync(db, wallId, ct)).AsNoTracking().ToListAsync(ct);
        var placed = holds
            .Where(h => h.WallPanelId is not null && !h.IsVirtual)
            .Select(h => WorldOf(h, frames, visible) is { } world ? ToCandidate(h, world) : null)
            .OfType<HoldLinkCandidate>()
            .ToList();
        var panelOf = holds.Where(h => h.WallPanelId is not null).ToDictionary(h => h.Id, h => h.WallPanelId!.Value);
        return new HoldLinkInputs(model.Id, placed, panelOf, holds.Select(h => h.Id).ToHashSet());
    }

    /// <summary>The wall's stored links as unordered pairs.</summary>
    /// <param name="db">The context.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The pairs in <see cref="HoldLinkPairSuggestion.Key"/> order.</returns>
    public static async Task<HashSet<(Guid A, Guid B)>> LinksAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var links = await db.HoldLinks.AsNoTracking()
            .Where(l => l.WallId == wallId)
            .Select(l => new { l.HoldAId, l.HoldBId })
            .ToListAsync(ct);
        return links.Select(l => HoldLinkPairSuggestion.Key(l.HoldAId, l.HoldBId)).ToHashSet();
    }

    /// <summary>Where the hold sits in the wall world; null when it is not placed on a facet of the model.</summary>
    internal static double[]? WorldOf(Hold h, IReadOnlyDictionary<string, FacetFrame> frames, IReadOnlySet<Guid> visibleVolumes)
    {
        if (h.FacetId is null || h.PlaneAMm is not { } a || h.PlaneBMm is not { } b
            || HoldTexturePlacer.IsRejected(h) || !frames.TryGetValue(h.FacetId, out var frame))
        {
            return null;
        }

        var onVolume = HoldVolumePlacement.FromJson(h.VolumePlacementJson);
        return onVolume is not null && visibleVolumes.Contains(onVolume.VolumeId) && onVolume.Matches(a, b)
            ? frame.ToWorld(onVolume.A, onVolume.B, onVolume.H)
            : frame.ToWorld(a, b);
    }

    private static HoldLinkCandidate ToCandidate(Hold h, double[] world)
    {
        var size = h.WidthMm is > 0 && h.HeightMm is > 0 ? Math.Max(h.WidthMm.Value, h.HeightMm.Value) : (double?)null;
        return new HoldLinkCandidate(h.Id, h.WallPanelId!.Value, h.Color, size, h.Category == HoldCategory.Foot, world);
    }

    private static Dictionary<string, FacetFrame>? Frames(string json)
    {
        WallGeometryDocument doc;
        try
        {
            doc = WallGeometryDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        return doc.Segments.SelectMany(s => s.Facets)
            .Where(f => !string.IsNullOrEmpty(f.Id))
            .Select(f => (f.Id, Frame: FacetFrame.From(f)))
            .Where(x => x.Frame is not null)
            .GroupBy(x => x.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Frame!, StringComparer.Ordinal);
    }
}
