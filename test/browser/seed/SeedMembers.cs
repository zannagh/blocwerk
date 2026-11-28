// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Browser.Seed;

/// <summary>
/// The members wall (Rhea's), for removing a member and leaving a wall: Max is removed by Rhea, Nia leaves, Ola stays.
/// Its invite and share tokens are known to the spec, which checks that Max cannot come back with the invite while the
/// share link keeps showing the wall. No other spec touches it.
/// </summary>
internal static class SeedMembers
{
    public const string ShareToken = "browser-members-share-token";

    public const string JoinToken = "browser-members-join-token";

    public static void Add(BlocwerkDbContext db, Wall wall, params Guid[] memberIds)
    {
        wall.ShareToken = ShareToken;
        wall.JoinToken = JoinToken;
        foreach (var memberId in memberIds)
        {
            db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = memberId, Role = WallRole.Member });
        }
    }
}
