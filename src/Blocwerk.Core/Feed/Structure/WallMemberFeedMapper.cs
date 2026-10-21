// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Feed.Payloads;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Feed.Structure;

/// <summary>
/// <see cref="WallMember"/>: <c>member.changed</c> for the wall's members (the member's user id and role, nothing else: the
/// kiosk consent and PIN columns never leave the row). A subscription's audiences are fixed when it starts, so a member
/// who loses some of them also gets <c>access.revoked</c>, addressed to that user alone: removed (payload null, their views
/// leave the wall) or demoted (payload <c>{ role }</c>, the new role, so their views subscribe again with the audiences
/// that role still has and stop receiving, say, a setter's draft rows).
/// </summary>
internal sealed class WallMemberFeedMapper : IFeedMapper
{
    public IReadOnlyCollection<Type> EntityTypes { get; } = [typeof(WallMember)];

    public void Map(EntityEntry entry, FeedMapContext context)
    {
        var member = (WallMember)entry.Entity;
        var role = FeedEntryValues.Value<WallRole>(entry, nameof(WallMember.Role));
        context.Add(StructureRows.Row(FeedKinds.MemberChanged, member.WallId, member.UserId, FeedAudience.Members, new MemberPayload(role.ToString())));

        var removed = entry.State == EntityState.Deleted;
        var demoted = entry.State == EntityState.Modified && Rank(role) < Rank(FeedEntryValues.Original<WallRole>(entry, nameof(WallMember.Role)));
        if (!removed && !demoted)
        {
            return;
        }

        var revoked = StructureRows.Row(FeedKinds.AccessRevoked, member.WallId, member.WallId, FeedAudience.User, removed ? null : new MemberPayload(role.ToString()));
        revoked.AudienceUserId = member.UserId;
        context.Add(revoked);
    }

    // Capability order (owner ⊇ admin ⊇ moderator ⊇ member, see WallRole): any step down may drop an audience.
    private static int Rank(WallRole role) => role switch
    {
        WallRole.Admin => 2,
        WallRole.Moderator => 1,
        _ => 0,
    };
}
