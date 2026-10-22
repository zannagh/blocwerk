// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Puts a wall's textures, masks and splats back into the state before the hash backfill (every hash null), so the
/// tests of the legacy versioned geometry routes see the legacy URLs <see cref="Assets.AssetUrls"/> falls back to.
/// Raw updates, as the backfill writes: a tracked write would go through the content-hash stamp.
/// </summary>
internal static class UnhashedGeometry
{
    public static async Task ClearAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        await db.WallGeometryTextures.ExecuteUpdateAsync(u => u
            .SetProperty(t => t.Sha256, (byte[]?)null).SetProperty(t => t.MaskSha256, (byte[]?)null));
        await db.WallGeometrySplats.ExecuteUpdateAsync(u => u
            .SetProperty(s => s.Sha256, (byte[]?)null).SetProperty(s => s.MobileSha256, (byte[]?)null));

        var ladders = await db.WallGeometrySplats.AsNoTracking()
            .Where(s => s.LodLevelsJson != null).Select(s => new { s.Id, s.LodLevelsJson }).ToListAsync();
        foreach (var ladder in ladders)
        {
            var cleared = SplatLodLadder.Serialize([.. SplatLodLadder.Parse(ladder.LodLevelsJson).Select(l => l with { Sha = null })]);
            await db.WallGeometrySplats.Where(s => s.Id == ladder.Id).ExecuteUpdateAsync(u => u.SetProperty(s => s.LodLevelsJson, cleared));
        }
    }
}
