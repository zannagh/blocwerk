// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>The wall's public share token, minted the way the app mints it (alongside the first invite).</summary>
internal static class WallShareTokens
{
    public static async Task<string> MintAsync(WallTestHarness h)
    {
        await h.WallService.GetOrCreateJoinTokenAsync(h.WallId);
        await using var db = h.CreateContext();
        return await db.Walls.IgnoreQueryFilters().Where(w => w.Id == h.WallId).Select(w => w.ShareToken!).FirstAsync();
    }
}
