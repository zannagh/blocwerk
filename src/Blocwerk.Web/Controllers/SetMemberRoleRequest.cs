// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>The body of <c>PUT /api/v1/walls/{w}/members/{userId}/role</c>.</summary>
public sealed class SetMemberRoleRequest
{
    /// <summary>Member, Moderator or Admin.</summary>
    public string? Role { get; set; }
}
