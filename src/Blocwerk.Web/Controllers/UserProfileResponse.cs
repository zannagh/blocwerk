// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Answer of <c>GET /api/v1/users/{id}</c>: another member's profile as the web shows it to someone who shares a wall
/// with them, the identity card and the progression pane, and nothing from their settings.
/// </summary>
/// <param name="Id">The member's user id.</param>
/// <param name="Name">Their display name.</param>
/// <param name="Role">User, Admin or Guest, as the role badge shows it.</param>
/// <param name="MemberSince">When their account was made.</param>
/// <param name="AvatarUrl">Their avatar's path, or null without one.</param>
/// <param name="Progression">Their progression over the requested window.</param>
public sealed record UserProfileResponse(
    Guid Id,
    string Name,
    string Role,
    DateTimeOffset MemberSince,
    string? AvatarUrl,
    ProgressionResponse Progression);
