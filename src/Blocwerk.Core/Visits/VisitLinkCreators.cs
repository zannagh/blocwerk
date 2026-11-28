// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Linq.Expressions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Visits;

/// <summary>
/// Whether a visitor link's maker may still invite to its wall: the wall's owner, a member, or a site admin, the rule
/// <see cref="WallVisitService.CreateAsync"/> checked when the link was made. A link and its visit end with that right,
/// so a removed member's guest loses the wall as the member did.
/// </summary>
internal static class VisitLinkCreators
{
    public static Expression<Func<WallVisitLink, bool>> StillMayInvite(BlocwerkDbContext db) =>
        l => db.Walls.IgnoreQueryFilters()
                 .Any(w => w.Id == l.WallId && (w.OwnerId == l.CreatedByUserId || w.Members.Any(m => m.UserId == l.CreatedByUserId)))
             || db.Users.IgnoreQueryFilters().Any(u => u.Id == l.CreatedByUserId && u.Role == IdentityRole.Admin);
}
