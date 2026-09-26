// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture.Corrections;

/// <summary>
/// A corrected model version shares its texture and photo-real files with the model it was derived from. Whoever replaces
/// one model's files (a new texture set, a retrained photo-real view) deletes only what no other row still points at.
/// </summary>
public static class SharedCaptureFiles
{
    /// <summary>Every stored texture and photo-real file name some row still references.</summary>
    /// <param name="db">A context (after the replacement was saved).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The names.</returns>
    public static async Task<HashSet<string>> ReferencedAsync(BlocwerkDbContext db, CancellationToken ct)
    {
        var textures = await db.WallGeometryTextures.AsNoTracking()
            .Select(t => new[] { t.StoredPath, t.MaskStoredPath, t.SourceMapStoredPath })
            .ToListAsync(ct);
        var splats = await db.WallGeometrySplats.AsNoTracking().ToListAsync(ct);
        return textures.SelectMany(t => t)
            .Concat(splats.SelectMany(SplatLodLadder.Files))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
    }
}
