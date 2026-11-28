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
/// <remarks>
/// The one exception is the Apple ID an account was made with by web Sign in with Apple (its identifier ends in
/// <c>__{sub}</c>, as a GitHub-made account's ends in its GitHub id). That Apple ID is the account's own origin, not a
/// later link; dropping it would make the owner's next Sign in with Apple open a new, empty account.
/// </remarks>
public static class AppleLinkReset
{
    /// <summary>Marks the user's link for removal in <paramref name="db"/>; the caller's save removes it (and tells the feed).</summary>
    public static async Task StageAsync(BlocwerkDbContext db, Guid userId)
    {
        var identifier = await db.Users.IgnoreQueryFilters().Where(u => u.Id == userId).Select(u => u.Identifier).FirstOrDefaultAsync();
        var links = await db.AppleLinks.Where(l => l.UserId == userId).ToListAsync();
        db.AppleLinks.RemoveRange(links.Where(l => identifier is null || !identifier.EndsWith("__" + l.Subject, StringComparison.Ordinal)));
    }
}
