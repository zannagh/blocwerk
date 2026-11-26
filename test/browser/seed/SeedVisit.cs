// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Visits;

namespace Blocwerk.Browser.Seed;

/// <summary>
/// The visit wall (Vic's), for one-time visitor links: Mia is a plain member who makes the links, there is a published
/// boulder a visitor may open and a draft they may not see, and a link that expired an hour ago, whose token the seed
/// hands to the spec (only its hash is in the database, as for every link).
/// </summary>
internal static class SeedVisit
{
    public const string BoulderName = "Visit boulder";

    public const string DraftName = "Visit draft";

    /// <summary>Seeds the wall's member, boulders and expired link; returns the expired link's token.</summary>
    public static string Add(BlocwerkDbContext db, Guid wallId, Guid ownerId, Guid memberId)
    {
        db.WallMembers.Add(new WallMember { WallId = wallId, UserId = memberId, Role = WallRole.Member });
        db.Boulders.Add(new Boulder { WallId = wallId, Name = BoulderName, Grade = "6a", CreatedByUserId = ownerId });
        db.Boulders.Add(new Boulder { WallId = wallId, Name = DraftName, Grade = "6b", CreatedByUserId = ownerId, IsDraft = true });

        var token = VisitLinkTokens.Create();
        var made = DateTimeOffset.UtcNow.AddHours(-25);
        db.WallVisitLinks.Add(new WallVisitLink
        {
            WallId = wallId,
            CreatedByUserId = memberId,
            TokenHash = VisitLinkTokens.Hash(token),
            CreatedAt = made,
            ExpiresAt = made.AddHours(24),
        });
        return token;
    }
}
