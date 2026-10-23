// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Whose name a kiosk tablet with nobody picked may show. The tablet hangs in public, so a member is named on it only
/// once they consented to the kiosk (<see cref="WallMember.KioskConsentedAt"/>); everyone else reads as
/// <see cref="PlaceholderName"/>, with no avatar and no id. Setters stay named on the entries that are about setting
/// (<see cref="KeepsName"/>), as the wall's boulder list names them. Erased and system authors stay Ghost. The page reads
/// (<see cref="CommentService"/>, <see cref="ActivityLogService"/>) and the live feed (<c>AuthorVisibility</c>) apply this
/// one rule. A kiosk acting as a member sees what that member sees.
/// </summary>
public static class KioskBylines
{
    /// <summary>The byline of a member who has not consented to the kiosk.</summary>
    public const string PlaceholderName = "A member";

    /// <summary>True for the setter entries whose author the tablet names whatever their consent.</summary>
    public static bool KeepsName(ActivityType type) => type is ActivityType.BoulderCreated or ActivityType.BoulderRevised;

    /// <summary>The members of <paramref name="wallId"/> who consented to the kiosk.</summary>
    public static async Task<HashSet<Guid>> ConsentedAsync(BlocwerkDbContext db, Guid wallId)
    {
        var ids = await db.WallMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.WallId == wallId && m.KioskConsentedAt != null)
            .Select(m => m.UserId)
            .ToListAsync();
        return ids.ToHashSet();
    }

    /// <summary>True for a kiosk tablet bound to a wall with nobody picked: the one viewer these bylines are for.</summary>
    public static async Task<bool> IsRestingKioskAsync(ICurrentUserService users, IKioskContext? kiosk)
    {
        if (KioskViewing.ViewableWallId(kiosk) is null)
        {
            return false;
        }

        try
        {
            await users.GetCurrentUserAsync();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// The proposer of <paramref name="proposal"/> as the tablet may show them: named when they consented to the kiosk,
    /// or when they are credited on the boulder (its setters and its creator are named on the same page anyway).
    /// </summary>
    public static async Task MaskProposerAsync(BlocwerkDbContext db, GradeProposal proposal)
    {
        var boulder = await db.Boulders.AsNoTracking()
            .Where(b => b.Id == proposal.BoulderId)
            .Select(b => new { b.WallId, b.CreatedByUserId, Setters = b.Setters.Select(s => s.UserId).ToList() })
            .FirstOrDefaultAsync();
        var proposer = proposal.ProposedByUserId;
        var named = boulder is not null
            && (boulder.CreatedByUserId == proposer || boulder.Setters.Contains(proposer) || (await ConsentedAsync(db, boulder.WallId)).Contains(proposer));
        proposal.ProposedBy = Mask(proposal.ProposedBy, named);
        proposal.ProposedByUserId = proposal.ProposedBy.Id == Guid.Empty ? Guid.Empty : proposer;
    }

    /// <summary>
    /// The author as the tablet may show them: <paramref name="user"/> itself when <paramref name="named"/>, or Ghost
    /// anyway, otherwise a placeholder user that carries no id, no avatar and no personal data.
    /// </summary>
    public static User Mask(User user, bool named)
    {
        if (named || user.IsDeleted || GhostUser.Is(user.Id))
        {
            return user;
        }

        return new User { Id = Guid.Empty, Identifier = string.Empty, DisplayName = PlaceholderName };
    }
}
