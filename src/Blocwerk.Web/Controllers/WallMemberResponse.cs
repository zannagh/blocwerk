// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>One row of <c>GET /api/v1/walls/{wallId}/members</c>, as the web wall page's member list shows it.</summary>
/// <param name="UserId">The member; their profile is <c>/api/v1/users/{userId}</c>.</param>
/// <param name="Name">Their display name, or "Unknown".</param>
/// <param name="AvatarUrl">Their avatar's path, or null without one.</param>
/// <param name="Role">Admin, Moderator or Member.</param>
/// <param name="IsOwner">True for the wall's owner (the web shows "Owner" in place of the role).</param>
/// <param name="JoinedAt">When they joined; the list is in this order.</param>
public sealed record WallMemberResponse(Guid UserId, string Name, string? AvatarUrl, string Role, bool IsOwner, DateTimeOffset JoinedAt);
