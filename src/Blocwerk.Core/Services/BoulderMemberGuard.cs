// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// The write guard of the per-user boulder marks (ratings, favourites, projects): only a member of the boulder's wall may
/// set them. The boulder page already hides the controls from guests; this guards the write path too.
/// </summary>
internal static class BoulderMemberGuard
{
    /// <summary>The refusal's message; the offline action endpoint reads it as a permanent refusal.</summary>
    public const string NotAMember = "Only wall members can do this";

    /// <summary>Throws unless <paramref name="userId"/> is a member of the wall <paramref name="boulderId"/> is on.</summary>
    public static async Task EnsureMemberAsync(BlocwerkDbContext db, Guid boulderId, Guid userId)
    {
        var wallId = await db.Boulders
            .Where(b => b.Id == boulderId)
            .Select(b => (Guid?)b.WallId)
            .FirstOrDefaultAsync();

        if (wallId == null)
        {
            throw new InvalidOperationException("Boulder not found");
        }

        var isMember = await db.WallMembers
            .AnyAsync(m => m.WallId == wallId.Value && m.UserId == userId);

        if (!isMember)
        {
            throw new InvalidOperationException(NotAMember);
        }
    }
}
