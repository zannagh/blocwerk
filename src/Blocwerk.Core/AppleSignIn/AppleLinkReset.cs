// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.AppleSignIn;

/// <summary>
/// Drops the account's linked Apple ID when its credentials are reset (a password reset or change, a second factor
/// turned on or off): whoever reset them may be locking somebody out, and an Apple ID linked from a briefly held device
/// key must not outlive that. The owner links it again from the app.
/// </summary>
public static class AppleLinkReset
{
    /// <summary>Marks the user's link for removal in <paramref name="db"/>; the caller's save removes it (and tells the feed).</summary>
    public static async Task StageAsync(BlocwerkDbContext db, Guid userId)
    {
        db.AppleLinks.RemoveRange(await db.AppleLinks.Where(l => l.UserId == userId).ToListAsync());
    }
}
