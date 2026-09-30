// <copyright file="ActiveModelTextures.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Geometry.View3D;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// The wall's active model as photo registration needs it: its facet textures (read from the capture store) with their
/// extents and 3D frames. Shared by placing holds on the model and by the panel update's 3D evidence.
/// </summary>
internal static class ActiveModelTextures
{
    /// <summary>The active model's id and JSON, or null when the wall has none.</summary>
    public static async Task<(Guid Id, string Json, DateTimeOffset CreatedAt)?> FindAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var model = await db.WallGeometryModels.AsNoTracking()
            .Where(m => m.WallId == wallId && m.IsActive)
            .Select(m => new { m.Id, m.Json, m.CreatedAt })
            .FirstOrDefaultAsync(ct);
        return model is null ? null : (model.Id, model.Json, model.CreatedAt);
    }

    /// <summary>The model's textures; an empty list when it has none (or none could be read).</summary>
    public static async Task<ActiveModel> LoadAsync(
        BlocwerkDbContext db, ICaptureFileStore files, Guid modelId, string json, ILogger logger, CancellationToken ct)
    {
        var (extents, facets) = Facets(json, logger);
        var rows = await db.WallGeometryTextures.AsNoTracking().Where(t => t.GeometryModelId == modelId).ToListAsync(ct);
        var textures = new List<RegistrationTexture>();
        foreach (var row in rows.OrderBy(r => r.FacetId, StringComparer.Ordinal))
        {
            var frame = TexturePlaneFrame.Of(row);
            var image = await files.ReadAsync(row.StoredPath, ct);
            if (image is null || !frame.IsValid)
            {
                logger.LogWarning("Texture of facet {FacetId} (model {ModelId}) is missing or has no grid; skipped", row.FacetId, modelId);
                continue;
            }

            var mask = row.MaskStoredPath is { } maskPath ? await files.ReadAsync(maskPath, ct) : null;
            var extent = extents.TryGetValue(row.FacetId, out var e) ? e : new PlaneRectMm(row.AMin, row.AMax, row.BMin, row.BMax);
            textures.Add(new RegistrationTexture(frame, extent, image, mask, facets.GetValueOrDefault(row.FacetId)));
        }

        return new ActiveModel(modelId, textures, extents, facets);
    }

    /// <summary>The model's facet extents and 3D frames by facet id (empty when it does not parse).</summary>
    public static (Dictionary<string, PlaneRectMm> Extents, Dictionary<string, FacetFrame> Frames) Facets(string json, ILogger logger)
    {
        try
        {
            var doc = WallGeometryDocument.Parse(json);
            var frames = doc.Segments.SelectMany(s => s.Facets)
                .Where(f => !string.IsNullOrEmpty(f.Id))
                .Select(f => (Id: f.Id!, Frame: FacetFrame.From(f)))
                .Where(f => f.Frame is not null)
                .GroupBy(f => f.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Frame!, StringComparer.Ordinal);
            return (Wall3DFallbackPlacement.FacetExtents(doc), frames);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "The active model does not parse; the textures' own bounds are used as facet extents");
            return ([], []);
        }
    }
}
