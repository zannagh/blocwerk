// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Tests;

/// <summary>Gives a model a texture (image, mask, source map) and a photo-real view (scene, one ladder level, uncleaned copy).</summary>
internal static class ModelFilesTestData
{
    /// <summary>Adds the rows and their files; returns the stored names, the texture image first.</summary>
    public static async Task<List<string>> AddAsync(WallTestHarness h, ICaptureFileStore files, Guid modelId)
    {
        var ct = CancellationToken.None;
        var texture = new WallGeometryTexture
        {
            GeometryModelId = modelId,
            FacetId = "0",
            StoredPath = await files.SaveAsync(CaptureScenario.TinyJpeg(1), ".jpg", ct),
            MaskStoredPath = await files.SaveAsync(new byte[300], ".png", ct),
            SourceMapStoredPath = await files.SaveAsync("{}"u8.ToArray(), ".json", ct),
        };
        var level = await files.SaveAsync(new byte[500], ".spz", ct);
        var splat = new WallGeometrySplat
        {
            GeometryModelId = modelId,
            StoredPath = await files.SaveAsync(new byte[2000], ".spz", ct),
            LodLevelsJson = SplatLodLadder.Serialize([new SplatLodLevel(40_000, level, 500)]),
            UncleanedStoredPath = await files.SaveAsync(new byte[2500], ".spz", ct),
            FrameJson = "{}",
        };
        await using var db = h.CreateContext();
        db.WallGeometryTextures.Add(texture);
        db.WallGeometrySplats.Add(splat);
        await db.SaveChangesAsync();
        return [texture.StoredPath, texture.MaskStoredPath!, texture.SourceMapStoredPath!, splat.StoredPath, level, splat.UncleanedStoredPath!];
    }
}
