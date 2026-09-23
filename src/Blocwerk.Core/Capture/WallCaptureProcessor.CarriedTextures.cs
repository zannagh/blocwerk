// <copyright file="WallCaptureProcessor.CarriedTextures.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture;

/// <summary>
/// A partial re-capture carries unphotographed facets over from the model it was registered to, in the same
/// frame; their textures are copied too (as new files, so neither model's cleanup can delete the other's).
/// </summary>
public sealed partial class WallCaptureProcessor
{
    private async Task CopyCarriedTexturesAsync(Guid modelId, List<WallGeometryTexture> rows, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var json = await db.WallGeometryModels.Where(m => m.Id == modelId).Select(m => m.Json).FirstAsync(ct);
        var (referenceId, facets) = RegisteredGeometry.Carried(json);
        if (referenceId is null || facets.Count == 0)
        {
            return;
        }

        var sources = await db.WallGeometryTextures.AsNoTracking()
            .Where(t => t.GeometryModelId == referenceId && facets.Contains(t.FacetId))
            .ToListAsync(ct);
        foreach (var source in sources.Where(s => rows.All(r => r.FacetId != s.FacetId)))
        {
            var bytes = await files.ReadAsync(source.StoredPath, ct);
            if (bytes is null)
            {
                continue;
            }

            rows.Add(new WallGeometryTexture
            {
                GeometryModelId = modelId,
                FacetId = source.FacetId,
                StoredPath = await files.SaveAsync(bytes, CapturePhotoFormat.Extension(CapturePhotoFormat.Sniff(bytes)), ct),
                ContentType = source.ContentType,
                SizeBytes = bytes.LongLength,
                AMin = source.AMin,
                AMax = source.AMax,
                BMin = source.BMin,
                BMax = source.BMax,
                WidthPx = source.WidthPx,
                HeightPx = source.HeightPx,
            });
        }
    }
}
