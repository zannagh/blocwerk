// <copyright file="Wall3DViewService.Volumes.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.View3D;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>The volumes step of the 3D view (<see cref="Wall3DVolumes"/>).</summary>
public sealed partial class Wall3DViewService
{
    /// <summary>The view with the active model's visible volumes; unchanged when it has none.</summary>
    private async Task<Wall3DView> WithVolumesAsync(Wall wall, Wall3DView view, string json, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var volumes = await db.WallVolumes.AsNoTracking()
            .Where(v => v.WallId == wall.Id && !v.IsHidden && v.GeometryModel.IsActive)
            .ToListAsync(ct);
        if (volumes.Count == 0)
        {
            return view;
        }

        var maps = new Dictionary<string, TextureSourceMap>(StringComparer.Ordinal);
        if (files is not null)
        {
            var facetIds = volumes.Select(v => v.FacetId).Distinct().ToList();
            var stored = await db.WallGeometryTextures.AsNoTracking()
                .Where(t => t.GeometryModel.WallId == wall.Id && t.GeometryModel.IsActive && t.SourceMapStoredPath != null && facetIds.Contains(t.FacetId))
                .Select(t => new { t.FacetId, Path = t.SourceMapStoredPath! })
                .ToListAsync(ct);
            foreach (var t in stored)
            {
                if (await files.ReadAsync(t.Path, ct) is { } bytes && TextureSourceMap.Parse(bytes) is { } map)
                {
                    maps[t.FacetId] = map;
                }
            }
        }

        var live = wall.Holds.Where(h => h.Generation <= wall.CurrentGeneration);
        return Wall3DVolumes.Apply(view, volumes, live, maps, SolvedCamera.ParseAll(json));
    }
}
