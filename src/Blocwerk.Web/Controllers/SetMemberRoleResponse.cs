// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>The member's role after <c>PUT /api/v1/walls/{w}/members/{userId}/role</c>.</summary>
/// <param name="UserId">The member.</param>
/// <param name="Role">Member, Moderator or Admin.</param>
public sealed record SetMemberRoleResponse(Guid UserId, string Role);
